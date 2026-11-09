// Checks Kauri Sync against docs/sync-vectors/v1.json without the Xcode
// project (its test target needs the exact Xcode version of the upstream CI).
//
// usage: scripts/sync_check.sh

// swiftlint:disable identifier_name force_cast
import CryptoKit
import Foundation

var failures = 0
func check(_ ok: Bool, _ what: String, line: Int = #line) {
    if !ok {
        failures += 1
        print("FAIL line \(line): \(what)")
    }
}

let url = URL(fileURLWithPath: CommandLine.arguments[1])
let v = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
func sec(_ n: String) -> [String: Any] { v[n] as! [String: Any] }
func hex(_ s: String, _ k: String) -> Data { Data(hex: sec(s)[k] as! String) }
func str(_ s: String, _ k: String) -> String { sec(s)[k] as! String }

// HChaCha20, draft-irtf-cfrg-xchacha-03 §2.2.1.
check(Kauri.hchacha20(
    key: Data(hex: "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"),
    nonce: Data(hex: "000000090000004a0000000031415927")
) == Data(hex: "82413b4227b27bfed30e42508a877d73a0f9e4d58a74a853c12ec41326d3ecdc"), "hchacha20")

// Chain.
let chain = KauriChain(secret: hex("chain", "secret"))
check(chain.words == str("chain", "words"), "words")
check((try? KauriChain(words: str("chain", "words")))?.id == chain.id, "words round trip")
check(chain.id == hex("chain", "chain_id"), "chain_id")
check(chain.idHash == hex("chain", "chain_id_hash"), "chain_id_hash")
check(chain.authKey.publicKey.rawRepresentation == hex("chain", "chain_key_pub"), "chain key")
check(chain.wrapKey == hex("chain", "wrap_key"), "wrap_key")
check(chain.idKey == hex("chain", "id_key"), "id_key")
check(chain.nameKey == hex("chain", "name_key"), "name_key")

// Records, blobs, keyring.
let rid = chain.recordID(kind: str("record", "kind"), localUUID: str("record", "local_uuid"))
check(rid == hex("record", "record_id"), "record id")
let dk = hex("record", "data_key")
let plain = Data(str("record", "plaintext").utf8)
let padded = try Kauri.pad(plain)
check(padded.count == sec("record")["padded_len"] as! Int, "padding")
let body = Kauri.seal(key: dk, plaintext: padded, aad: chain.recordAAD(rid, epoch: 1), nonce: hex("record", "nonce"))
check(body == hex("record", "body"), "record body (XChaCha20-Poly1305)")
check((try? chain.openRecord(dataKey: dk, epoch: 1, recordID: rid, body: body)) == plain, "open record")
var tampered = body
tampered[tampered.count - 1] ^= 1
check((try? chain.openRecord(dataKey: dk, epoch: 1, recordID: rid, body: tampered)) == nil, "tampered body is rejected")
check(chain.blobID(Data(str("blob", "content").utf8)) == hex("blob", "blob_id"), "blob id")
check((try? chain.unwrap(hex("keyring", "wrapped"), epoch: 1)) == dk, "unwrap")

// Device, request, envelopes.
let device = KauriDevice(
    sign: try .init(rawRepresentation: hex("device", "ed25519_seed")),
    x25519: try .init(rawRepresentation: hex("device", "x25519_priv")),
    mlkemSeed: hex("device", "mlkem768_seed"))
check(device.publicKey == hex("device", "ed25519_pub"), "device key")
check(device.id == hex("device", "device_id"), "device id")
check(device.recipient.x25519 == hex("device", "x25519_pub"), "x25519 pub")
let req = sec("request")
let msg = KauriRequest.message(method: req["method"] as! String, path: req["path"] as! String,
                               time: Int64(req["time"] as! Int), nonce: Data(hex: req["nonce"] as! String),
                               body: Data((req["body"] as! String).utf8))
check(msg == Data(hex: req["message"] as! String), "request message")
check(device.sign.publicKey.isValidSignature(Data(hex: req["signature"] as! String), for: msg), "request signature")
check((try? device.openEnvelope(chainID: chain.id, epoch: 1, envelope: hex("envelopes", "x25519"))) == dk, "x25519 envelope")
if #available(macOS 26.0, *) {
    check((try? device.openEnvelope(chainID: chain.id, epoch: 1, envelope: hex("envelopes", "hybrid"))) == dk, "hybrid envelope")
    check(Kauri.sha256(device.mlkemPublic) == hex("device", "mlkem768_pub_sha256"), "ML-KEM public key")
    let env = try KauriEnvelope.seal(chainID: chain.id, epoch: 7, to: device.recipient, payload: dk)
    check((try? device.openEnvelope(chainID: chain.id, epoch: 7, envelope: env)) == dk, "envelope round trip")
    check((try? device.openEnvelope(chainID: chain.id, epoch: 8, envelope: env)) == nil, "envelope of another epoch")
} else {
    print("skip: ML-KEM needs macOS 26")
}

