//
//  KauriSyncClient.swift
//
//  The HTTP side of Kauri Sync v1 (docs/sync.md §7): signed requests, the
//  endpoints, Server-Sent Events and the unsigned pairing requests.
//

import Foundation

struct KauriAPIError: Error, LocalizedError {
    let status: Int
    let message: String

    var errorDescription: String? { message.isEmpty ? "HTTP \(status)" : "\(message) (HTTP \(status))" }
}

struct KauriDeviceInfo: Codable, Equatable {
    var sign: String
    var x25519: String
    var mlkem: String
    var name: String
}

struct KauriDeviceEntry: Decodable, Identifiable, Equatable {
    let id: String
    let sign: String
    let x25519: String
    let mlkem: String?
    let name: String
    let added: Int64
    let removed: Int64?
}

struct KauriRecordOut: Decodable {
    let id: String
    let version: Int64
    let epoch: UInt32
    let deleted: Bool
    let body: String
}

struct KauriChanges: Decodable {
    let version: Int64
    let records: [KauriRecordOut]
    let more: Bool
}

struct KauriRecordIn: Encodable {
    let id: String
    let epoch: UInt32
    let body: String
    let deleted: Bool
    let blobs: [String]?
    let ifVersion: Int64?

    enum CodingKeys: String, CodingKey {
        case id, epoch, body, deleted, blobs
        case ifVersion = "if_version"
    }
}

struct KauriPairSlotState: Decodable {
    let msg: String?
    let joined: Bool?
    let bmsg: String?
    let confirm: String?
    let box: String?
    let approval: String?
    let expires: Int64?
}

struct KauriSyncClient {
    let server: URL
    let chain: KauriChain
    let device: KauriDevice
    var session: URLSession = .shared

    // MARK: - Requests

    func send(_ method: String, _ path: String, body: Data? = nil, contentType: String = "application/json") async throws -> (Data, HTTPURLResponse) {
        var request = URLRequest(url: KauriSyncClient.url(server, path))
        request.httpMethod = method
        request.httpBody = body
        request.timeoutInterval = 60
        let time = Int64(Date().timeIntervalSince1970)
        let nonce = Kauri.random(16)
        request.setValue("1", forHTTPHeaderField: "Kauri-Protocol")
        request.setValue(Kauri.b64(chain.id), forHTTPHeaderField: "Kauri-Chain")
        request.setValue(Kauri.b64(device.publicKey), forHTTPHeaderField: "Kauri-Device")
        request.setValue(String(time), forHTTPHeaderField: "Kauri-Time")
        request.setValue(Kauri.b64(nonce), forHTTPHeaderField: "Kauri-Nonce")
        let signature = device.signRequest(method: method, path: path, time: time, nonce: nonce, body: body ?? Data())
        request.setValue(Kauri.b64(signature), forHTTPHeaderField: "Kauri-Signature")
        if body != nil {
            request.setValue(contentType, forHTTPHeaderField: "Content-Type")
        }
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
        return (data, http)
    }

    @discardableResult
    func call<T: Decodable>(_ method: String, _ path: String, json: (any Encodable)? = nil, as type: T.Type = Empty.self) async throws -> T {
        let body = try json.map { try JSONEncoder().encode($0) }
        let (data, response) = try await send(method, path, body: body)
        try KauriSyncClient.check(response, data)
        if T.self == Empty.self || data.isEmpty {
            return try JSONDecoder().decode(T.self, from: Data("{}".utf8))
        }
        return try JSONDecoder().decode(T.self, from: data)
    }

    struct Empty: Codable {}

    static func check(_ response: HTTPURLResponse, _ data: Data) throws {
        guard response.statusCode / 100 != 2 else { return }
        let message = (try? JSONDecoder().decode([String: AnyCodableString].self, from: data))?["error"]?.value ?? ""
        throw KauriAPIError(status: response.statusCode, message: message)
    }

    static func url(_ server: URL, _ path: String) -> URL {
        URL(string: server.absoluteString.trimmingCharacters(in: CharacterSet(charactersIn: "/")) + path)!
    }

    // MARK: - Chain and devices

    func deviceInfo(name: String) -> KauriDeviceInfo {
        let recipient = device.recipient
        return KauriDeviceInfo(
            sign: Kauri.b64(device.publicKey),
            x25519: Kauri.b64(recipient.x25519),
            mlkem: Kauri.b64(recipient.mlkem),
            name: Kauri.b64(chain.sealName(deviceID: device.id, name: name))
        )
    }

