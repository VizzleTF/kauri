# Kauri Sync, protocol version 1

Kauri Sync keeps snippets the same on all your devices, and clipboard history too if you turn it on. It is built around three properties:

- **End-to-end encrypted.** The server stores data it cannot read. It cannot even tell a snippet from a clip.
- **No account.** Devices form a *sync chain*. Its secret is a 24-word sync code that never leaves the devices.
- **Automatic.** A device pushes each change as it happens, and the server wakes the chain's other devices at once.

The server is [kauri-sync](https://github.com/VizzleTF/kauri-sync). The Windows and macOS clients and the server implement this document. The test vectors in [`sync-vectors/v1.json`](sync-vectors/v1.json) are part of the specification, and every client MUST pass them. [`sync-vectors/check.py`](sync-vectors/check.py) checks them independently of the reference code, using PyNaCl (libsodium) and `cryptography`. The check covers SPAKE2 through libsodium's edwards25519 operations.

The words MUST, SHOULD and MAY are used as in RFC 2119.

## 1. Threat model

| Threat | Protection |
|---|---|
| A server operator, or an intruder on the server, reads or changes data | Content is encrypted and authenticated on the device (§4). A changed record fails to decrypt. A record replayed from an older version is rejected (§6) |
| The server learns what kind of data you keep | Record IDs are random-looking, all kinds share one opaque type, sizes are padded into buckets (§4.3), and records carry no plaintext timestamps |
| A network attacker | TLS. On top of it, every request is signed by a device key and can be used once (§7.1) |
| A lost or stolen device | Remove it from another device. The chain moves to new keys, and the removed device can read or write nothing new (§9) |
| Harvest now, decrypt later | Data keys travel to devices in hybrid X25519 + ML-KEM-768 envelopes (§3.3). Every symmetric key is 256 bits |
| A guessable secret | Nobody types a password. The sync code holds 256 bits, and pairing codes allow a single online guess (§8) |

Kauri Sync does not protect against malware that runs as you on a device of the chain. It also does not hide traffic patterns from the server operator, who sees your IP address, when your devices sync, and roughly how much the chain stores. Running your own server, or reaching it over Tor, limits this.

## 2. Encodings and primitives

**Encodings.** Bytes in JSON are base64url without padding. Every integer that goes into an AAD, a signature or a key derivation is fixed-width big-endian: `u32(n)` is 4 bytes, `u64(n)` is 8. Strings are UTF-8. `||` is concatenation and `0x00` is a single zero byte.

**Primitives.**

| Purpose | Algorithm |
|---|---|
| Key derivation | HKDF-SHA-256 (RFC 5869) |
| Encryption | XChaCha20-Poly1305 (draft-irtf-cfrg-xchacha, as libsodium's `crypto_aead_xchacha20poly1305_ietf`) with a random 24-byte nonce |
| Signatures | Ed25519 (RFC 8032) |
| Envelopes | X25519 + ML-KEM-768 (FIPS 203), §3.3 |
| Pairing | SPAKE2 (RFC 9382) over edwards25519, §8 |
| Sync code | BIP-39 English: 24 words, 256-bit entropy |

Every symmetric ciphertext in this document has the form `seal(key, plaintext, aad) = nonce(24) || ciphertext || tag(16)`.

**Platform libraries.**

- Windows uses libsodium (the official `libsodium` NuGet package) and .NET's `MLKem`, HKDF and DPAPI.
- macOS uses CryptoKit only: `Curve25519`, `ChaChaPoly`, HKDF and, from macOS 26, `MLKEM768`. XChaCha20 is HChaCha20 in front of `ChaChaPoly`, and the edwards25519 group operations for pairing are a port of TweetNaCl.
- The reference implementation, and the source of the test vectors, is the Go package `kauri` in kauri-sync.

## 3. Keys

### 3.1 From the sync code

The 24 words encode the 32-byte *chain secret* `CS` as BIP-39 entropy. Chain values are derived with `HKDF(ikm = CS, salt = "kauri-sync/v1", info, length 32)`:

| Value | `info` | Use |
|---|---|---|
| `chain_id` | `chain-id` | Names the chain to the server |
| `chain_auth` | `chain-auth` | The Ed25519 seed of the *chain key*, which can add devices |
| `wrap_key` | `wrap-key` | Wraps data keys, so the sync code alone can recover them |
| `id_key` | `record-id-key` | Keys record and blob IDs (§4.1) |
| `name_key` | `device-name-key` | Encrypts device names |

The server stores `SHA-256(chain_id)` and the chain key's public key. It never sees `CS` or any value derived from it except `chain_id`, which travels in a request header.

### 3.2 Data keys

Content is encrypted with a random 32-byte *data key* `DK`. Each data key belongs to an *epoch*, which starts at 1. For each epoch the server keeps:

- `wrapped = seal(wrap_key, DK, "kauri-sync/v1/keyring" || chain_id || u32(epoch))`, which lets a device that has only the sync code recover;
- one envelope (§3.3) of `DK` for each member device.

A member MAY start a new epoch at any time (§7.2, `POST /v1/keyring`). New writes then use it; older records stay readable until they are rewritten.

### 3.3 Devices and envelopes

On first start a device creates three keys and keeps their private halves in the platform keystore (DPAPI on Windows, the Keychain on macOS):

- an Ed25519 *device key*;
- an X25519 key;
- an ML-KEM-768 key.

`device_id = SHA-256(device public key)[0..16]`.

To seal a 32-byte payload to a device:

```
e, E   = new X25519 key pair                    ; E is 32 bytes
ss_x   = X25519(e, device X25519 key)           ; MUST NOT be all zeros
ct_k, ss_k = ML-KEM-768.Encaps(device ML-KEM key)   ; ct_k is 1088 bytes
k      = HKDF(ikm  = ss_x || ss_k,
              salt = "kauri-sync/v1/envelope",
              info = E || ct_k || device X25519 key || device_id, length 32)
envelope = 0x02 || E || ct_k || seal(k, payload, chain_id || u32(epoch) || device_id)
```

If a device has no ML-KEM key (macOS before 26), its envelopes start with `0x01`, and `ct_k` and `ss_k` are empty. Clients show such a device as not post-quantum.

## 4. Records

Everything that syncs is a *record*: a folder, a snippet, a clip or the shared settings. The server sees a record as:

```json
{ "id": "kTq3-...", "version": 1842, "epoch": 1, "deleted": false, "body": "..." }
```

### 4.1 IDs

- `record id = HMAC-SHA-256(id_key, kind || ":" || local ID)[0..16]`
- `blob id   = HMAC-SHA-256(id_key, "blob:" || SHA-256(plaintext))[0..16]`

The local ID of a folder or snippet is its UUID in lowercase. The device that creates the item picks the UUID and writes it into the body as `u` (§4.2). Every other device stores the item under that same UUID, so all devices derive the same record ID, also for a new chain (§9).

The local ID of a clip is the lowercase hex SHA-256 of its text. Two devices that copy the same text share one record.

Both IDs are sent as 22-character base64url. They are stable, and reveal neither the kind nor the content.

### 4.2 Body

The plaintext is a UTF-8 JSON object:

```json
{
  "k": "snippet",
  "f": { "title": ["Hello", [1762600000000, 0, "<device_id>"]], "...": "..." },
  "d": null
}
```

- `f` maps each field to `[value, hlc]`. An HLC is `[wall ms, counter, device_id]` (§5.1). `device_id` is base64url.
- `d` is the HLC of the deletion, or `null`.
- `u` is the UUID of a folder or snippet (§4.1). It never changes.

The fields of each kind:

| Kind | Fields |
|---|---|
| `folder` | `title`, `enabled`, `order` |
| `snippet` | `folder` (record ID), `title`, `content`, `enabled`, `order` |
| `clip` | `types` (list), `title`, `text`, `assets` (list of `{"type", "blob", "size"}`), `created` and `lastUsed` (Unix seconds) |
| `settings` | Settings that make sense on every device: history limit, menu numbering, folder size. Shortcuts, excluded apps and per-device hot keys never sync |

Clients MUST keep fields they don't know and write them back unchanged.

```
body = seal(DK_epoch, pad(json), chain_id || record id || u32(epoch))
```

### 4.3 Padding

`pad(p)` appends `0x80` and then zeros, up to the smallest of 256, 1024, 4096, 16384 or 65536 bytes that leaves room for the `0x80`. Plaintext longer than 65535 bytes MUST NOT be a record: put the large part in a blob.

### 4.4 Blobs

Clip images and file contents are blobs, uploaded before the record that names them.

```
blob = seal(DK_epoch, pad64(content), chain_id || blob id || u32(epoch))
```

`pad64` pads in the same way as `pad`, up to the next multiple of 65536 bytes.

A record write lists the IDs of the blobs the record uses (`blobs`, §7.2). From that list the server knows which blobs are still in use. It deletes a blob that no live record lists once a day has passed.

## 5. Merging

The clients merge; the server only orders writes.

### 5.1 Hybrid logical clocks

An HLC is `[wall, counter, device]`, compared in that order:

- `wall`: Unix milliseconds;
- `counter`: 0 to 65535;
- `device`: compared as a string.

On every local edit the device takes `max(now, highest wall it has seen)`. It resets the counter if `wall` moved forward and increments it otherwise. It also advances its clock past every HLC it receives.

To merge two versions of a record:

- take the newer `[value, hlc]` of every field;
- take the newer `d`;
- the record is deleted if `d` is newer than every field. Editing a field after a deletion revives the record.

### 5.2 Order

`order` is a fractional index: a string of the digits `0-9A-Za-z` that sorts by byte. An item moved between neighbours `a` and `b` gets a string strictly between them. Equal strings break the tie by record ID.

### 5.3 Deletions

To delete an item, write its record with `d` set and `deleted: true`. The server removes deleted records 30 days after their last write. It then answers `410 resync` to `changes` requests whose `since` is older than that.

On a resync a client fetches everything from version 0. It deletes local items that are missing from the result, unless they have unsent changes, and then uploads what is left.

## 6. Versions

The server gives every write the next value of a per-chain counter, called the `version`. A client remembers the highest version it has applied.

A write MAY carry `if_version`. The server rejects the batch with `409` if the record's current version differs. The client then fetches the record, merges and retries.

A client MUST ignore a record whose version is not higher than the version it already applied to that ID. This stops the server from replaying old ciphertext.

## 7. Server API

Endpoints live under `/v1/`. Request and response bodies are JSON, except blobs.

### 7.1 Signed requests

Every request except the unauthenticated pairing ones carries these headers:

```
Kauri-Protocol:  1
Kauri-Chain:     base64url(chain_id)
Kauri-Device:    base64url(device public key)
Kauri-Time:      Unix seconds
Kauri-Nonce:     base64url(16 random bytes)
Kauri-Signature: base64url(Ed25519(device key,
                   "kauri-sync/v1/request" || 0x00 || method || 0x00 || path with query
                   || 0x00 || Kauri-Time || 0x00 || nonce || SHA-256(body)))
```

The nonce keeps two identical requests sent in the same second from getting the same signature. Ed25519 is deterministic, so without it the second request would look like a replay.

The server answers `401` in three cases:

- the time is more than 300 seconds away from its clock;
- the signature was already used;
- the device is not a member of the chain, apart from the requests that add one.

A removed device gets `403`.

### 7.2 Endpoints

A *device info* object is `{"sign", "x25519", "mlkem", "name"}`:

- `mlkem` MAY be empty;
- `name` is `seal(name_key, device name, "kauri-sync/v1/name" || device_id)`.

| Request | Body | Answer |
|---|---|---|
| `POST /v1/chains` | `{"chain_key", "chain_sig", "device", "keyring": {"epoch": 1, "wrapped", "envelope"}}` | `201`. `409` if the chain exists |
| `POST /v1/devices` | `{"device", "chain_sig"?, "envelope"?}` | `201` |
| `GET /v1/devices` | | `{"devices": [{"id", "sign", "x25519", "mlkem", "name", "added", "removed"}]}` |
| `DELETE /v1/devices/{id}` | | `204` |
| `GET /v1/keyring` | | `{"epoch", "wrapped", "envelope"}`. `envelope` is this device's envelope, or empty |
| `POST /v1/keyring` | `{"epoch", "wrapped", "envelopes": [{"device", "envelope"}]}` | `201`. `epoch` MUST be the current one plus 1 |
| `GET /v1/changes?since=V&limit=N` | | `{"version", "records": [...], "more"}`. `410 {"error": "resync"}` if `since` is too old |
| `POST /v1/records` | `{"records": [{"id", "epoch", "body", "deleted", "blobs"?, "if_version"?}]}` | `{"versions": [...]}`. `409 {"conflicts": [ids]}`. `413` over a limit |
| `PUT /v1/blobs/{id}` | the blob bytes | `204` |
| `GET /v1/blobs/{id}` | | the blob bytes, or `404` |
| `GET /v1/events` | | Server-Sent Events, §7.3 |
| `POST /v1/forward` | `{"envelopes": [{"device", "envelope"}]}` | `204`, §9 |
| `GET /v1/forward` | | `{"envelope"}` for this device |

**Who may add a device:**

- `POST /v1/chains` is signed by the device that creates the chain. `chain_sig = Ed25519(chain key, "kauri-sync/v1/add" || 0x00 || chain_id || device public key)` proves that the device knows the sync code.
- `POST /v1/devices` is either signed by a member, which adds the device in the body and sends its envelope, or signed by the new device itself together with `chain_sig`. The second form is recovery from the 24 words.

**Batches.** `POST /v1/records` is all or nothing. Versions are returned in the order of the request.

**Limits per chain** (defaults): 256 MiB of bodies and blobs, 100,000 records, 16 MiB per blob, 1,000 records and 8 MiB per batch.

### 7.3 Events

`GET /v1/events` streams these events:

| Event | Data | What the client does |
|---|---|---|
| `ready` | `{}` | The stream is open. The client fetches changes |
| `changed` | `{"version"}` | Fetch changes. A device is not told about its own writes |
| `devices` | `{}` | The device list changed |
| `keyring` | `{"epoch"}` | A new epoch started |
| `removed` | `{}` | This device was removed: stop syncing and forget the chain |
| `forwarded` | `{}` | The chain moved: `GET /v1/forward` (§9) |

The server sends a comment every 25 seconds and `retry: 5000`.

## 8. Adding a device

A device joins in one of two ways.

### 8.1 With a pairing code (the usual way)

A device of the chain, called A, shows a code of 12 Crockford base32 characters as `XXXX-XXXX-XXXX`. The alphabet is `0-9A-Z` without `I`, `L`, `O` and `U`; when the code is typed, `O` reads as `0`, and `I` and `L` read as `1`. The new device, B, types it. The code lives 5 minutes.

```
slot  = hex(SHA-256("kauri-pair-slot-v1" || 0x00 || first 4 characters))
w     = SHA-512("kauri-pair-v1" || 0x00 || slot || 0x00 || last 8 characters) mod L
SPAKE2 (RFC 9382): group edwards25519, generator G, M and N from RFC 9382 §6,
        A = "kauri-pair-existing", B = "kauri-pair-new", AAD = slot
pA    = w·M + x·G           (A, random scalar x)
pB    = w·N + y·G           (B, random scalar y)
K     = (8x mod L)·(pB − w·N) = (8y mod L)·(pA − w·M)
TT    = RFC 9382 transcript of A, B, pA, pB, K and w as a 32-byte big-endian number
Ke || Ka = SHA-256(TT)
KcA || KcB = HKDF(ikm = Ka, salt = none (32 zero bytes, RFC 5869), info = "ConfirmationKeys" || AAD, length 32)
cB    = HMAC-SHA-256(KcB, TT)
box_key = HKDF(ikm = Ke, salt = "kauri-pair-v1", info = "box", length 32)
number  = u32(HKDF(ikm = Ke, salt = "kauri-pair-v1", info = "verify", length 4)) mod 10000
```

Points MUST be valid encodings in the prime-order subgroup. Neither side may use `K`, `Ke` or `box_key` before it has checked the confirmation it receives.

1. A opens the slot with `pA`.
2. B reads the slot and finishes SPAKE2. It joins with `pB`, `cB` and `seal(box_key, info, "kauri-pair-v1/join" || slot)`. Here `info` is B's device info as JSON. B does not know `name_key` yet, so it puts its name in as plain text.
3. A checks `cB`. If the check fails, A deletes the slot and makes a new code. Otherwise both devices show `number`, and A asks the user to allow the new device by name.
4. On Allow, A seals B's name with `name_key` and adds B with `POST /v1/devices`, including B's envelope. It then approves with `seal(box_key, CS, "kauri-pair-v1/approve" || slot)`.
5. B opens the box and has the sync code.

The pairing endpoints:

| Request | Auth | Body | Answer |
|---|---|---|---|
| `POST /v1/pair` | member | `{"slot", "msg"}` | `204`. `409` if the slot is taken |
| `GET /v1/pair/{slot}` | none | | `{"msg", "joined", "bmsg", "confirm", "box", "approval", "expires"}` |
| `POST /v1/pair/{slot}/join` | none | `{"msg", "confirm", "box"}` | `204`. `409` if already joined |
| `POST /v1/pair/{slot}/approve` | member of the slot's chain | `{"box"}` | `204` |
| `DELETE /v1/pair/{slot}` | member of the slot's chain | | `204` |

A slot takes one join and one approval. The server limits code creation to 20 per hour per chain, joins to 30 per hour per address, and reads to 1,200 per hour per address. A wrong code fails at step 3 after one online guess, and a SPAKE2 transcript gives no way to guess offline.

### 8.2 With the sync code

B enters the 24 words and joins itself with `POST /v1/devices` and `chain_sig`. It then reads `wrapped` from the keyring and unwraps `DK` with `wrap_key`. This path works even when no other device is online.

## 9. Removing a device

The removed device knows `CS`, so removing it MUST move the chain to new secrets. A remaining device M does the following:

1. Removes the device: `DELETE /v1/devices/{id}`. From then on the server refuses its requests.
2. Creates `CS'` and a new chain with `POST /v1/chains`, and re-encrypts and uploads every live record and blob under the new chain's `DK'`.
3. Calls `POST /v1/forward` on the old chain with an envelope of `CS'` for every remaining member. The envelopes are sealed as in §3.3 with the old `chain_id` and epoch 0. The server deletes the old chain's records, blobs and keyring. It keeps only the forwarding envelopes, for 90 days. It answers every other request on the old chain with `410 {"error": "forwarded"}` and sends `forwarded` events.
4. Every remaining device reads its envelope with `GET /v1/forward`. It opens the envelope with its own keys, which also works for devices that were offline during the move. It then switches to `CS'`, joins the new chain with `chain_sig`, and syncs.

The app shows the new sync code and suggests writing it down. The removed device keeps what it had already downloaded and nothing more.

A device that leaves on its own (Turn Off Sync) removes itself with `DELETE /v1/devices/{its id}` and forgets `CS`. The chain does not move: the device was trusted until it left, and the user can still remove it from another device to force a move.

## 10. Rules for the clients

- Snippets and folders sync once sync is on. Clipboard history syncs only if you turn it on.
- A clip is never stored or synced if:
  - its source marks it as confidential (`ExcludeClipboardContentFromMonitorProcessing`, or `CanIncludeInClipboardHistory` = 0, on Windows; `org.nspasteboard.ConcealedType` on macOS);
  - it comes from an excluded app.
- Synced history is capped at the smaller of the local history limit and 500 clips. Images sync only when that is turned on separately, and only up to 4 MiB each.
- Clients SHOULD batch writes over a 2-second window, and SHOULD refetch on `changed` within 1 second.

## 11. Versioning

Clients send `Kauri-Protocol: 1`. A change to the record format, the key schedule or the signatures raises the protocol version. A server MAY serve several versions at the same time.
