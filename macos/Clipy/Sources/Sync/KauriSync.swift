//
//  KauriSync.swift
//
//  Keeps snippets, folders and (if turned on) text clips in sync with a Kauri
//  Sync server. The local database stays the source of truth for the app; this
//  class compares it with the records it knows, pushes local edits and applies
//  remote ones (docs/sync.md).
//

// swiftlint:disable file_length type_body_length identifier_name
import AppKit
import Combine
import Dependencies
import Foundation
import SQLiteData

extension Notification.Name {
    /// Posted with the history ID as `object` when the user deletes one clip.
    static let kauriHistoryDeleted = Notification.Name("KauriHistoryDeleted")
}

@MainActor
final class KauriSync: ObservableObject {
    static let shared = KauriSync()

    enum Status: Equatable {
        case off
        case connecting
        case syncing
        case synced
        case offline(String)
    }

    struct DeviceRow: Identifiable, Equatable {
        let id: String
        let name: String
        let isThisDevice: Bool
        let isPostQuantum: Bool
    }

    @Published private(set) var status: Status = .off
    @Published private(set) var devices: [DeviceRow] = []
    @Published private(set) var state = SyncState.load()
    /// Set when the chain moved and the user should write down the new code.
    @Published var newSyncCode: String?
    /// Set when another device removed this one.
    @Published var wasRemoved = false

    @Dependency(\.defaultDatabase)
    private var database
    @Dependency(\.snippetRepository)
    private var snippetRepository
    @Dependency(\.pasteboardHistoryRepository)
    private var historyRepository

    private var secrets: SyncSecrets?
    private var chain: KauriChain?
    private var device: KauriDevice?
    private var dataKeys: [UInt32: Data] = [:]
    private var epoch: UInt32 = 0
    private var loop: Task<Void, Never>?
    private var observers = Set<AnyCancellable>()
    private var deletedHistory = Set<String>()
    private var running = false
    private var again = false

    static let historyCap = 500
    private static let kinds = ["folder", "snippet", "clip"]

    private init() {}

    var isOn: Bool { state.enabled && secrets != nil }
    var syncCode: String? { chain?.words }

    // MARK: - Lifecycle

    func start() {
        secrets = SyncSecrets.load()
        guard state.enabled, let secrets else {
            status = .off
            return
        }
        do {
            device = try secrets.device
            chain = KauriChain(secret: secrets.chainSecret)
        } catch {
            status = .offline(error.localizedDescription)
            return
        }
        observeLocalChanges()
        loop?.cancel()
        loop = Task { await run() }
    }

    #if DEBUG
    /// KAURI_SYNC_TEST_SERVER and KAURI_SYNC_TEST_WORDS: join that chain at
    /// launch. The app then reads no clipboard (see AppDelegate).
    static var testEnvironment: (server: String, words: String)? {
        let env = ProcessInfo.processInfo.environment
        guard let server = env["KAURI_SYNC_TEST_SERVER"], let words = env["KAURI_SYNC_TEST_WORDS"] else { return nil }
        return (server, words)
    }

    func startForTest() {
        guard let test = KauriSync.testEnvironment else { return }
        start()
        guard !isOn else { return }
        Task {
            do {
                try await join(server: test.server, words: test.words)
            } catch {
                status = .offline(error.localizedDescription)
                print("KauriSync test join failed: \(error)")
            }
        }
    }
    #endif

    func stop() {
        loop?.cancel()
        loop = nil
        observers.removeAll()
        status = .off
    }

    private func observeLocalChanges() {
        observers.removeAll()
        snippetRepository.observeFolderDetails()
            .dropFirst()
            .debounce(for: .seconds(2), scheduler: DispatchQueue.main)
            .sink { [weak self] _ in Task { await self?.sync(pull: false) } }
            .store(in: &observers)
        historyRepository.observeHistories()
            .dropFirst()
            .debounce(for: .seconds(2), scheduler: DispatchQueue.main)
            .sink { [weak self] _ in
                guard self?.state.syncsHistory == true else { return }
                Task { await self?.sync(pull: false) }
            }
            .store(in: &observers)
        NotificationCenter.default.publisher(for: .kauriHistoryDeleted)
            .compactMap { $0.object as? String }
            .sink { [weak self] id in self?.deletedHistory.insert(id) }
            .store(in: &observers)
    }

