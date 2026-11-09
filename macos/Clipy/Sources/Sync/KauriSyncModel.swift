//
//  KauriSyncModel.swift
//
//  Records, clocks and order keys of Kauri Sync v1 (docs/sync.md §4-5), and
//  the state this Mac keeps between launches.
//

// swiftlint:disable identifier_name
import Foundation
import Security

// MARK: - JSON

enum JSONValue: Codable, Equatable {
    case null
    case bool(Bool)
    case int(Int64)
    case double(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    init(from decoder: Decoder) throws {
        let c = try decoder.singleValueContainer()
        if c.decodeNil() {
            self = .null
        } else if let v = try? c.decode(Bool.self) {
            self = .bool(v)
        } else if let v = try? c.decode(Int64.self) {
            self = .int(v)
        } else if let v = try? c.decode(Double.self) {
            self = .double(v)
        } else if let v = try? c.decode(String.self) {
            self = .string(v)
        } else if let v = try? c.decode([JSONValue].self) {
            self = .array(v)
        } else {
            self = .object(try c.decode([String: JSONValue].self))
        }
    }

    func encode(to encoder: Encoder) throws {
        var c = encoder.singleValueContainer()
        switch self {
        case .null: try c.encodeNil()
        case .bool(let v): try c.encode(v)
        case .int(let v): try c.encode(v)
        case .double(let v): try c.encode(v)
        case .string(let v): try c.encode(v)
        case .array(let v): try c.encode(v)
        case .object(let v): try c.encode(v)
        }
    }

    var string: String? { if case .string(let v) = self { v } else { nil } }
    var bool: Bool? { if case .bool(let v) = self { v } else { nil } }
    var int: Int64? {
        switch self {
        case .int(let v): v
        case .double(let v): Int64(exactly: v.rounded())
        default: nil
        }
    }
}

// MARK: - Hybrid logical clock (§5.1)

struct HLC: Comparable, Codable, Hashable {
    var wall: Int64
    var counter: Int
    var device: String

    static func < (a: HLC, b: HLC) -> Bool {
        (a.wall, a.counter, a.device) < (b.wall, b.counter, b.device)
    }

    init(wall: Int64, counter: Int, device: String) {
        self.wall = wall
        self.counter = counter
        self.device = device
    }

    init?(_ json: JSONValue?) {
        guard case .array(let a)? = json, a.count == 3, let w = a[0].int, let c = a[1].int, let d = a[2].string else { return nil }
        self.init(wall: w, counter: Int(c), device: d)
    }

    var json: JSONValue { .array([.int(wall), .int(Int64(counter)), .string(device)]) }
}

struct HLCClock: Codable {
    var wall: Int64 = 0
    var counter: Int = 0

    mutating func tick(device: String, now: Date = Date()) -> HLC {
        let ms = Int64(now.timeIntervalSince1970 * 1000)
        if ms > wall {
            wall = ms
            counter = 0
        } else {
            counter += 1
            if counter > 65_535 {
                wall += 1
                counter = 0
            }
        }
        return HLC(wall: wall, counter: counter, device: device)
    }

    mutating func observe(_ h: HLC) {
        if h.wall > wall {
            wall = h.wall
            counter = h.counter
        } else if h.wall == wall {
            counter = max(counter, h.counter)
        }
    }
}

// MARK: - Record body (§4.2)

struct RecordBody: Equatable {
    var kind: String
    var fields: [String: Field] = [:]
    var deleted: HLC?
    /// Top-level keys this version does not know; written back unchanged.
    var extra: [String: JSONValue] = [:]

    struct Field: Equatable {
        var value: JSONValue
        var hlc: HLC
    }

    init(kind: String) { self.kind = kind }

    init(json data: Data) throws {
        guard case .object(var o) = try JSONDecoder().decode(JSONValue.self, from: data),
              let kind = o.removeValue(forKey: "k")?.string else { throw KauriCryptoError.badLength }
        self.kind = kind
        if case .object(let f)? = o.removeValue(forKey: "f") {
            for (name, pair) in f {
                guard case .array(let a) = pair, a.count == 2, let h = HLC(a[1]) else { continue }
                fields[name] = Field(value: a[0], hlc: h)
            }
        }
        deleted = HLC(o.removeValue(forKey: "d"))
        extra = o
    }

    func json() throws -> Data {
        var o = extra
        o["k"] = .string(kind)
        o["f"] = .object(fields.mapValues { .array([$0.value, $0.hlc.json]) })
        o["d"] = deleted?.json ?? .null
        let encoder = JSONEncoder()
        encoder.outputFormatting = .sortedKeys
        return try encoder.encode(JSONValue.object(o))
    }

    /// True if the deletion is newer than every field.
    var isDeleted: Bool {
        guard let d = deleted else { return false }
        return fields.values.allSatisfy { $0.hlc < d }
    }

    var newest: HLC? { (fields.values.map(\.hlc) + [deleted].compactMap { $0 }).max() }

    subscript(name: String) -> JSONValue? { fields[name]?.value }

    /// Sets a field to a new value at `hlc`, if the value differs.
    @discardableResult
    mutating func set(_ name: String, _ value: JSONValue, at hlc: () -> HLC) -> Bool {
        guard fields[name]?.value != value || isDeleted else { return false }
        fields[name] = Field(value: value, hlc: hlc())
        return true
    }

    /// Field by field, the newer side wins (§5.1).
    func merged(with other: RecordBody) -> RecordBody {
        var out = self
        for (name, f) in other.fields where out.fields[name].map({ $0.hlc < f.hlc }) ?? true {
            out.fields[name] = f
        }
        if let d = other.deleted, out.deleted.map({ $0 < d }) ?? true {
            out.deleted = d
        }
        out.extra.merge(other.extra) { _, theirs in theirs }
        return out
    }
}

// MARK: - Fractional index (§5.2)

enum OrderKey {
    static let digits = Array("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".utf8)