    /// Registers a new chain with this device; returns the data key of epoch 1.
    func createChain(deviceName: String) async throws -> Data {
        let dataKey = Kauri.random(32)
        let envelope = try KauriEnvelope.seal(chainID: chain.id, epoch: 1, to: device.recipient, payload: dataKey)
        struct Keyring: Encodable { let epoch: UInt32; let wrapped: String; let envelope: String }
        struct Body: Encodable {
            let chain_key: String // swiftlint:disable:this identifier_name
            let chain_sig: String // swiftlint:disable:this identifier_name
            let device: KauriDeviceInfo
            let keyring: Keyring
        }
        try await call("POST", "/v1/chains", json: Body(
            chain_key: Kauri.b64(chain.authKey.publicKey.rawRepresentation),
            chain_sig: Kauri.b64(chain.addSignature(device: device.publicKey)),
            device: deviceInfo(name: deviceName),
            keyring: Keyring(epoch: 1, wrapped: Kauri.b64(chain.wrap(dataKey: dataKey, epoch: 1)), envelope: Kauri.b64(envelope))
        ))
        return dataKey
    }

    /// Adds this device to the chain with the sync code (§8.2).
    func joinWithSyncCode(deviceName: String) async throws {
        struct Body: Encodable {
            let device: KauriDeviceInfo
            let chain_sig: String // swiftlint:disable:this identifier_name
        }
        try await call("POST", "/v1/devices", json: Body(
            device: deviceInfo(name: deviceName),
            chain_sig: Kauri.b64(chain.addSignature(device: device.publicKey))
        ))
    }

    /// Adds another device as a member, with its envelope of the data key.
    func addDevice(_ info: KauriDeviceInfo, epoch: UInt32, dataKey: Data) async throws {
        guard let sign = Kauri.unb64(info.sign), let x25519 = Kauri.unb64(info.x25519) else { throw KauriCryptoError.badLength }
        let recipient = KauriRecipient(id: Kauri.sha256(sign).prefix(16), x25519: x25519, mlkem: Kauri.unb64(info.mlkem) ?? Data())
        let envelope = try KauriEnvelope.seal(chainID: chain.id, epoch: epoch, to: recipient, payload: dataKey)
        struct Body: Encodable { let device: KauriDeviceInfo; let envelope: String }
        try await call("POST", "/v1/devices", json: Body(device: info, envelope: Kauri.b64(envelope)))
    }

    func devices() async throws -> [KauriDeviceEntry] {
        struct Answer: Decodable { let devices: [KauriDeviceEntry] }
        return try await call("GET", "/v1/devices", as: Answer.self).devices
    }

    func removeDevice(id: String) async throws {
        try await call("DELETE", "/v1/devices/\(id)")
    }

    /// The current epoch and its data key, from this device's envelope or
    /// else from the wrapped copy.
    func dataKey() async throws -> (UInt32, Data) {
        struct Keyring: Decodable { let epoch: UInt32; let wrapped: String; let envelope: String? }
        let keyring = try await call("GET", "/v1/keyring", as: Keyring.self)
        if let env = keyring.envelope.flatMap(Kauri.unb64), !env.isEmpty,
           let key = try? device.openEnvelope(chainID: chain.id, epoch: keyring.epoch, envelope: env), key.count == 32 {
            return (keyring.epoch, key)
        }
        guard let wrapped = Kauri.unb64(keyring.wrapped) else { throw KauriCryptoError.badLength }
        return (keyring.epoch, try chain.unwrap(wrapped, epoch: keyring.epoch))
    }

    // MARK: - Records and blobs

    func changes(since: Int64, limit: Int = 500) async throws -> KauriChanges {
        try await call("GET", "/v1/changes?since=\(since)&limit=\(limit)", as: KauriChanges.self)
    }

    func write(_ records: [KauriRecordIn]) async throws -> [Int64] {
        struct Body: Encodable { let records: [KauriRecordIn] }
        struct Answer: Decodable { let versions: [Int64] }
        return try await call("POST", "/v1/records", json: Body(records: records), as: Answer.self).versions
    }

    // MARK: - Moving to a new chain (§9)