// Pairing (SPAKE2).
let p = sec("pairing")
let code = KauriPairing.normalize(p["code"] as! String)!
check(code == p["normalized"] as! String, "code normalization")
let a = KauriPairing(code: code, existing: true, seed: Data(hex: p["x_seed"] as! String))
let b = KauriPairing(code: code, existing: false, seed: Data(hex: p["y_seed"] as! String))
check(a.slot == p["slot"] as! String, "slot")
check(a.message == Data(hex: p["pA"] as! String), "pA")
check(b.message == Data(hex: p["pB"] as! String), "pB")
let kb = try b.finish(peer: a.message)
let ka = try a.finish(peer: b.message)
check(kb.confirmB == Data(hex: p["confirm_b"] as! String), "confirmation")
check(ka.checkConfirm(kb.confirmB), "A accepts B's confirmation")
check(kb.boxKey == Data(hex: p["box_key"] as! String) && ka.boxKey == kb.boxKey, "box key")
check(kb.number == p["number"] as! Int, "number")
let approval = KauriPairing.sealApproval(ka, slot: a.slot, secret: chain.secret, nonce: Data(hex: p["approval_nonce"] as! String))
check(approval == Data(hex: p["approval"] as! String), "approval box")
check((try? KauriPairing.openApproval(kb, slot: b.slot, box: approval)) == chain.secret, "open approval")
let wrong = KauriPairing(code: KauriPairing.normalize("ZZZZ" + code.dropFirst(4))!, existing: false)
check(!ka.checkConfirm((try? wrong.finish(peer: a.message))?.confirmB ?? Data()), "a wrong code is not confirmed")
check((try? a.finish(peer: Data(count: 32))) == nil, "an invalid point is rejected")
check((try? a.finish(peer: Data([1] + [UInt8](repeating: 0, count: 31)))) == nil, "the identity is rejected")

// Order keys.
let between = OrderKey.between(nil, nil)
check(OrderKey.between("V", "W") > "V" && OrderKey.between("V", "W") < "W", "between")
check(OrderKey.between("V", "V1") > "V" && OrderKey.between("V", "V1") < "V1", "between a key and its extension")
check(OrderKey.between(nil, "1") < "1", "before the first")
var keys = (0..<50).reduce(into: [String]()) { acc, _ in acc.append(OrderKey.between(acc.last, nil)) }
check(keys == keys.sorted() && Set(keys).count == 50, "appending keeps order: \(between)")
keys = (0..<50).reduce(into: [String]()) { acc, _ in acc.insert(OrderKey.between(nil, acc.first), at: 0) }
check(keys == keys.sorted() && Set(keys).count == 50, "prepending keeps order")
let moved = OrderKey.assign(["e", "a", "b", "c", "d"])
check(moved == moved.sorted() && Array(moved.suffix(4)) == ["a", "b", "c", "d"], "moving one item rewrites only it: \(moved)")
let fresh = OrderKey.assign([nil, "b", nil, nil, "c", nil])
check(fresh == fresh.sorted() && fresh[1] == "b" && fresh[4] == "c", "new items get keys between: \(fresh)")

// Merging.
func h(_ w: Int64, _ d: String = "A") -> HLC { HLC(wall: w, counter: 0, device: d) }
var x = RecordBody(kind: "snippet")
x.fields["title"] = .init(value: .string("old"), hlc: h(1))
x.fields["content"] = .init(value: .string("mine"), hlc: h(5))
x.extra["u"] = .string("uuid")
var y = RecordBody(kind: "snippet")
y.fields["title"] = .init(value: .string("new"), hlc: h(3, "B"))
y.fields["content"] = .init(value: .string("theirs"), hlc: h(4, "B"))
y.extra["future"] = .bool(true)
let m1 = x.merged(with: y), m2 = y.merged(with: x)
check(m1["title"] == .string("new") && m1["content"] == .string("mine"), "field-wise last writer wins")
check(m1.fields == m2.fields, "merge is commutative")
check(m1.extra["future"] == .bool(true) && m1.extra["u"] == .string("uuid"), "unknown keys are kept")
var del = m1
del.deleted = h(4)
check(!del.isDeleted, "an edit after the deletion revives")
del.deleted = h(9)
check(del.isDeleted, "a deletion newer than every field deletes")
let round = try RecordBody(json: del.json())
check(round == del, "JSON round trip")
check(try RecordBody(json: Data(#"{"k":"folder","f":{"title":["T",[1762600000000,2,"dev"]]},"d":null,"z":1}"#.utf8)).fields["title"]?.hlc
    == HLC(wall: 1_762_600_000_000, counter: 2, device: "dev"), "parses the spec's body")

var clock = HLCClock()
let t1 = clock.tick(device: "A", now: Date(timeIntervalSince1970: 100))
let t2 = clock.tick(device: "A", now: Date(timeIntervalSince1970: 99))
check(t2 > t1, "the clock never goes back")
clock.observe(h(500_000))
check(clock.tick(device: "A", now: Date(timeIntervalSince1970: 1)) > h(500_000, "Z"), "the clock moves past what it sees")

print(failures == 0 ? "all sync checks passed" : "\(failures) sync checks failed")
exit(failures == 0 ? 0 : 1)

// swiftlint:enable identifier_name force_cast