    private static func digit(_ s: [UInt8], _ i: Int) -> Int {
        guard i < s.count, let d = digits.firstIndex(of: s[i]) else { return 0 }
        return d
    }

    /// A key strictly between `a` and `b` (nil: no bound). Never ends in "0",
    /// so there is always room for another key before it.
    static func between(_ a: String?, _ b: String?) -> String {
        let a = Array((a ?? "").utf8)
        let b = b.map { Array($0.utf8) }
        var out = [UInt8]()
        var i = 0
        var bounded = b != nil
        while i < 64 {
            let da = digit(a, i)
            let db = bounded ? digit(b!, i) : digits.count
            if db - da > 1 {
                out.append(digits[(da + db) / 2])
                return (String(bytes: out, encoding: .utf8) ?? "")
            }
            out.append(digits[da])
            if db - da == 1 { bounded = false }
            i += 1
        }
        out.append(digits[digits.count / 2])
        return (String(bytes: out, encoding: .utf8) ?? "")
    }

    /// Keys for `items` in their local order. Keeps the longest run of keys
    /// that are already in order and gives new keys to the others.
    static func assign(_ keys: [String?]) -> [String] {
        // Longest increasing subsequence of the existing keys.
        var tails = [Int]()
        var prev = [Int](repeating: -1, count: keys.count)
        for (i, k) in keys.enumerated() {
            guard let k else { continue }
            var lo = 0, hi = tails.count
            while lo < hi {
                let mid = (lo + hi) / 2
                if keys[tails[mid]]! < k { lo = mid + 1 } else { hi = mid }
            }
            if lo > 0 { prev[i] = tails[lo - 1] }
            if lo == tails.count { tails.append(i) } else { tails[lo] = i }
        }
        var keep = Set<Int>()
        var j = tails.last ?? -1
        while j >= 0 {
            keep.insert(j)
            j = prev[j]
        }
        var out = [String](repeating: "", count: keys.count)
        var lower: String?
        for i in keys.indices {
            if keep.contains(i) {
                out[i] = keys[i]!
            } else {
                let upper = ((i + 1)..<keys.count).first(where: keep.contains).map { keys[$0]! }
                out[i] = between(lower, upper)
            }
            lower = out[i]
        }
        return out
    }
}

// MARK: - Persisted state

/// What this Mac remembers about one record.
struct SyncedRecord: Codable {
    var kind: String
    /// The local ID: a folder or snippet UUID, or a clip's text hash.
    var local: String
    var version: Int64 = 0
    var epoch: UInt32 = 0
    var dirty = false
    var body: Data
    /// Clips only: the local history item. Empty once it left the local
    /// history without being deleted, so it is not added back.
    var history: String?
    /// Folders and snippets: true while the item exists in the local
    /// database. Only such items can be deleted by deleting them locally.
    var materialized: Bool?
}

struct SyncState: Codable {
    var server: String = ""
    var deviceName: String = Host.current().localizedName ?? "Mac"
    var syncsHistory = false
    var enabled = false
    var since: Int64 = 0
    var clock = HLCClock()
    var records: [String: SyncedRecord] = [:]

    static var fileURL: URL {
        let dir = (try? FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true))
            ?? FileManager.default.temporaryDirectory
        let app = dir.appending(path: Bundle.main.bundleIdentifier ?? "Kauri")
        try? FileManager.default.createDirectory(at: app, withIntermediateDirectories: true)
        return app.appending(path: "sync.json")
    }

    static func load() -> SyncState {
        guard let data = try? Data(contentsOf: fileURL), let s = try? JSONDecoder().decode(SyncState.self, from: data) else { return SyncState() }
        return s
    }

    func save() {
        guard let data = try? JSONEncoder().encode(self) else { return }
        try? data.write(to: SyncState.fileURL, options: [.atomic, .completeFileProtection])
    }
}

// MARK: - Keychain

/// The chain secret and the device keys, in one Keychain item.
struct SyncSecrets: Codable {
    var chainSecret: Data
    var sign: Data
    var x25519: Data
    var mlkemSeed: Data?

    private static let service = "io.github.vizzletf.kauri.sync"

    var device: KauriDevice {
        get throws {
            KauriDevice(sign: try .init(rawRepresentation: sign), x25519: try .init(rawRepresentation: x25519), mlkemSeed: mlkemSeed)
        }
    }

    init(chainSecret: Data, device: KauriDevice) {
        self.chainSecret = chainSecret
        sign = device.sign.rawRepresentation
        x25519 = device.x25519.rawRepresentation
        mlkemSeed = device.mlkemSeed
    }

    static func load() -> SyncSecrets? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecReturnData as String: true
        ]
        var out: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &out) == errSecSuccess, let data = out as? Data else { return nil }
        return try? JSONDecoder().decode(SyncSecrets.self, from: data)
    }

    func save() throws {
        let data = try JSONEncoder().encode(self)
        SyncSecrets.delete()
        let item: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: SyncSecrets.service,
            kSecAttrAccount as String: "chain",
            kSecAttrLabel as String: "Kauri Clip sync keys",
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
            kSecValueData as String: data
        ]
        let status = SecItemAdd(item as CFDictionary, nil)
        guard status == errSecSuccess else { throw NSError(domain: NSOSStatusErrorDomain, code: Int(status)) }
    }

    static func delete() {
        SecItemDelete([kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service] as CFDictionary)
    }
}

// swiftlint:enable identifier_name
