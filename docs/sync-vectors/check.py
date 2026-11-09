"""Independent check of v1.json with PyNaCl and cryptography (not the Go code).

    pip install pynacl cryptography
    python docs/sync-vectors/check.py
"""
import hashlib
import hmac
import json
import pathlib

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
from cryptography.hazmat.primitives.kdf.hkdf import HKDF
from nacl.bindings import (
    crypto_aead_xchacha20poly1305_ietf_decrypt as xopen,
    crypto_aead_xchacha20poly1305_ietf_encrypt as xseal,
)

v = json.loads((pathlib.Path(__file__).parent / "v1.json").read_text())
H = bytes.fromhex


def hkdf(ikm, salt, info, n=32):
    return HKDF(algorithm=hashes.SHA256(), length=n, salt=salt, info=info).derive(ikm)


def check(name, got, want):
    assert got == want, f"{name}: {got!r} != {want!r}"
    print("ok", name)


c = v["chain"]
cs = H(c["secret"])
check("chain_id", hkdf(cs, b"kauri-sync/v1", b"chain-id").hex(), c["chain_id"])
check("chain_id_hash", hashlib.sha256(H(c["chain_id"])).hexdigest(), c["chain_id_hash"])
seed = hkdf(cs, b"kauri-sync/v1", b"chain-auth")
pub = Ed25519PrivateKey.from_private_bytes(seed).public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)
check("chain_key_pub", pub.hex(), c["chain_key_pub"])
for k, info in [("wrap_key", b"wrap-key"), ("id_key", b"record-id-key"), ("name_key", b"device-name-key")]:
    check(k, hkdf(cs, b"kauri-sync/v1", info).hex(), c[k])

r = v["record"]
rid = hmac.new(H(c["id_key"]), f"{r['kind']}:{r['local_uuid']}".encode(), hashlib.sha256).digest()[:16]
check("record_id", rid.hex(), r["record_id"])
aad = H(c["chain_id"]) + rid + (1).to_bytes(4, "big")
check("record aad", aad.hex(), r["aad"])
pt = r["plaintext"].encode()
padded = pt + b"\x80" + b"\0" * (r["padded_len"] - len(pt) - 1)
body = H(r["nonce"]) + xseal(padded, aad, H(r["nonce"]), H(r["data_key"]))
check("record body", body.hex(), r["body"])

k = v["keyring"]
wrapped = H(k["nonce"]) + xseal(H(r["data_key"]), b"kauri-sync/v1/keyring" + H(c["chain_id"]) + (1).to_bytes(4, "big"), H(k["nonce"]), H(c["wrap_key"]))
check("keyring wrapped", wrapped.hex(), k["wrapped"])

b = v["blob"]
bid = hmac.new(H(c["id_key"]), b"blob:" + hashlib.sha256(b["content"].encode()).digest(), hashlib.sha256).digest()[:16]
check("blob_id", bid.hex(), b["blob_id"])

d = v["device"]
sk = Ed25519PrivateKey.from_private_bytes(H(d["ed25519_seed"]))
dpub = sk.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)
check("device pub", dpub.hex(), d["ed25519_pub"])
check("device_id", hashlib.sha256(dpub).digest()[:16].hex(), d["device_id"])
chain_sk = Ed25519PrivateKey.from_private_bytes(seed)
check("chain_sig", chain_sk.sign(b"kauri-sync/v1/add\x00" + H(c["chain_id"]) + dpub).hex(), d["chain_sig"])

q = v["request"]
msg = (b"kauri-sync/v1/request\x00" + q["method"].encode() + b"\x00" + q["path"].encode() + b"\x00"
       + str(q["time"]).encode() + b"\x00" + H(q["nonce"]) + hashlib.sha256(q["body"].encode()).digest())
check("request message", msg.hex(), q["message"])
check("request signature", sk.sign(msg).hex(), q["signature"])

# The X25519-only envelope (ML-KEM is not in these libraries).
e = H(v["envelopes"]["x25519"])
assert e[0] == 1
E, sealed = e[1:33], e[33:]
xpriv = X25519PrivateKey.from_private_bytes(H(d["x25519_priv"]))
xpub = xpriv.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)
check("x25519 pub", xpub.hex(), d["x25519_pub"])
ss = xpriv.exchange(X25519PublicKey.from_public_bytes(E))
key = hkdf(ss, b"kauri-sync/v1/envelope", E + xpub + H(d["device_id"]))
payload = xopen(sealed[24:], H(c["chain_id"]) + (1).to_bytes(4, "big") + H(d["device_id"]), sealed[:24], key)
check("x25519 envelope", payload.hex(), r["data_key"])

p = v["pairing"]
check("pair slot", hashlib.sha256(b"kauri-pair-slot-v1\x00" + p["normalized"][:4].encode()).hexdigest(), p["slot"])
appr = H(p["approval_nonce"]) + xseal(cs, ("kauri-pair-v1/approve" + p["slot"]).encode(), H(p["approval_nonce"]), H(p["box_key"]))
check("pair approval", appr.hex(), p["approval"])
# SPAKE2 with libsodium's edwards25519 point operations, as the clients do.
from nacl.bindings import (
    crypto_core_ed25519_add as padd,
    crypto_core_ed25519_scalar_mul as smul,
    crypto_core_ed25519_scalar_reduce as sreduce,
    crypto_core_ed25519_sub as psub,
    crypto_scalarmult_ed25519_base_noclamp as base,
    crypto_scalarmult_ed25519_noclamp as mul,
)

M = H("d048032c6ea0b6d697ddc2e86bda85a33adac920f1bf18e1b0c6d166a5cecdaf")
N = H("d3bfb518f44f3430f29d0c92af503865a1ed3281dc69b35dd868ba85f886c4ab")
code = p["normalized"]
w = sreduce(hashlib.sha512(b"kauri-pair-v1\x00" + p["slot"].encode() + b"\x00" + code[4:].encode()).digest())
x = sreduce(H(p["x_seed"]))
y = sreduce(H(p["y_seed"]))
pA = padd(mul(w, M), base(x))
pB = padd(mul(w, N), base(y))
check("pA", pA.hex(), p["pA"])
check("pB", pB.hex(), p["pB"])
eight = (8).to_bytes(32, "little")
K = mul(smul(eight, y), psub(pA, mul(w, M)))
assert K == mul(smul(eight, x), psub(pB, mul(w, N)))


def ln(b):
    return len(b).to_bytes(8, "little") + b


tt = b"".join(ln(s) for s in [b"kauri-pair-existing", b"kauri-pair-new", pA, pB, K, w[::-1]])
h = hashlib.sha256(tt).digest()
ke, ka = h[:16], h[16:]
kc = HKDF(algorithm=hashes.SHA256(), length=32, salt=None, info=b"ConfirmationKeys" + p["slot"].encode()).derive(ka)
check("confirm_b", hmac.new(kc[16:], tt, hashlib.sha256).hexdigest(), p["confirm_b"])
check("box_key", hkdf(ke, b"kauri-pair-v1", b"box").hex(), p["box_key"])
check("number", int.from_bytes(hkdf(ke, b"kauri-pair-v1", b"verify", 4), "big") % 10000, p["number"])
print("all vectors check out")