    private func client() -> KauriSyncClient? {
        guard let chain, let device, let url = URL(string: state.server) else { return nil }
        return KauriSyncClient(server: url, chain: chain, device: device)
    }

    /// Listens for events and syncs on each change, reconnecting with backoff.
    private func run() async {
        var delay: UInt64 = 2
        while !Task.isCancelled, isOn, let client = client() {
            do {
                status = .connecting
                try await loadDataKey(client)
                await refreshDevices()
                for try await (event, _) in client.events() {
                    delay = 2
                    switch event {
                    case "ready", "changed":
                        await sync(pull: true)
                    case "devices":
                        await refreshDevices()
                    case "keyring":
                        try await loadDataKey(client)
                    case "removed":
                        forget(removed: true)
                        return
                    case "forwarded":
                        try await followForward()
                    default:
                        break
                    }
                }
            } catch let error as KauriAPIError where error.status == 403 {
                forget(removed: true)
                return
            } catch let error as KauriAPIError where error.status == 410 && error.message == "forwarded" {
                do { try await followForward() } catch { status = .offline(error.localizedDescription) }
                continue
            } catch is CancellationError {
                return
            } catch {
                if Task.isCancelled { return }
                status = .offline(error.localizedDescription)
            }
            try? await Task.sleep(nanoseconds: delay * 1_000_000_000)
            delay = min(delay * 2, 300)
        }
    }

    private func loadDataKey(_ client: KauriSyncClient) async throws {
        let (e, key) = try await client.dataKey()
        epoch = e
        dataKeys[e] = key
    }

    // MARK: - Turning sync on and off

    private static func checkServer(_ server: String) throws -> URL {
        guard let url = URL(string: server.trimmingCharacters(in: .whitespaces)), let host = url.host,
              url.scheme == "https" || (url.scheme == "http" && ["localhost", "127.0.0.1", "::1"].contains(host))
        else { throw KauriAPIError(status: 0, message: String(localized: "The server must be an https:// address.")) }
        return url
    }

    /// Creates a chain on `server` with this Mac and turns sync on.
    func createChain(server: String) async throws {
        let url = try KauriSync.checkServer(server)
        let chain = KauriChain.new()
        let device = KauriDevice.new()
        let client = KauriSyncClient(server: url, chain: chain, device: device)
        let key = try await client.createChain(deviceName: state.deviceName)
        try enable(server: url, chain: chain, device: device)
        dataKeys = [1: key]
        epoch = 1
        newSyncCode = chain.words
        start()
    }

    /// Joins the chain of `words` on `server` (§8.2).
    func join(server: String, words: String) async throws {
        let url = try KauriSync.checkServer(server)
        let chain = try KauriChain(words: words)
        let device = KauriDevice.new()
        try await KauriSyncClient(server: url, chain: chain, device: device).joinWithSyncCode(deviceName: state.deviceName)
        try enable(server: url, chain: chain, device: device)
        start()
    }

    private func enable(server: URL, chain: KauriChain, device: KauriDevice) throws {
        try SyncSecrets(chainSecret: chain.secret, device: device).save()
        state.server = server.absoluteString
        state.enabled = true
        state.since = 0
        // Everything local is new to the chain.
        for (id, r) in state.records {
            state.records[id]?.version = 0
            state.records[id]?.dirty = !((try? RecordBody(json: r.body))?.isDeleted ?? true)
        }
        state.save()
        self.chain = chain
        self.device = device
    }

    /// Leaves the chain. Local data stays.
    func turnOff() async {
        if let client = client(), let device {
            try? await client.removeDevice(id: Kauri.b64(device.id))
        }
        forget(removed: false)
    }

