//
//  KauriCrypto.swift
//
//  Kauri Sync v1 cryptography (docs/sync.md in github.com/VizzleTF/kauri).
//  Checked against docs/sync-vectors/v1.json by SyncVectorTests.
//

// swiftlint:disable identifier_name function_parameter_count
import CryptoKit
import Foundation
import Security

enum KauriCryptoError: Error {
    case badLength
    case badPadding
    case decryptFailed
    case badWords(String)
    case badPoint
    case unsupported
    case badCode
    case badConfirmation
}

extension KauriCryptoError: LocalizedError {
    var errorDescription: String? {
        switch self {
        case .badWords: String(localized: "The words are not a valid sync code.")
        case .badCode: String(localized: "The code is not valid.")
        case .badConfirmation: String(localized: "The pairing was not confirmed. Try a new code.")
        default: String(localized: "Sync failed")
        }
    }
}

enum Kauri {
    static let salt = Data("kauri-sync/v1".utf8)

    // MARK: Encodings

    static func b64(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    static func unb64(_ string: String) -> Data? {
        var s = string.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while s.count % 4 != 0 { s += "=" }
        return Data(base64Encoded: s)
    }

    static func u32(_ n: UInt32) -> Data { withUnsafeBytes(of: n.bigEndian) { Data($0) } }

    static func hkdf(_ ikm: Data, salt: Data, info: Data, count: Int = 32) -> Data {
        let key = HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: ikm), salt: salt, info: info, outputByteCount: count)
        return key.withUnsafeBytes { Data($0) }
    }

    static func sha256(_ data: Data) -> Data { Data(SHA256.hash(data: data)) }

    static func hmac(_ key: Data, _ message: Data) -> Data {
        Data(HMAC<SHA256>.authenticationCode(for: message, using: SymmetricKey(data: key)))
    }

    static func random(_ count: Int) -> Data {
        var d = Data(count: count)
        let rc = d.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, count, $0.baseAddress!) }
        precondition(rc == errSecSuccess, "no randomness")
        return d
    }

    // MARK: seal / open (§2)

    /// XChaCha20-Poly1305: HChaCha20 makes a subkey from the first 16 bytes
    /// of the nonce, and ChaCha20-Poly1305 (IETF) runs with the last 8.
    static func seal(key: Data, plaintext: Data, aad: Data, nonce: Data? = nil) -> Data {
        precondition(key.count == 32)
        let nonce = nonce ?? random(24)
        precondition(nonce.count == 24)
        let box = try! ChaChaPoly.seal(
            plaintext, using: SymmetricKey(data: hchacha20(key: key, nonce: nonce.prefix(16))),
            nonce: try ChaChaPoly.Nonce(data: Data(count: 4) + nonce.suffix(8)), authenticating: aad
        )
        return nonce + box.ciphertext + box.tag
    }

    static func open(key: Data, sealed: Data, aad: Data) throws -> Data {
        guard key.count == 32, sealed.count >= 40 else { throw KauriCryptoError.badLength }
        let nonce = sealed.prefix(24)
        do {
            let box = try ChaChaPoly.SealedBox(
                nonce: ChaChaPoly.Nonce(data: Data(count: 4) + nonce.suffix(8)),
                ciphertext: sealed.dropFirst(24).dropLast(16), tag: sealed.suffix(16)
            )
            return try ChaChaPoly.open(box, using: SymmetricKey(data: hchacha20(key: key, nonce: nonce.prefix(16))), authenticating: aad)
        } catch {
            throw KauriCryptoError.decryptFailed
        }
    }

    /// HChaCha20 (draft-irtf-cfrg-xchacha §2.2).
    static func hchacha20(key: Data, nonce: Data) -> Data {
        func words(_ d: Data) -> [UInt32] {
            let b = [UInt8](d)
            return stride(from: 0, to: b.count, by: 4).map {
                UInt32(b[$0]) | UInt32(b[$0 + 1]) << 8 | UInt32(b[$0 + 2]) << 16 | UInt32(b[$0 + 3]) << 24
            }
        }
        var x: [UInt32] = [0x6170_7865, 0x3320_646e, 0x7962_2d32, 0x6b20_6574] + words(key) + words(nonce)
        func quarter(_ a: Int, _ b: Int, _ c: Int, _ d: Int) {
            x[a] &+= x[b]; x[d] ^= x[a]; x[d] = x[d] << 16 | x[d] >> 16
            x[c] &+= x[d]; x[b] ^= x[c]; x[b] = x[b] << 12 | x[b] >> 20
            x[a] &+= x[b]; x[d] ^= x[a]; x[d] = x[d] << 8 | x[d] >> 24
            x[c] &+= x[d]; x[b] ^= x[c]; x[b] = x[b] << 7 | x[b] >> 25
        }
        for _ in 0..<10 {
            quarter(0, 4, 8, 12); quarter(1, 5, 9, 13); quarter(2, 6, 10, 14); quarter(3, 7, 11, 15)
            quarter(0, 5, 10, 15); quarter(1, 6, 11, 12); quarter(2, 7, 8, 13); quarter(3, 4, 9, 14)
        }
        var out = Data(capacity: 32)
        for w in x[0..<4] + x[12..<16] {
            withUnsafeBytes(of: w.littleEndian) { out.append(contentsOf: $0) }
        }
        return out
    }

    // MARK: padding (§4.3, §4.4)

    static let buckets = [256, 1024, 4096, 16384, 65536]

    static func pad(_ p: Data) throws -> Data {
        guard let b = buckets.first(where: { p.count + 1 <= $0 }) else { throw KauriCryptoError.badLength }
        return padTo(p, b)
    }

    static func pad64(_ p: Data) -> Data { padTo(p, (p.count / 65536 + 1) * 65536) }

    private static func padTo(_ p: Data, _ n: Int) -> Data {
        var out = p
        out.append(0x80)
        out.append(Data(count: n - out.count))
        return out
    }

    static func unpad(_ p: Data) throws -> Data {
        guard let i = p.lastIndex(where: { $0 != 0 }), p[i] == 0x80 else { throw KauriCryptoError.badPadding }
        return p.prefix(upTo: i)
    }
}