    func forward(envelopes: [(deviceID: Data, envelope: Data)]) async throws {
        struct Item: Encodable { let device: String; let envelope: String }
        struct Body: Encodable { let envelopes: [Item] }
        try await call("POST", "/v1/forward", json: Body(envelopes: envelopes.map {
            Item(device: Kauri.b64($0.deviceID), envelope: Kauri.b64($0.envelope))
        }))
    }

    /// The sync code of the chain this chain moved to.
    func forwardedSecret() async throws -> Data {
        struct Answer: Decodable { let envelope: String }
        let answer = try await call("GET", "/v1/forward", as: Answer.self)
        guard let env = Kauri.unb64(answer.envelope) else { throw KauriCryptoError.badLength }
        let secret = try device.openEnvelope(chainID: chain.id, epoch: 0, envelope: env)
        guard secret.count == 32 else { throw KauriCryptoError.badLength }
        return secret
    }

    // MARK: - Events (§7.3)

    /// Streams `(event, data)` pairs until the connection closes.
    func events() -> AsyncThrowingStream<(String, String), Error> {
        AsyncThrowingStream { continuation in
            let task = Task {
                do {
                    var request = URLRequest(url: KauriSyncClient.url(server, "/v1/events"))
                    let time = Int64(Date().timeIntervalSince1970)
                    let nonce = Kauri.random(16)
                    request.timeoutInterval = 90 // the server sends a comment every 25 s
                    request.setValue("text/event-stream", forHTTPHeaderField: "Accept")
                    request.setValue("1", forHTTPHeaderField: "Kauri-Protocol")
                    request.setValue(Kauri.b64(chain.id), forHTTPHeaderField: "Kauri-Chain")
                    request.setValue(Kauri.b64(device.publicKey), forHTTPHeaderField: "Kauri-Device")
                    request.setValue(String(time), forHTTPHeaderField: "Kauri-Time")
                    request.setValue(Kauri.b64(nonce), forHTTPHeaderField: "Kauri-Nonce")
                    let signature = device.signRequest(method: "GET", path: "/v1/events", time: time, nonce: nonce, body: Data())
                    request.setValue(Kauri.b64(signature), forHTTPHeaderField: "Kauri-Signature")
                    let (bytes, response) = try await session.bytes(for: request)
                    guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
                    guard http.statusCode == 200 else { throw KauriAPIError(status: http.statusCode, message: "") }
                    var event = "", data = ""
                    // `lines` drops empty lines, so an event ends at the next `event:` or at a comment.
                    for try await line in bytes.lines {
                        if line.hasPrefix("event: ") {
                            if !event.isEmpty { continuation.yield((event, data)) }
                            event = String(line.dropFirst(7))
                            data = ""
                        } else if line.hasPrefix("data: ") {
                            data = String(line.dropFirst(6))
                            if !event.isEmpty {
                                continuation.yield((event, data))
                                event = ""
                            }
                        }
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    // MARK: - Pairing (§8.1)

    func openPairing(slot: String, message: Data) async throws {
        try await call("POST", "/v1/pair", json: ["slot": slot, "msg": Kauri.b64(message)])
    }

    func approvePairing(slot: String, box: Data) async throws {
        try await call("POST", "/v1/pair/\(slot)/approve", json: ["box": Kauri.b64(box)])
    }

    func closePairing(slot: String) async throws {
        try await call("DELETE", "/v1/pair/\(slot)")
    }

    /// Unsigned: anyone with the slot may read it.
    static func pairingState(server: URL, slot: String) async throws -> KauriPairSlotState {
        let (data, response) = try await URLSession.shared.data(from: url(server, "/v1/pair/\(slot)"))
        guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
        try check(http, data)
        return try JSONDecoder().decode(KauriPairSlotState.self, from: data)
    }

    /// Unsigned: the new device answers a slot.
    static func joinPairing(server: URL, slot: String, message: Data, confirm: Data, box: Data) async throws {
        var request = URLRequest(url: url(server, "/v1/pair/\(slot)/join"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(["msg": Kauri.b64(message), "confirm": Kauri.b64(confirm), "box": Kauri.b64(box)])
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
        try check(http, data)
    }
}

/// Decodes only string values; other JSON values decode as empty.
struct AnyCodableString: Decodable {
    let value: String

    init(from decoder: Decoder) throws {
        value = (try? decoder.singleValueContainer().decode(String.self)) ?? ""
    }
}