    private func forget(removed: Bool) {
        stop()
        SyncSecrets.delete()
        secrets = nil
        chain = nil
        device = nil
        dataKeys = [:]
        devices = []
        let server = state.server, history = state.syncsHistory
        state = SyncState()
        state.server = server
        state.syncsHistory = history
        state.save()
        wasRemoved = removed
    }

    func setSyncsHistory(_ on: Bool) {
        state.syncsHistory = on
        state.save()
        Task { await sync(pull: false) }
    }

    func setServer(_ server: String) {
        state.server = server
        state.save()
    }

    // MARK: - Devices

    func refreshDevices() async {
        guard let client = client(), let chain, let device else { return }
        guard let list = try? await client.devices() else { return }
        let me = Kauri.b64(device.id)
        devices = list.filter { $0.removed == nil }.map { d in
            let id = Kauri.unb64(d.id) ?? Data()
            let name = Kauri.unb64(d.name).flatMap { chain.openName(deviceID: id, sealed: $0) } ?? "?"
            return DeviceRow(id: d.id, name: name, isThisDevice: d.id == me, isPostQuantum: !(d.mlkem ?? "").isEmpty)
        }
        .sorted { ($0.isThisDevice ? 0 : 1, $0.name) < ($1.isThisDevice ? 0 : 1, $1.name) }
    }

    /// Removes another device and moves the chain to a new sync code (§9).
    func removeDevice(id: String) async throws {
        guard let old = client(), let device, let url = URL(string: state.server) else { return }
        try await old.removeDevice(id: id)
        let remaining = try await old.devices().filter { $0.removed == nil && $0.id != Kauri.b64(device.id) && $0.id != id }

        let next = KauriChain.new()
        let nextClient = KauriSyncClient(server: url, chain: next, device: device)
        let key = try await nextClient.createChain(deviceName: state.deviceName)
        loop?.cancel()
        rekey(to: next)
        chain = next
        dataKeys = [1: key]
        epoch = 1
        try await push(nextClient)

        let envelopes = try remaining.map { d -> (Data, Data) in
            guard let sign = Kauri.unb64(d.sign), let x = Kauri.unb64(d.x25519) else { throw KauriCryptoError.badLength }
            let r = KauriRecipient(id: Kauri.sha256(sign).prefix(16), x25519: x, mlkem: Kauri.unb64(d.mlkem ?? "") ?? Data())
            return (r.id, try KauriEnvelope.seal(chainID: old.chain.id, epoch: 0, to: r, payload: next.secret))
        }
        if !envelopes.isEmpty {
            try await old.forward(envelopes: envelopes.map { (deviceID: $0.0, envelope: $0.1) })
        }
        try SyncSecrets(chainSecret: next.secret, device: device).save()
        secrets = SyncSecrets.load()
        newSyncCode = next.words
        start()
    }

    /// Another device moved the chain: open our envelope and join the new one.
    private func followForward() async throws {
        guard let old = client(), let device, let url = URL(string: state.server) else { return }
        let secret = try await old.forwardedSecret()
        let next = KauriChain(secret: secret)
        try await KauriSyncClient(server: url, chain: next, device: device).joinWithSyncCode(deviceName: state.deviceName)
        try SyncSecrets(chainSecret: secret, device: device).save()
        secrets = SyncSecrets.load()
        rekey(to: next)
        chain = next
        dataKeys = [:]
        newSyncCode = next.words
        start()
    }

    /// Moves every record to the IDs of `next`; all of them need uploading.
    private func rekey(to next: KauriChain) {
        var map = [String: String]()
        var records = [String: SyncedRecord]()
        for (id, r) in state.records {
            guard let body = try? RecordBody(json: r.body), !body.isDeleted else { continue }
            map[id] = Kauri.b64(next.recordID(kind: r.kind, localUUID: r.local))
        }
        for (id, r) in state.records {
            guard let newID = map[id], var body = try? RecordBody(json: r.body) else { continue }
            if let f = body.fields["folder"], let folder = f.value.string {
                body.fields["folder"]?.value = .string(map[folder] ?? folder)
            }
            var n = r
            n.body = (try? body.json()) ?? r.body
            n.version = 0
            n.dirty = true
            records[newID] = n
        }
        state.records = records
        state.since = 0
        state.save()
    }

