# Kauri Sync, protocol version 1

Kauri Sync keeps snippets, and if you turn it on, clipboard history, the same on all your devices. It has three properties:

- **End-to-end encrypted.** The server stores data it cannot read, and cannot tell a snippet from a clip.
- **No account.** Devices form a *sync chain*. The chain's secret is a 24-word sync code that never leaves the devices.
- **Automatic.** Changes are pushed as they happen, and the server wakes the other devices at once.

The server is `kauri-sync` (separate repository, to be created). This document is what the Windows and macOS clients and the server implement. Test vectors for every derivation and format go into `docs/sync-vectors/` together with the first implementation, and are part of the specification.

Words such as MUST and SHOULD are used as in RFC 2119.

## 1. Threat model

What Kauri Sync protects against:

| Threat | Protection |
|---|---|
| A server operator, or someone who breaks into the server, reads or changes data | All content is encrypted and authenticated on the device (§4). A changed or replayed record fails to decrypt, or is rejected by the version rules (§6) |
| The server learns what kind of data you store | Record IDs are random. Every kind of record goes into one opaque type. Sizes are padded into buckets (§4.3). There are no plaintext timestamps |
| A network attacker | TLS. Requests are also signed by a device key, so a stolen bearer value cannot be replayed (§7.1) |
| A stolen or lost device | You remove it from another device. The chain is re-keyed, and the removed device can no longer read new data or write anything (§9) |
| Harvest now, decrypt later | Data keys travel between devices in hybrid X25519 + ML-KEM-768 envelopes (§3.3). All symmetric keys are 256-bit |
| A weak device-local secret | There is none to type in. The sync code holds 256 bits of entropy |

What it does not protect against: malware running as you on a device that is in the chain, and traffic analysis by the server operator. The operator can see your IP address, when your devices sync and roughly how much data the chain holds. Running your own server, or reaching it over Tor, limits this.

## 2. Primitives

| Purpose | Algorithm | Windows (.NET 10) | macOS (CryptoKit) |
|---|---|---|---|
| Key derivation | HKDF-SHA-256 (RFC 5869) | `HKDF` | `HKDF<SHA256>` |
| Content encryption | XChaCha20-Poly1305 with 192-bit random nonces | libsodium via NSec | swift-sodium / libsodium |
| Device signatures | Ed25519 | NSec | `Curve25519.Signing` |
| Envelopes | X25519 + ML-KEM-768 hybrid KEM, then HKDF, then XChaCha20-Poly1305 | NSec + `MLKem` (liboqs where Windows lacks ML-KEM) | `Curve25519.KeyAgreement` + `MLKEM768` (macOS 26+) |
| Pairing | SPAKE2 (RFC 9382) over edwards25519 | shared implementation, test vectors | shared implementation, test vectors |
| Sync code | BIP-39 English, 24 words = 256-bit entropy + 8-bit checksum | | |

The same libsodium build is used on both platforms, so XChaCha20-Poly1305 is identical everywhere. Random 192-bit nonces can't realistically collide, so no nonce counter has to be kept across devices.

## 3. Keys

### 3.1 From the sync code

The sync code encodes the *chain secret* `CS` (32 bytes). Every other chain-level value comes from `CS` through HKDF-SHA-256, with `salt = "kauri-sync/v1"` and these `info` strings:

| Output | `info` | Use |
|---|---|---|
| `chain_id` (32 B) | `chain-id` | Identifies the chain to the server. Sent as base64url |
| `chain_auth` (32 B) | `chain-auth` | Seed of the chain's Ed25519 key, used to authorise new devices (§7.1) |
| `wrap_key` (32 B) | `wrap-key` | Wraps the current data key, for recovery from the sync code alone |
| `id_key` (32 B) | `record-id-key` | Keys the HMAC that turns local IDs into record IDs (§4.1) |

The server stores only `SHA-256(chain_id)` and the public half of `chain_auth`. It never sees `CS`.

### 3.2 The data key

Content is encrypted with a random 32-byte *data key* `DK`. Each data key has an *epoch*, a counter that starts at 1. Re-keying (§9) creates `DK` for the next epoch and re-encrypts every record.

The server keeps a *keyring* record for each epoch:

- `DK` wrapped with `wrap_key` (XChaCha20-Poly1305, AAD = `"keyring" || chain_id || epoch`), so a device that has only the sync code can recover;
- `DK` sealed in an envelope (§3.3) to every device that is a member at that epoch.

### 3.3 Device keys and envelopes

On first start every device creates:

- an Ed25519 key `dev_sign`, which signs requests and identifies the device;
- an X25519 key and an ML-KEM-768 key, which receive envelopes.

The private keys stay in the platform keystore: DPAPI-protected storage on Windows, the Keychain on macOS (with the Secure Enclave where it can).

An envelope to a device seals a 32-byte payload as follows:

```
(ss_x, ct_x)   = X25519 ephemeral exchange with the device's X25519 public key
(ss_k, ct_k)   = ML-KEM-768 encapsulate to the device's ML-KEM public key
k              = HKDF-SHA-256(salt = "kauri-sync/v1/envelope",
                              ikm  = ss_x || ss_k,
                              info = ct_x || ct_k || device_id)
envelope       = ct_x || ct_k || XChaCha20-Poly1305(k, payload, aad = chain_id || epoch || device_id)
```

A device that has no ML-KEM implementation, such as macOS before 26, registers no ML-KEM key. Envelopes to it then use X25519 only, with `ss_k` and `ct_k` empty. The device list marks such devices, because their envelopes are not post-quantum.

## 4. Records

Everything that syncs is a *record*: a snippet folder, a snippet, a clip, or a settings bundle. The server sees records only in this form:

```json
{ "id": "<22 chars base64url>", "version": 1842, "epoch": 3, "body": "<base64url ciphertext>" }
```

### 4.1 Record IDs

`id = base64url(HMAC-SHA-256(id_key, kind || ":" || local_uuid)[0..16])`

The ID is stable for the life of the item and reveals neither its kind nor its local UUID.

### 4.2 Body

The plaintext is deterministic CBOR (RFC 8949 §4.2):

```
{
  "k": "folder" | "snippet" | "clip" | "settings",
  "f": { <field name>: [<value>, <HLC timestamp>], ... },   ; per-field last-writer-wins
  "d": <HLC timestamp> | null                              ; tombstone time, if deleted
}
```

The fields of each kind:

| Kind | Fields |
|---|---|
| `folder` | `title`, `enabled`, `order` (fractional index, §5.2), `hotkey` (optional; each device can override it locally) |
| `snippet` | `folder` (record ID), `title`, `content`, `enabled`, `order` |
| `clip` | `types` (list), `title`, `assets` (list of `{type, blob}`), `created`, `lastUsed`, `origin` (device ID) |
| `settings` | Only the settings that make sense on every device: menu layout, history limit, numbering. Not shortcuts, not excluded apps |

`body = nonce(24) || XChaCha20-Poly1305(DK_epoch, pad(cbor), aad = chain_id || id || epoch)`

### 4.3 Padding

Before encryption the CBOR is padded to the smallest bucket that fits: 256 B, 1 KiB, 4 KiB, 16 KiB or 64 KiB. Above 64 KiB the content has to go into a blob. Padding is an ISO/IEC 7816-4 0x80 byte, then zeros.

### 4.4 Blobs

Image and file assets of clips are stored as blobs, never inside a record.

- `blob_id = base64url(HMAC-SHA-256(DK_epoch, "blob:" || content)[0..16])`
- The blob is encrypted the same way, padded to a multiple of 64 KiB, and uploaded once.
- A blob that no live record references is deleted by the server after a grace period.

## 5. Merging

Clients merge. The server only orders writes.

### 5.1 Hybrid logical clocks

Each field carries an HLC timestamp:

- `wall`: Unix milliseconds;
- `counter`: 16 bits;
- `device`: the first 8 bytes of the device ID.

Timestamps are compared in that order. To merge two versions of a record, take the newer value of every field. A tombstone wins over every field that is older than it. A field edited after the tombstone revives the record.

### 5.2 Order

Folder and snippet order is a fractional index: a base-62 string that sorts lexicographically. Moving an item rewrites only that item's `order`. Two devices that pick the same index break the tie by record ID.

### 5.3 History

- Clips never change after they are created, except `lastUsed`.
- Each device enforces the history limit by deleting the oldest clips, writing tombstones for them.
- Tombstones older than 30 days are compacted by the server.

