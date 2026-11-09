//
//  SyncVectorTests.swift
//
//  Kauri Sync v1 test vectors (docs/sync-vectors/v1.json in the kauri repo).
//

// swiftlint:disable identifier_name force_cast
import CryptoKit
import Foundation
import Testing
@testable import Clipy

private struct Vectors {
    let json: [String: Any]

    init() throws {
        let url = try #require(Bundle(for: BundleToken.self).url(forResource: "kauri-sync-v1", withExtension: "json"))
        json = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
    }

    func section(_ name: String) -> [String: Any] { json[name] as! [String: Any] }
    func hex(_ s: String, _ k: String) -> Data { Data(hex: section(s)[k] as! String) }
    func string(_ s: String, _ k: String) -> String { section(s)[k] as! String }
}

private final class BundleToken {}

struct SyncVectorTests {
    @Test func chainDerivation() throws {
        let v = try Vectors()
        let chain = KauriChain(secret: v.hex("chain", "secret"))
        #expect(chain.words == v.string("chain", "words"))
        #expect(try KauriChain(words: v.string("chain", "words")).id == chain.id)
        #expect(chain.id == v.hex("chain", "chain_id"))
        #expect(chain.idHash == v.hex("chain", "chain_id_hash"))
        #expect(chain.authKey.publicKey.rawRepresentation == v.hex("chain", "chain_key_pub"))
        #expect(chain.wrapKey == v.hex("chain", "wrap_key"))
        #expect(chain.idKey == v.hex("chain", "id_key"))
        #expect(chain.nameKey == v.hex("chain", "name_key"))
    }

    @Test func recordsBlobsKeyring() throws {
        let v = try Vectors()
        let chain = KauriChain(secret: v.hex("chain", "secret"))
        let id = chain.recordID(kind: v.string("record", "kind"), localUUID: v.string("record", "local_uuid"))
        #expect(id == v.hex("record", "record_id"))
        let dk = v.hex("record", "data_key")
        let plain = Data(v.string("record", "plaintext").utf8)
        let padded = try Kauri.pad(plain)
        #expect(padded.count == v.section("record")["padded_len"] as! Int)
        let body = Kauri.seal(key: dk, plaintext: padded, aad: chain.recordAAD(id, epoch: 1), nonce: v.hex("record", "nonce"))
        #expect(body == v.hex("record", "body"))
        #expect(try chain.openRecord(dataKey: dk, epoch: 1, recordID: id, body: body) == plain)
        #expect(chain.blobID(Data(v.string("blob", "content").utf8)) == v.hex("blob", "blob_id"))
        let wrapped = v.hex("keyring", "wrapped")
        #expect(try chain.unwrap(wrapped, epoch: 1) == dk)
    }

    @Test func deviceRequestAndEnvelopes() throws {
        let v = try Vectors()
        let chain = KauriChain(secret: v.hex("chain", "secret"))
        let device = KauriDevice(
            sign: try .init(rawRepresentation: v.hex("device", "ed25519_seed")),
            x25519: try .init(rawRepresentation: v.hex("device", "x25519_priv")),
            mlkemSeed: v.hex("device", "mlkem768_seed"))
        #expect(device.publicKey == v.hex("device", "ed25519_pub"))
        #expect(device.id == v.hex("device", "device_id"))
        #expect(device.recipient.x25519 == v.hex("device", "x25519_pub"))
        // CryptoKit's Ed25519 signatures are randomized, so check by verification.
        let sig = chain.addSignature(device: device.publicKey)
        #expect(chain.authKey.publicKey.isValidSignature(sig, for: KauriChain.addMessage(chainID: chain.id, device: device.publicKey)))
        let req = v.section("request")
        let msg = KauriRequest.message(method: req["method"] as! String, path: req["path"] as! String,
                                       time: Int64(req["time"] as! Int), nonce: Data(hex: req["nonce"] as! String),
                                       body: Data((req["body"] as! String).utf8))
        #expect(msg == Data(hex: req["message"] as! String))
        #expect(device.sign.publicKey.isValidSignature(Data(hex: req["signature"] as! String), for: msg))

        let dk = v.hex("record", "data_key")
        #expect(try device.openEnvelope(chainID: chain.id, epoch: 1, envelope: v.hex("envelopes", "x25519")) == dk)
        if #available(macOS 26.0, *) {
            #expect(try device.openEnvelope(chainID: chain.id, epoch: 1, envelope: v.hex("envelopes", "hybrid")) == dk)
            #expect(Kauri.sha256(device.mlkemPublic) == v.hex("device", "mlkem768_pub_sha256"))
            let env = try KauriEnvelope.seal(chainID: chain.id, epoch: 7, to: device.recipient, payload: dk)
            #expect(try device.openEnvelope(chainID: chain.id, epoch: 7, envelope: env) == dk)
        }
    }

    @Test func pairing() throws {
        let v = try Vectors()
        let p = v.section("pairing")
        let code = try #require(KauriPairing.normalize(p["code"] as! String))
        #expect(code == p["normalized"] as! String)
        let a = KauriPairing(code: code, existing: true, seed: Data(hex: p["x_seed"] as! String))
        let b = KauriPairing(code: code, existing: false, seed: Data(hex: p["y_seed"] as! String))
        #expect(a.slot == p["slot"] as! String)
        #expect(a.message == Data(hex: p["pA"] as! String))
        #expect(b.message == Data(hex: p["pB"] as! String))
        let kb = try b.finish(peer: a.message)
        let ka = try a.finish(peer: b.message)
        #expect(kb.confirmB == Data(hex: p["confirm_b"] as! String))
        #expect(ka.checkConfirm(kb.confirmB))
        #expect(kb.boxKey == Data(hex: p["box_key"] as! String))
        #expect(kb.number == p["number"] as! Int)
        let approval = KauriPairing.sealApproval(ka, slot: a.slot, secret: v.hex("chain", "secret"), nonce: Data(hex: p["approval_nonce"] as! String))
        #expect(approval == Data(hex: p["approval"] as! String))
        #expect(try KauriPairing.openApproval(kb, slot: b.slot, box: approval) == v.hex("chain", "secret"))
    }
}

// swiftlint:enable identifier_name force_cast