    // MARK: - Pairing (§8.1)

    struct PairingRequest {
        let code: String
        fileprivate let pairing: KauriPairing
    }

    struct PairingJoin {
        let name: String
        let number: Int
        fileprivate let keys: KauriPairing.Keys
        fileprivate let slot: String
        fileprivate let info: KauriDeviceInfo
    }

    /// On this (existing) device: shows a code for a new device.
    func openPairing() async throws -> PairingRequest {
        guard let client = client() else { throw KauriCryptoError.badLength }
        let code = KauriPairing.newCode()
        let pairing = KauriPairing(code: KauriPairing.normalize(code)!, existing: true)
        try await client.openPairing(slot: pairing.slot, message: pairing.message)
        return PairingRequest(code: code, pairing: pairing)
    }

    /// Waits until the new device joins the slot and checks its confirmation.
    func waitForJoin(_ request: PairingRequest) async throws -> PairingJoin {
        guard let url = URL(string: state.server) else { throw KauriCryptoError.badLength }
        let slot = request.pairing.slot
        while true {
            try Task.checkCancellation()
            let s = try await KauriSyncClient.pairingState(server: url, slot: slot)
            if s.joined == true, let bmsg = s.bmsg.flatMap(Kauri.unb64), let confirm = s.confirm.flatMap(Kauri.unb64),
               let box = s.box.flatMap(Kauri.unb64) {
                let keys = try request.pairing.finish(peer: bmsg)
                guard keys.checkConfirm(confirm) else {
                    try? await client()?.closePairing(slot: slot)
                    throw KauriCryptoError.badConfirmation
                }
                let info = try JSONDecoder().decode(KauriDeviceInfo.self, from: KauriPairing.openJoin(keys, slot: slot, box: box))
                return PairingJoin(name: info.name, number: keys.number, keys: keys, slot: slot, info: info)
            }
            try await Task.sleep(nanoseconds: 2_000_000_000)
        }
    }

    func allow(_ join: PairingJoin) async throws {
        guard let client = client(), let chain, let key = dataKeys[epoch] else { throw KauriCryptoError.badLength }
        guard let sign = Kauri.unb64(join.info.sign) else { throw KauriCryptoError.badLength }
        var info = join.info
        info.name = Kauri.b64(chain.sealName(deviceID: Kauri.sha256(sign).prefix(16), name: String(join.name.prefix(64))))
        try await client.addDevice(info, epoch: epoch, dataKey: key)
        try await client.approvePairing(slot: join.slot, box: KauriPairing.sealApproval(join.keys, slot: join.slot, secret: chain.secret))
        await refreshDevices()
    }

    func deny(_ join: PairingJoin) async {
        try? await client()?.closePairing(slot: join.slot)
    }

    func cancelPairing(_ request: PairingRequest) async {
        try? await client()?.closePairing(slot: request.pairing.slot)
    }

    struct PendingJoin {
        let number: Int
        fileprivate let keys: KauriPairing.Keys
        fileprivate let slot: String
        fileprivate let device: KauriDevice
        fileprivate let server: URL
    }

    /// On the new device: answers the slot of `code`.
    func join(server: String, code: String) async throws -> PendingJoin {
        let url = try KauriSync.checkServer(server)
        guard let code = KauriPairing.normalize(code) else { throw KauriCryptoError.badCode }
        let slot = KauriPairing.slot(for: code)
        let s = try await KauriSyncClient.pairingState(server: url, slot: slot)
        guard let pA = s.msg.flatMap(Kauri.unb64) else { throw KauriCryptoError.badCode }
        let pairing = KauriPairing(code: code, existing: false)
        let keys = try pairing.finish(peer: pA)
        let device = KauriDevice.new()
        let r = device.recipient
        let info = KauriDeviceInfo(sign: Kauri.b64(device.publicKey), x25519: Kauri.b64(r.x25519), mlkem: Kauri.b64(r.mlkem), name: state.deviceName)
        let box = KauriPairing.sealJoin(keys, slot: slot, info: try JSONEncoder().encode(info))
        try await KauriSyncClient.joinPairing(server: url, slot: slot, message: pairing.message, confirm: keys.confirmB, box: box)
        return PendingJoin(number: keys.number, keys: keys, slot: slot, device: device, server: url)
    }

