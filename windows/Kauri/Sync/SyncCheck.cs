using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kauri.Sync;

/// <summary>
/// Checks Kauri Sync against docs/sync-vectors/v1.json, and the merge and
/// ordering rules. Usage: Kauri.exe --sync-check v1.json --out result.txt
/// </summary>
public static class SyncCheck
{
    public static int Run(string vectorsPath, TextWriter log)
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            if (!ok) { failures++; log.WriteLine("FAIL " + what); }
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(vectorsPath));
            var v = doc.RootElement;
            string S(string sec, string k) => v.GetProperty(sec).GetProperty(k).GetString()!;
            byte[] H(string sec, string k) => KauriCrypto.Hex(S(sec, k));
            int I(string sec, string k) => v.GetProperty(sec).GetProperty(k).GetInt32();

            var chain = new KauriChain(H("chain", "secret"));
            Check(chain.Words == S("chain", "words"), "words");
            Check(KauriChain.FromWords(S("chain", "words")).Id.SequenceEqual(chain.Id), "words round trip");
            Check(chain.Id.SequenceEqual(H("chain", "chain_id")), "chain_id");
            Check(chain.IdHash.SequenceEqual(H("chain", "chain_id_hash")), "chain_id_hash");
            Check(chain.AuthKey.PublicKey.SequenceEqual(H("chain", "chain_key_pub")), "chain key");
            Check(chain.WrapKey.SequenceEqual(H("chain", "wrap_key")), "wrap_key");
            Check(chain.IdKey.SequenceEqual(H("chain", "id_key")), "id_key");
            Check(chain.NameKey.SequenceEqual(H("chain", "name_key")), "name_key");

            var rid = chain.RecordId(S("record", "kind"), S("record", "local_uuid"));
            Check(rid.SequenceEqual(H("record", "record_id")), "record id");
            var dk = H("record", "data_key");
            var plain = Encoding.UTF8.GetBytes(S("record", "plaintext"));
            var padded = KauriCrypto.Pad(plain);
            Check(padded.Length == I("record", "padded_len"), "padding");
            var body = KauriCrypto.Seal(dk, padded, chain.RecordAad(rid, 1), H("record", "nonce"));
            Check(body.SequenceEqual(H("record", "body")), "record body");
            Check(chain.OpenRecord(dk, 1, rid, body).SequenceEqual(plain), "open record");
            var tampered = (byte[])body.Clone();
            tampered[^1] ^= 1;
            Check(Throws(() => chain.OpenRecord(dk, 1, rid, tampered)), "tampered body is rejected");
            Check(chain.BlobId(Encoding.UTF8.GetBytes(S("blob", "content"))).SequenceEqual(H("blob", "blob_id")), "blob id");
            Check(chain.Unwrap(H("keyring", "wrapped"), 1).SequenceEqual(dk), "unwrap");

            var device = new KauriDevice(H("device", "ed25519_seed"), H("device", "x25519_priv"), H("device", "mlkem768_seed"));
            Check(device.PublicKey.SequenceEqual(H("device", "ed25519_pub")), "device key");
            Check(device.Id.SequenceEqual(H("device", "device_id")), "device id");
            Check(device.Recipient.X25519Key.SequenceEqual(H("device", "x25519_pub")), "x25519 pub");
            var req = v.GetProperty("request");
            var msg = KauriRequest.Message(req.GetProperty("method").GetString()!, req.GetProperty("path").GetString()!,
                req.GetProperty("time").GetInt64(), KauriCrypto.Hex(req.GetProperty("nonce").GetString()!), Encoding.UTF8.GetBytes(req.GetProperty("body").GetString()!));
            Check(msg.SequenceEqual(KauriCrypto.Hex(req.GetProperty("message").GetString()!)), "request message");
            // libsodium's Ed25519 is deterministic, so the signature matches the vector exactly.
            Check(device.SignRequest(req.GetProperty("method").GetString()!, req.GetProperty("path").GetString()!, req.GetProperty("time").GetInt64(),
                KauriCrypto.Hex(req.GetProperty("nonce").GetString()!), Encoding.UTF8.GetBytes(req.GetProperty("body").GetString()!))
                .SequenceEqual(KauriCrypto.Hex(req.GetProperty("signature").GetString()!)), "request signature");
            Check(device.OpenEnvelope(chain.Id, 1, H("envelopes", "x25519")).SequenceEqual(dk), "x25519 envelope");
            if (MLKem.IsSupported)
            {
                Check(device.OpenEnvelope(chain.Id, 1, H("envelopes", "hybrid")).SequenceEqual(dk), "hybrid envelope");
                Check(KauriCrypto.Sha256(device.MLKemPublic).SequenceEqual(H("device", "mlkem768_pub_sha256")), "ML-KEM public key");
                var env = KauriEnvelope.Seal(chain.Id, 7, device.Recipient, dk);
                Check(device.OpenEnvelope(chain.Id, 7, env).SequenceEqual(dk), "envelope round trip");
                Check(Throws(() => device.OpenEnvelope(chain.Id, 8, env)), "envelope of another epoch");
            }
            else log.WriteLine("skip: ML-KEM is not available on this Windows");

            var p = v.GetProperty("pairing");
            string P(string k) => p.GetProperty(k).GetString()!;
            var code = KauriPairing.Normalize(P("code"))!;
            Check(code == P("normalized"), "code normalization");
            var a = new KauriPairing(code, true, KauriCrypto.Hex(P("x_seed")));
            var b = new KauriPairing(code, false, KauriCrypto.Hex(P("y_seed")));
            Check(a.Slot == P("slot"), "slot");
            Check(a.Message.SequenceEqual(KauriCrypto.Hex(P("pA"))), "pA");
            Check(b.Message.SequenceEqual(KauriCrypto.Hex(P("pB"))), "pB");
            var kb = b.Finish(a.Message);
            var ka = a.Finish(b.Message);
            Check(kb.ConfirmB.SequenceEqual(KauriCrypto.Hex(P("confirm_b"))), "confirmation");
            Check(ka.CheckConfirm(kb.ConfirmB), "A accepts B's confirmation");
            Check(kb.BoxKey.SequenceEqual(KauriCrypto.Hex(P("box_key"))) && ka.BoxKey.SequenceEqual(kb.BoxKey), "box key");
            Check(kb.Number == p.GetProperty("number").GetInt32(), "number");
            var approval = KauriPairing.SealApproval(ka, a.Slot, chain.Secret, KauriCrypto.Hex(P("approval_nonce")));
            Check(approval.SequenceEqual(KauriCrypto.Hex(P("approval"))), "approval box");
            Check(KauriPairing.OpenApproval(kb, b.Slot, approval).SequenceEqual(chain.Secret), "open approval");
            var wrong = new KauriPairing(KauriPairing.Normalize("ZZZZ" + code[4..])!, false);
            Check(!ka.CheckConfirm(wrong.Finish(a.Message).ConfirmB), "a wrong code is not confirmed");
            Check(Throws(() => a.Finish(new byte[32])), "an invalid point is rejected");

            // Order keys.
            Check(string.CompareOrdinal(OrderKey.Between("V", "W"), "V") > 0 && string.CompareOrdinal(OrderKey.Between("V", "W"), "W") < 0, "between");
            Check(string.CompareOrdinal(OrderKey.Between("V", "V1"), "V") > 0 && string.CompareOrdinal(OrderKey.Between("V", "V1"), "V1") < 0, "between a key and its extension");
            var keys = new List<string>();
            for (int i = 0; i < 50; i++) keys.Add(OrderKey.Between(keys.Count > 0 ? keys[^1] : null, null));
            Check(keys.SequenceEqual(keys.OrderBy(k => k, StringComparer.Ordinal)) && keys.Distinct().Count() == 50, "appending keeps order");
            keys.Clear();
            for (int i = 0; i < 50; i++) keys.Insert(0, OrderKey.Between(null, keys.Count > 0 ? keys[0] : null));
            Check(keys.SequenceEqual(keys.OrderBy(k => k, StringComparer.Ordinal)) && keys.Distinct().Count() == 50, "prepending keeps order");
            var moved = OrderKey.Assign(["e", "a", "b", "c", "d"]);
            Check(moved.SequenceEqual(moved.OrderBy(k => k, StringComparer.Ordinal)) && moved.Skip(1).SequenceEqual(["a", "b", "c", "d"]), "moving one item rewrites only it");
            var fresh = OrderKey.Assign([null, "b", null, null, "c", null]);
            Check(fresh.SequenceEqual(fresh.OrderBy(k => k, StringComparer.Ordinal)) && fresh[1] == "b" && fresh[4] == "c", "new items get keys between");
            // The same keys as the Swift implementation.
            Check(OrderKey.Between(null, null) == "V" && OrderKey.Between("V", null) == "k" && OrderKey.Between("V", "W") == "VV", "keys match macOS");

            // Merging.
            static Hlc Hl(long w, string d = "A") => new(w, 0, d);
            var x = new RecordBody("snippet");
            x.Fields["title"] = ("old", Hl(1));
            x.Fields["content"] = ("mine", Hl(5));
            x.Extra["u"] = "uuid";
            var y = new RecordBody("snippet");
            y.Fields["title"] = ("new", Hl(3, "B"));
            y.Fields["content"] = ("theirs", Hl(4, "B"));
            y.Extra["future"] = true;
            var m1 = x.Merged(y);
            var m2 = y.Merged(x);
            Check(m1.Str("title") == "new" && m1.Str("content") == "mine", "field-wise last writer wins");
            Check(m1.Fields.Count == m2.Fields.Count && m1.Fields.All(f => m2.Fields[f.Key].Hlc == f.Value.Hlc), "merge is commutative");
            Check(m1.Extra.ContainsKey("future") && m1.Extra.ContainsKey("u"), "unknown keys are kept");
            m1.Deleted = Hl(4);
            Check(!m1.IsDeleted, "an edit after the deletion revives");
            m1.Deleted = Hl(9);
            Check(m1.IsDeleted, "a deletion newer than every field deletes");
            Check(RecordBody.Parse(m1.ToJson()).SameAs(m1), "JSON round trip");
            var spec = RecordBody.Parse("""{"k":"folder","f":{"title":["T",[1762600000000,2,"dev"]]},"d":null,"z":1}""");
            Check(spec.Fields["title"].Hlc == new Hlc(1762600000000, 2, "dev") && spec.Str("title") == "T", "parses the spec's body");
            Check(!spec.Set("title", "T", () => Hl(99)) && spec.Set("title", "U", () => Hl(99)), "only real changes get a new clock");

            var clock = new HlcClock();
            var t1 = clock.Tick("A", DateTimeOffset.FromUnixTimeSeconds(100));
            var t2 = clock.Tick("A", DateTimeOffset.FromUnixTimeSeconds(99));
            Check(t2 > t1, "the clock never goes back");
            clock.Observe(Hl(500_000));
            Check(clock.Tick("A", DateTimeOffset.FromUnixTimeSeconds(1)) > Hl(500_000, "Z"), "the clock moves past what it sees");
        }
        catch (Exception e)
        {
            failures++;
            log.WriteLine("FAIL exception: " + e);
        }
        log.WriteLine(failures == 0 ? "all sync checks passed" : $"{failures} sync checks failed");
        return failures == 0 ? 0 : 1;
    }

    static bool Throws(Action a)
    {
        try { a(); return false; }
        catch (Exception e) when (e is SyncCryptoException or CryptographicException) { return true; }
    }
}