// MARK: - BIP-39

enum BIP39 {
    static func mnemonic(_ entropy: Data) -> String {
        let checksum = Kauri.sha256(entropy)[0]
        let bits = Array(entropy) + [checksum]
        let total = entropy.count * 8 + entropy.count * 8 / 32
        var words: [String] = []
        for i in stride(from: 0, to: total, by: 11) {
            var idx = 0
            for j in 0..<11 {
                let b = i + j
                if bits[b / 8] & (0x80 >> UInt8(b % 8)) != 0 { idx |= 1 << (10 - j) }
            }
            words.append(BIP39Words.english[idx])
        }
        return words.joined(separator: " ")
    }

    static func entropy(_ mnemonic: String) throws -> Data {
        let words = mnemonic.lowercased().split(whereSeparator: { $0.isWhitespace }).map(String.init)
        guard words.count == 24 else { throw KauriCryptoError.badWords("A sync code has 24 words.") }
        var bits = [UInt8](repeating: 0, count: 33)
        for (i, w) in words.enumerated() {
            guard let idx = BIP39Words.english.firstIndex(of: w) else { throw KauriCryptoError.badWords("“\(w)” is not a sync code word.") }
            for j in 0..<11 where idx & (1 << (10 - j)) != 0 {
                let b = i * 11 + j
                bits[b / 8] |= 0x80 >> UInt8(b % 8)
            }
        }
        let entropy = Data(bits[0..<32])
        guard Kauri.sha256(entropy)[0] == bits[32] else { throw KauriCryptoError.badWords("The sync code has a typo.") }
        return entropy
    }
}

// MARK: - Chain (§3.1, §3.2, §4)

struct KauriChain {
    let secret: Data
    let id: Data
    let authKey: Curve25519.Signing.PrivateKey
    let authSeed: Data
    let wrapKey: Data
    let idKey: Data
    let nameKey: Data