## 6. Versions and writes

The server gives every write the next value of a per-chain sequence, called `version`. A client keeps the highest version it has applied. A write MAY carry `if_version`. The server refuses the write with `409` if the record has changed since that version; the client then fetches the record, merges it and tries again.

A client MUST reject a record whose version is lower than one it has already applied for the same ID. This stops a server from rolling a record back to an older ciphertext.

## 7. Server API

All endpoints live under `https://<server>/v1/` and use JSON.

### 7.1 Authentication

Every request carries the following headers:

```
Kauri-Chain:     base64url(chain_id)
Kauri-Device:    base64url(dev_sign public key)
Kauri-Time:      Unix seconds
Kauri-Signature: Ed25519(dev_sign, method || path || time || SHA-256(body))
```

The server rejects a request if:

- the time is more than 5 minutes off;
- the signature has been seen before, within the time window;
- the device is not a member of the chain.

Adding a member requires the signature of the chain key (`chain_auth`) or of an existing member.

### 7.2 Endpoints

| Method and path | Purpose |
|---|---|
| `POST /chains` | Create a chain: `chain_id` hash, `chain_auth` public key, first device, keyring epoch 1 |
| `POST /devices` | Join, signed by the chain key or by a member. Uploads the device's public keys and an encrypted device name |
| `DELETE /devices/{id}` | Remove a device, signed by a member. Starts a re-key (§9) |
| `GET /keyring` | Wrapped data keys and the envelopes for this device |
| `GET /changes?since=<version>&limit=<n>` | Records changed after `version`, in version order |
| `POST /records` | Write a batch of `{id, epoch, body, if_version?}`, all or nothing |
| `PUT /blobs/{id}` / `GET /blobs/{id}` | Upload or download a blob |
| `GET /events` | Server-Sent Events: `changed <version>`, `keyring <epoch>`, `removed` |
| `POST /pair/...` | SPAKE2 relay (§8) |

### 7.3 Limits

The server enforces limits per chain: total bytes, number of records, and blob size. The defaults are 256 MiB, 100,000 records and 16 MiB per blob. Clients SHOULD keep within them by limiting history and by not syncing large files.

## 8. Adding a device

You can add a device in two ways.

1. **Pairing code (the usual way).** A device in the chain shows a 6-digit code, valid for 5 minutes. The new device types it in. The two devices run SPAKE2 with the code as the password, through the server's relay. They then use the shared key to exchange encrypted messages:
   - the new device sends its public keys;
   - the existing device sends `CS` and adds the new device as a member.

   The server sees only SPAKE2 messages. A wrong code fails after a single guess, and the server allows 5 attempts per code.
2. **Sync code.** Type the 24 words. The device derives the chain values, joins with the chain key and unwraps `DK` with `wrap_key`. This is the recovery path when no other device is at hand.

## 9. Removing a device and re-keying

Removing a device MUST re-key the chain, because the removed device knows `CS`.

1. A remaining device creates a new chain secret `CS'` and a new data key `DK'`.
2. It creates the new chain, which gets a new `chain_id`, and seals `CS'` in an envelope to each remaining device.
3. It re-encrypts every live record under `DK'` and uploads them to the new chain.
4. It writes a *forwarding record* to the old chain, sealed in an envelope to each remaining device. The record names the new chain, so the other devices move over by themselves.
5. It deletes the old chain.

The removed device keeps what it already downloaded, but it cannot read or write anything new. The app shows the new sync code and suggests that you write it down.

## 10. Privacy rules in the clients

- Snippets and folders sync once sync is on. Clipboard history syncs only if you turn it on.
- A clip is never synced, and is not even stored, if:
  - its source marks it as confidential: `ExcludeClipboardContentFromMonitorProcessing` or `CanIncludeInClipboardHistory = 0` on Windows, `org.nspasteboard.ConcealedType` on macOS;
  - it was copied in an excluded app.
- Synced history is capped at the smaller of the local history limit and 500 clips. Images sync only if you turn that on separately, and only up to 4 MiB each.
- Every device can see the list of devices in the chain, with their names. Device names are encrypted.

## 11. Versioning

Clients send `Kauri-Protocol: 1`. Whatever changes the record format or the key schedule raises the version. A server MAY serve several protocol versions side by side.