    /// Waits for the other device to allow this one, then turns sync on.
    func waitForApproval(_ join: PendingJoin) async throws {
        while true {
            try Task.checkCancellation()
            let s = try await KauriSyncClient.pairingState(server: join.server, slot: join.slot)
            if let box = s.approval.flatMap(Kauri.unb64), !box.isEmpty {
                let secret = try KauriPairing.openApproval(join.keys, slot: join.slot, box: box)
                try enable(server: join.server, chain: KauriChain(secret: secret), device: join.device)
                start()
                return
            }
            try await Task.sleep(nanoseconds: 2_000_000_000)
        }
    }

    // MARK: - Sync

    /// Pulls (if asked), compares the local data with the records and pushes.
    func sync(pull: Bool) async {
        guard isOn, let client = client() else { return }
        if running {
            again = true
            return
        }
        running = true
        defer { running = false }
        repeat {
            again = false
            status = .syncing
            do {
                if dataKeys[epoch] == nil { try await loadDataKey(client) }
                if pull || state.since == 0 { try await self.pull(client) }
                scan()
                try await push(client)
                status = .synced
            } catch let error as KauriAPIError where error.status == 403 {
                forget(removed: true)
                return
            } catch let error as KauriAPIError where error.status == 410 && error.message == "forwarded" {
                try? await followForward()
                return
            } catch {
                status = .offline(error.localizedDescription)
            }
            state.save()
        } while again
    }

    private func pull(_ client: KauriSyncClient) async throws {
        let full = state.since == 0
        var cursor = state.since
        var records = [KauriRecordOut]()
        while true {
            let page: KauriChanges
            do {
                page = try await client.changes(since: cursor)
            } catch let error as KauriAPIError where error.status == 410 && error.message == "resync" {
                state.since = 0
                return try await pull(client)
            }
            records += page.records
            cursor = max(cursor, page.version)
            if !page.more { break }
        }
        // From here to the end nothing awaits, so no local edit can slip in
        // between reading the database and writing the merged records to it.
        scan()
        var changed = false
        var seen = Set<String>()
        for r in records {
            seen.insert(r.id)
            if try merge(r) { changed = true }
        }
        state.since = cursor
        if full {
            // Records the server no longer has were deleted long ago (§5.3).
            for (id, r) in state.records where !seen.contains(id) && !r.dirty && r.version > 0 {
                state.records[id]?.body = (try? tombstone(r).json()) ?? r.body
                changed = true
            }
        }
        if changed { applyAll() }
    }

    /// Merges one remote record into the state. Returns true if it changed.
    private func merge(_ r: KauriRecordOut) throws -> Bool {
        guard let chain, let rid = Kauri.unb64(r.id), let sealed = Kauri.unb64(r.body) else { return false }
        if let mine = state.records[r.id], r.version <= mine.version { return false }
        guard let key = dataKeys[r.epoch] else { return false } // an epoch this device cannot open
        guard let plain = try? chain.openRecord(dataKey: key, epoch: r.epoch, recordID: rid, body: sealed),
              let remote = try? RecordBody(json: plain), KauriSync.kinds.contains(remote.kind) else { return false }
        if let n = remote.newest { state.clock.observe(n) }
        guard var mine = state.records[r.id] else {
            guard let local = localID(of: remote) else { return false }
            state.records[r.id] = SyncedRecord(kind: remote.kind, local: local, version: r.version, epoch: r.epoch, body: plain)
            return true
        }
        let old = try RecordBody(json: mine.body)
        let merged = old.merged(with: remote)
        mine.version = r.version
        mine.epoch = r.epoch
        mine.dirty = mine.dirty && merged != remote
        mine.body = try merged.json()
        state.records[r.id] = mine
        return merged != old
    }