    init(secret: Data) {
        precondition(secret.count == 32)
        self.secret = secret
        func d(_ info: String) -> Data { Kauri.hkdf(secret, salt: Kauri.salt, info: Data(info.utf8)) }
        id = d("chain-id")
        authSeed = d("chain-auth")
        authKey = try! Curve25519.Signing.PrivateKey(rawRepresentation: authSeed)
        wrapKey = d("wrap-key")
        idKey = d("record-id-key")
        nameKey = d("device-name-key")
    }

    init(words: String) throws { self.init(secret: try BIP39.entropy(words)) }

    static func new() -> KauriChain { KauriChain(secret: Kauri.random(32)) }

    var words: String { BIP39.mnemonic(secret) }
    var idHash: Data { Kauri.sha256(id) }

    static func addMessage(chainID: Data, device: Data) -> Data { Data("kauri-sync/v1/add\u{0}".utf8) + chainID + device }

    func addSignature(device: Data) -> Data {
        try! authKey.signature(for: KauriChain.addMessage(chainID: id, device: device))
    }

    func recordID(kind: String, localUUID: String) -> Data {
        Kauri.hmac(idKey, Data("\(kind):\(localUUID.lowercased())".utf8)).prefix(16)
    }

    func blobID(_ content: Data) -> Data {
        Kauri.hmac(idKey, Data("blob:".utf8) + Kauri.sha256(content)).prefix(16)
    }

    func recordAAD(_ recordID: Data, epoch: UInt32) -> Data { id + recordID + Kauri.u32(epoch) }

    func sealRecord(dataKey: Data, epoch: UInt32, recordID: Data, plaintext: Data) throws -> Data {
        Kauri.seal(key: dataKey, plaintext: try Kauri.pad(plaintext), aad: recordAAD(recordID, epoch: epoch))
    }

    func openRecord(dataKey: Data, epoch: UInt32, recordID: Data, body: Data) throws -> Data {
        try Kauri.unpad(Kauri.open(key: dataKey, sealed: body, aad: recordAAD(recordID, epoch: epoch)))
    }

    func sealBlob(dataKey: Data, epoch: UInt32, blobID: Data, content: Data) -> Data {
        Kauri.seal(key: dataKey, plaintext: Kauri.pad64(content), aad: id + blobID + Kauri.u32(epoch))
    }

    func openBlob(dataKey: Data, epoch: UInt32, blobID: Data, blob: Data) throws -> Data {
        try Kauri.unpad(Kauri.open(key: dataKey, sealed: blob, aad: id + blobID + Kauri.u32(epoch)))
    }

    private func keyringAAD(_ epoch: UInt32) -> Data { Data("kauri-sync/v1/keyring".utf8) + id + Kauri.u32(epoch) }

    func wrap(dataKey: Data, epoch: UInt32) -> Data { Kauri.seal(key: wrapKey, plaintext: dataKey, aad: keyringAAD(epoch)) }

    func unwrap(_ wrapped: Data, epoch: UInt32) throws -> Data {
        let k = try Kauri.open(key: wrapKey, sealed: wrapped, aad: keyringAAD(epoch))
        guard k.count == 32 else { throw KauriCryptoError.badLength }
        return k
    }

    func sealName(deviceID: Data, name: String) -> Data {
        Kauri.seal(key: nameKey, plaintext: Data(name.utf8), aad: Data("kauri-sync/v1/name".utf8) + deviceID)
    }

    func openName(deviceID: Data, sealed: Data) -> String? {
        (try? Kauri.open(key: nameKey, sealed: sealed, aad: Data("kauri-sync/v1/name".utf8) + deviceID)).flatMap { String(data: $0, encoding: .utf8) }
    }
}

// MARK: - Devices and envelopes (§3.3)

struct KauriRecipient {
    let id: Data
    let x25519: Data
    let mlkem: Data // empty: X25519 only
}

struct KauriDevice {
    let sign: Curve25519.Signing.PrivateKey
    let x25519: Curve25519.KeyAgreement.PrivateKey
    let mlkemSeed: Data? // 64 bytes (d || z)

