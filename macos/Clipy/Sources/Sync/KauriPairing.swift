//
//  KauriPairing.swift
//
//  Pairing (docs/sync.md §8): SPAKE2 (RFC 9382) over edwards25519, with the
//  group operations of KauriCurve.swift.
//

// swiftlint:disable identifier_name
import CryptoKit
import Foundation

struct KauriPairing {
    static let crockford = Array("0123456789ABCDEFGHJKMNPQRSTVWXYZ")
    private static let pointM = Data(hex: "d048032c6ea0b6d697ddc2e86bda85a33adac920f1bf18e1b0c6d166a5cecdaf")
    private static let pointN = Data(hex: "d3bfb518f44f3430f29d0c92af503865a1ed3281dc69b35dd868ba85f886c4ab")

    let slot: String
    let existing: Bool
    let message: Data
    private let w: Data
    private let x: Data

    struct Keys {
        let confirmB: Data
        let boxKey: Data
        let number: Int

        func checkConfirm(_ c: Data) -> Bool {
            c.count == confirmB.count && zip(c, confirmB).reduce(0) { $0 | ($1.0 ^ $1.1) } == 0
        }
    }

    static func newCode() -> String {
        let r = Kauri.random(12)
        var s = ""
        for (i, b) in r.enumerated() {
            if i == 4 || i == 8 { s += "-" }
            s.append(crockford[Int(b & 31)])
        }
        return s
    }

    /// Upper-cases a typed code, maps O to 0 and I, L to 1, drops separators.
    static func normalize(_ code: String) -> String? {
        var out = ""
        for ch in code.uppercased() {
            switch ch {
            case "-", " ": continue
            case "O": out.append("0")
            case "I", "L": out.append("1")
            default:
                guard crockford.contains(ch) else { return nil }
                out.append(ch)
            }
        }
        return out.count == 12 ? out : nil
    }

    static func slot(for code: String) -> String {
        Kauri.sha256(Data("kauri-pair-slot-v1\u{0}\(code.prefix(4))".utf8)).hexString
    }

    /// `code` must be normalized. `existing` is true on the device already in the chain.
    init(code: String, existing: Bool, seed: Data? = nil) {
        slot = KauriPairing.slot(for: code)
        self.existing = existing
        let wHash = Data(SHA512.hash(data: Data("kauri-pair-v1\u{0}\(slot)\u{0}\(code.dropFirst(4))".utf8)))
        w = Sodium.reduce(wHash)
        x = Sodium.reduce(seed ?? Kauri.random(64))
        let mask = existing ? KauriPairing.pointM : KauriPairing.pointN
        message = Sodium.add(Sodium.mul(w, mask)!, Sodium.base(x))!
    }

    func finish(peer: Data) throws -> Keys {
        guard peer.count == 32, Sodium.isValidPoint(peer) else { throw KauriCryptoError.badPoint }
        let peerMask = existing ? KauriPairing.pointN : KauriPairing.pointM
        guard let t = Sodium.sub(peer, Sodium.mul(w, peerMask)!),
              let k = Sodium.mul(Sodium.scalarMul(Sodium.eight, x), t) else { throw KauriCryptoError.badPoint }
        let (pA, pB) = existing ? (message, peer) : (peer, message)
        var tt = Data()
        for part in [Data("kauri-pair-existing".utf8), Data("kauri-pair-new".utf8), pA, pB, k, Data(w.reversed())] {
            tt.append(withUnsafeBytes(of: UInt64(part.count).littleEndian) { Data($0) })
            tt.append(part)
        }
        let h = Kauri.sha256(tt)
        let ke = h.prefix(16), ka = h.suffix(16)
        let kc = HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: ka), info: Data("ConfirmationKeys\(slot)".utf8), outputByteCount: 32)
            .withUnsafeBytes { Data($0) }
        let confirm = Kauri.hmac(kc.suffix(16), tt)
        let box = Kauri.hkdf(Data(ke), salt: Data("kauri-pair-v1".utf8), info: Data("box".utf8))
        let v = Kauri.hkdf(Data(ke), salt: Data("kauri-pair-v1".utf8), info: Data("verify".utf8), count: 4)
        let number = Int(v.reduce(UInt32(0)) { $0 << 8 | UInt32($1) } % 10000)
        return Keys(confirmB: confirm, boxKey: box, number: number)
    }

    static func sealJoin(_ keys: Keys, slot: String, info: Data) -> Data {
        Kauri.seal(key: keys.boxKey, plaintext: info, aad: Data("kauri-pair-v1/join\(slot)".utf8))
    }

    static func openJoin(_ keys: Keys, slot: String, box: Data) throws -> Data {
        try Kauri.open(key: keys.boxKey, sealed: box, aad: Data("kauri-pair-v1/join\(slot)".utf8))
    }

    static func sealApproval(_ keys: Keys, slot: String, secret: Data, nonce: Data? = nil) -> Data {
        Kauri.seal(key: keys.boxKey, plaintext: secret, aad: Data("kauri-pair-v1/approve\(slot)".utf8), nonce: nonce)
    }

    static func openApproval(_ keys: Keys, slot: String, box: Data) throws -> Data {
        let s = try Kauri.open(key: keys.boxKey, sealed: box, aad: Data("kauri-pair-v1/approve\(slot)".utf8))
        guard s.count == 32 else { throw KauriCryptoError.badLength }
        return s
    }
}

/// The libsodium-style names KauriPairing uses.
private enum Sodium {
    static let eight: Data = { var d = Data(count: 32); d[0] = 8; return d }()

    static func reduce(_ wide: Data) -> Data { Ed25519Group.scalarReduce(wide) }
    static func mul(_ n: Data, _ p: Data) -> Data? { Ed25519Group.mult(n, p) }
    static func base(_ n: Data) -> Data { Ed25519Group.baseMult(n) }
    static func add(_ p: Data, _ q: Data) -> Data? { Ed25519Group.add(p, q) }
    static func sub(_ p: Data, _ q: Data) -> Data? { Ed25519Group.sub(p, q) }
    static func scalarMul(_ a: Data, _ b: Data) -> Data { Ed25519Group.scalarMul(a, b) }
    static func isValidPoint(_ p: Data) -> Bool { Ed25519Group.isValidPoint(p) }
}

extension Data {
    init(hex: String) {
        var d = Data(capacity: hex.count / 2)
        var it = hex.makeIterator()
        while let a = it.next(), let b = it.next() {
            d.append(UInt8(String([a, b]), radix: 16)!)
        }
        self = d
    }

    var hexString: String { map { String(format: "%02x", $0) }.joined() }
}

// swiftlint:enable identifier_name