    private func localID(of body: RecordBody) -> String? {
        switch body.kind {
        case "clip":
            body["text"]?.string.map { Kauri.sha256(Data($0.utf8)).hexString }
        default:
            body.extra["u"]?.string.flatMap { UUID(uuidString: $0) }.map { $0.uuidString.lowercased() }
        }
    }

    private func tombstone(_ r: SyncedRecord) throws -> RecordBody {
        var body = try RecordBody(json: r.body)
        if !body.isDeleted { body.deleted = tick() }
        return body
    }

    private func tick() -> HLC {
        state.clock.tick(device: device.map { Kauri.b64($0.id) } ?? "")
    }

    private func push(_ client: KauriSyncClient) async throws {
        guard let chain, let key = dataKeys[epoch] else { return }
        let dirty = state.records.filter(\.value.dirty).sorted { ($0.value.kind == "folder" ? 0 : 1) < ($1.value.kind == "folder" ? 0 : 1) }
        for batch in stride(from: 0, to: dirty.count, by: 200).map({ Array(dirty[$0..<min($0 + 200, dirty.count)]) }) {
            var records = [KauriRecordIn]()
            for (id, r) in batch {
                guard let rid = Kauri.unb64(id), let body = try? RecordBody(json: r.body) else { continue }
                let sealed = try chain.sealRecord(dataKey: key, epoch: epoch, recordID: rid, plaintext: r.body)
                records.append(KauriRecordIn(id: id, epoch: epoch, body: Kauri.b64(sealed), deleted: body.isDeleted, blobs: nil, ifVersion: r.version))
            }
            do {
                let versions = try await client.write(records)
                for (rec, version) in zip(records, versions) {
                    state.records[rec.id]?.version = version
                    state.records[rec.id]?.epoch = epoch
                    state.records[rec.id]?.dirty = false
                }
            } catch let error as KauriAPIError where error.status == 409 {
                // Someone wrote first: merge their version and retry next round.
                try await pull(client)
                again = true
                return
            }
        }
        // Tombstones the server has are not needed locally beyond their version.
        state.save()
    }

    // MARK: - Local data → records