    static func new() -> KauriDevice {
        var seed: Data?
        if #available(macOS 26.0, *) {
            seed = (try? MLKEM768.PrivateKey())?.seedRepresentation
        }
        return KauriDevice(sign: .init(), x25519: .init(), mlkemSeed: seed)
    }

    var publicKey: Data { sign.publicKey.rawRepresentation }
    var id: Data { Kauri.sha256(publicKey).prefix(16) }

    var mlkemPublic: Data {
        guard let seed = mlkemSeed else { return Data() }
        if #available(macOS 26.0, *), let k = try? MLKEM768.PrivateKey(seedRepresentation: seed, publicKey: nil) {
            return k.publicKey.rawRepresentation
        }
        return Data()
    }

    var recipient: KauriRecipient {
        KauriRecipient(id: id, x25519: x25519.publicKey.rawRepresentation, mlkem: mlkemPublic)
    }

    func signRequest(method: String, path: String, time: Int64, nonce: Data, body: Data) -> Data {
        try! sign.signature(for: KauriRequest.message(method: method, path: path, time: time, nonce: nonce, body: body))
    }

    func openEnvelope(chainID: Data, epoch: UInt32, envelope: Data) throws -> Data {
        guard envelope.count > 33 else { throw KauriCryptoError.badLength }
        let kind = envelope[envelope.startIndex]
        var rest = envelope.dropFirst()
        let e = Data(rest.prefix(32))
        rest = rest.dropFirst(32)
        var ctk = Data(), ssk = Data()
        switch kind {
        case 0x01:
            break
        case 0x02:
            guard #available(macOS 26.0, *), let seed = mlkemSeed, rest.count > 1088 else { throw KauriCryptoError.unsupported }
            ctk = Data(rest.prefix(1088))
            rest = rest.dropFirst(1088)
            let key = try MLKEM768.PrivateKey(seedRepresentation: seed, publicKey: nil)
            ssk = try key.decapsulate(ctk).withUnsafeBytes { Data($0) }
        default:
            throw KauriCryptoError.unsupported
        }
        let ssx = try x25519.sharedSecretFromKeyAgreement(with: .init(rawRepresentation: e)).withUnsafeBytes { Data($0) }
        let k = KauriEnvelope.key(ssx: ssx, ssk: ssk, e: e, ctk: ctk, recipientX: x25519.publicKey.rawRepresentation, deviceID: id)
        return try Kauri.open(key: k, sealed: Data(rest), aad: chainID + Kauri.u32(epoch) + id)
    }
}

enum KauriEnvelope {
    static func key(ssx: Data, ssk: Data, e: Data, ctk: Data, recipientX: Data, deviceID: Data) -> Data {
        Kauri.hkdf(ssx + ssk, salt: Data("kauri-sync/v1/envelope".utf8), info: e + ctk + recipientX + deviceID)
    }

    static func seal(chainID: Data, epoch: UInt32, to r: KauriRecipient, payload: Data) throws -> Data {
        let eph = Curve25519.KeyAgreement.PrivateKey()
        let e = eph.publicKey.rawRepresentation
        let ssx = try eph.sharedSecretFromKeyAgreement(with: .init(rawRepresentation: r.x25519)).withUnsafeBytes { Data($0) }
        guard ssx != Data(count: 32) else { throw KauriCryptoError.badPoint }
        var ctk = Data(), ssk = Data(), kind: UInt8 = 0x01
        if !r.mlkem.isEmpty {
            guard #available(macOS 26.0, *) else { throw KauriCryptoError.unsupported }
            let res = try MLKEM768.PublicKey(rawRepresentation: r.mlkem).encapsulate()
            ctk = res.encapsulated
            ssk = res.sharedSecret.withUnsafeBytes { Data($0) }
            kind = 0x02
        }
        let k = key(ssx: ssx, ssk: ssk, e: e, ctk: ctk, recipientX: r.x25519, deviceID: r.id)
        return Data([kind]) + e + ctk + Kauri.seal(key: k, plaintext: payload, aad: chainID + Kauri.u32(epoch) + r.id)
    }
}

enum KauriRequest {
    static func message(method: String, path: String, time: Int64, nonce: Data, body: Data) -> Data {
        var m = Data("kauri-sync/v1/request\u{0}\(method)\u{0}\(path)\u{0}\(time)\u{0}".utf8)
        m.append(nonce)
        m.append(Kauri.sha256(body))
        return m
    }
}

// swiftlint:enable identifier_name function_parameter_count