    /// Records every local change since the last scan with a fresh HLC.
    private func scan() {
        guard let chain else { return }
        let (folders, snippets) = (try? database.read { db in
            (try SnippetFolder.all.order(by: \.index).fetchAll(db), try Snippet.all.order(by: \.index).fetchAll(db))
        }) ?? ([], [])

        var byLocal = [String: String]() // "kind:local" → record ID
        for (id, r) in state.records { byLocal["\(r.kind):\(r.local)"] = id }

        func record(_ kind: String, _ local: String) -> (String, RecordBody) {
            if let id = byLocal["\(kind):\(local)"], let r = state.records[id], let body = try? RecordBody(json: r.body) {
                return (id, body)
            }
            let id = Kauri.b64(chain.recordID(kind: kind, localUUID: local))
            var body = RecordBody(kind: kind)
            if kind != "clip" { body.extra["u"] = .string(local) }
            state.records[id] = SyncedRecord(kind: kind, local: local, body: (try? body.json()) ?? Data())
            byLocal["\(kind):\(local)"] = id
            return (id, body)
        }

        func save(_ id: String, _ body: RecordBody, changed: Bool) {
            if state.records[id]?.kind != "clip" { state.records[id]?.materialized = true }
            guard changed else { return }
            state.records[id]?.body = (try? body.json()) ?? Data()
            state.records[id]?.dirty = true
        }

        var present = Set<String>()
        var folderIDs = [SnippetFolder.ID: String]()

        let folderRecords = folders.map { record("folder", $0.id.rawValue.uuidString.lowercased()) }
        let folderOrder = OrderKey.assign(folderRecords.map { $0.1["order"]?.string })
        for (i, folder) in folders.enumerated() {
            var (id, body) = folderRecords[i]
            folderIDs[folder.id] = id
            present.insert(id)
            var changed = false
            changed = body.set("title", .string(folder.title), at: tick) || changed
            changed = body.set("enabled", .bool(folder.isEnabled), at: tick) || changed
            changed = body.set("order", .string(folderOrder[i]), at: tick) || changed
            save(id, body, changed: changed)
        }

        for (folderID, items) in Dictionary(grouping: snippets, by: \.folderID) {
            guard let folderRecord = folderIDs[folderID] else { continue }
            let items = items.sorted { $0.index < $1.index }
            let recs = items.map { record("snippet", $0.id.rawValue.uuidString.lowercased()) }
            let order = OrderKey.assign(recs.map { $0.1["folder"]?.string == folderRecord ? $0.1["order"]?.string : nil })
            for (i, snippet) in items.enumerated() {
                var (id, body) = recs[i]
                present.insert(id)
                var changed = false
                changed = body.set("folder", .string(folderRecord), at: tick) || changed
                changed = body.set("title", .string(snippet.title), at: tick) || changed
                changed = body.set("content", .string(snippet.content), at: tick) || changed
                changed = body.set("enabled", .bool(snippet.isEnabled), at: tick) || changed
                changed = body.set("order", .string(order[i]), at: tick) || changed
                save(id, body, changed: changed)
            }
        }

        if state.syncsHistory { present.formUnion(scanHistory(record: record, save: save)) }

        // Local items that are gone were deleted.
        for (id, r) in state.records where !present.contains(id) {
            guard var body = try? RecordBody(json: r.body), !body.isDeleted else { continue }
            if r.kind == "clip" {
                // Only clips the user deleted are deleted everywhere. A clip that
                // fell out of the local history stays in sync for the others.
                guard state.syncsHistory, let local = r.history, !local.isEmpty else { continue }
                guard deletedHistory.contains(local) else {
                    state.records[id]?.history = ""
                    continue
                }
            } else {
                // A remote item that was never written here (its folder is
                // missing, say) was not deleted by the user.
                guard r.materialized == true else { continue }
                state.records[id]?.materialized = false
            }
            body.deleted = tick()
            save(id, body, changed: true)
        }
        deletedHistory.removeAll()
    }

    private func scanHistory(record: (String, String) -> (String, RecordBody), save: (String, RecordBody, Bool) -> Void) -> Set<String> {
        let histories = (try? database.read { db in
            try PasteboardHistory.order { $0.updateAt.desc() }.limit(KauriSync.historyCap).fetchAll(db)
        }) ?? []
        var byHistory = [String: String]()
        for (id, r) in state.records where r.kind == "clip" {
            if let h = r.history, !h.isEmpty { byHistory[h] = id }
        }
        var present = Set<String>()
        for h in histories where h.pasteboardTypes.contains(.string) {
            let rid: String
            var body: RecordBody
            if let known = byHistory[h.id.rawValue], let r = state.records[known], let b = try? RecordBody(json: r.body) {
                (rid, body) = (known, b)
            } else {
                guard let text = historyText(h.id), !text.isEmpty, text.utf8.count <= 60_000 else { continue }
                (rid, body) = record("clip", Kauri.sha256(Data(text.utf8)).hexString)
                state.records[rid]?.history = h.id.rawValue
                var changed = body.set("types", .array([.string("text")]), at: tick)
                changed = body.set("text", .string(text), at: tick) || changed
                changed = body.set("title", .string(String(h.title.prefix(200))), at: tick) || changed
                changed = body.set("created", .int(Int64(h.createdAt)), at: tick) || changed
                save(rid, body, changed)
            }
            present.insert(rid)
            if (body["lastUsed"]?.int ?? 0) < Int64(h.updateAt) {
                body.set("lastUsed", .int(Int64(h.updateAt)), at: tick)
                save(rid, body, true)
            }
        }
        return present
    }

    private func historyText(_ id: PasteboardHistory.ID) -> String? {
        let assets = (try? database.read { db in
            try PasteboardHistoryAsset.where { $0.pasteboardHistoryID.eq(id) }.fetchAll(db)
        }) ?? []
        return assets.first { $0.pasteboardType == .string }.flatMap { String(data: $0.data, encoding: .utf8) }
    }

    // MARK: - Records → local data

    /// Writes the state of every record to the local database.
    private func applyAll() {
        var folders = [(id: SnippetFolder.ID, rid: String, body: RecordBody)]()
        var snippets = [(id: Snippet.ID, rid: String, body: RecordBody)]()
        var clips = [(rid: String, body: RecordBody)]()
        for (rid, r) in state.records {
            guard let body = try? RecordBody(json: r.body) else { continue }
            switch r.kind {
            case "folder": if let u = UUID(uuidString: r.local) { folders.append((.init(u), rid, body)) }
            case "snippet": if let u = UUID(uuidString: r.local) { snippets.append((.init(u), rid, body)) }
            case "clip": clips.append((rid, body))
            default: break
            }
        }
        let folderByRecord = Dictionary(folders.map { ($0.rid, $0.id) }, uniquingKeysWith: { a, _ in a })
        withErrorReporting {
            try database.write { db in
                let liveFolders = folders.filter { !$0.body.isDeleted }
                    .sorted { ($0.body["order"]?.string ?? "", $0.rid) < ($1.body["order"]?.string ?? "", $1.rid) }
                for (index, f) in liveFolders.enumerated() {
                    state.records[f.rid]?.materialized = true
                    try SnippetFolder.upsert {
                        SnippetFolder(id: f.id, title: f.body["title"]?.string ?? "", index: index, isEnabled: f.body["enabled"]?.bool ?? true)
                    }
                    .execute(db)
                }
                let live = snippets.filter { !$0.body.isDeleted }
                let grouped = Dictionary(grouping: live) { $0.body["folder"]?.string ?? "" }
                for (folderRecord, items) in grouped {
                    guard let folderID = folderByRecord[folderRecord], liveFolders.contains(where: { $0.id == folderID }) else { continue }
                    let sorted = items.sorted { ($0.body["order"]?.string ?? "", $0.rid) < ($1.body["order"]?.string ?? "", $1.rid) }
                    for (index, s) in sorted.enumerated() {
                        state.records[s.rid]?.materialized = true
                        try Snippet.upsert {
                            Snippet(
                                id: s.id, folderID: folderID, title: s.body["title"]?.string ?? "",
                                content: s.body["content"]?.string ?? "", index: index, isEnabled: s.body["enabled"]?.bool ?? true
                            )
                        }
                        .execute(db)
                    }
                }
                for s in snippets where s.body.isDeleted {
                    state.records[s.rid]?.materialized = false
                    try Snippet.delete().where { $0.id.eq(s.id) }.execute(db)
                }
                for f in folders where f.body.isDeleted {
                    state.records[f.rid]?.materialized = false
                    try SnippetFolder.delete().where { $0.id.eq(f.id) }.execute(db)
                }
            }
        }
        if state.syncsHistory { applyClips(clips) }
    }

    private func applyClips(_ clips: [(rid: String, body: RecordBody)]) {
        for (rid, body) in clips {
            guard let r = state.records[rid] else { continue }
            if body.isDeleted {
                if let h = r.history, !h.isEmpty {
                    try? database.write { db in
                        try PasteboardHistory.delete().where { $0.id.eq(PasteboardHistory.ID(rawValue: h)) }.execute(db)
                    }
                    state.records[rid]?.history = nil
                }
                continue
            }
            guard r.history == nil, r.version > 0, let text = body["text"]?.string,
                  let content = PasteboardContent(assets: [.init(type: .string, data: Data(text.utf8))]) else { continue }
            let updateAt = Int(body["lastUsed"]?.int ?? body["created"]?.int ?? Int64(Date().timeIntervalSince1970))
            historyRepository.save(id: .init(rawValue: content.hash), content: content, updateAt: updateAt)
            state.records[rid]?.history = content.hash
        }
    }
}

// swiftlint:enable file_length type_body_length identifier_name
