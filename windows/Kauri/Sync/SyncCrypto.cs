using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Kauri.Sync;

// Kauri Sync v1 cryptography (docs/sync.md). Checked against
// docs/sync-vectors/v1.json by `Kauri.exe --sync-check`.

public sealed class SyncCryptoException(string message) : Exception(message);

/// <summary>The libsodium functions Kauri Sync uses (libsodium NuGet package, full build).</summary>
static class Sodium
{
    const string Lib = "libsodium";
    static readonly bool Ready = sodium_init() >= 0;

    public static void EnsureReady() { if (!Ready) throw new SyncCryptoException("libsodium failed to start"); }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int sodium_init();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int crypto_aead_xchacha20poly1305_ietf_encrypt(byte[] c, out ulong clen, byte[] m, ulong mlen, byte[] ad, ulong adlen, IntPtr nsec, byte[] npub, byte[] k);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int crypto_aead_xchacha20poly1305_ietf_decrypt(byte[] m, out ulong mlen, IntPtr nsec, byte[] c, ulong clen, byte[] ad, ulong adlen, byte[] npub, byte[] k);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int crypto_sign_seed_keypair(byte[] pk, byte[] sk, byte[] seed);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int crypto_sign_detached(byte[] sig, out ulong siglen, byte[] m, ulong mlen, byte[] sk);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int crypto_sign_verify_detached(byte[] sig, byte[] m, ulong mlen, byte[] pk);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int crypto_scalarmult_base(byte[] q, byte[] n);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int crypto_scalarmult(byte[] q, byte[] n, byte[] p);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void crypto_core_ed25519_scalar_reduce(byte[] r, byte[] s);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void crypto_core_ed25519_scalar_mul(byte[] z, byte[] x, byte[] y);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int crypto_scalarmult_ed25519_noclamp(byte[] q, byte[] n, byte[] p);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int crypto_scalarmult_ed25519_base_noclamp(byte[] q, byte[] n);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int crypto_core_ed25519_add(byte[] r, byte[] p, byte[] q);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int crypto_core_ed25519_sub(byte[] r, byte[] p, byte[] q);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int crypto_core_ed25519_is_valid_point(byte[] p);
}

public static class KauriCrypto
{
    public static readonly byte[] Salt = Encoding.UTF8.GetBytes("kauri-sync/v1");

    public static string B64(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? UnB64(string? s)
    {
        if (s == null) return null;
        var t = s.Replace('-', '+').Replace('_', '/');
        t += new string('=', (4 - t.Length % 4) % 4);
        try { return Convert.FromBase64String(t); } catch (FormatException) { return null; }
    }

    public static byte[] Hex(string h) => Convert.FromHexString(h);
    public static string ToHex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);
    public static byte[] U32(uint n) => [(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n];
    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    public static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] info, int count = 32) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, count, salt, info);

    public static byte[] Sha256(byte[] data) => SHA256.HashData(data);
    public static byte[] Hmac(byte[] key, byte[] message) => HMACSHA256.HashData(key, message);
    public static byte[] Random(int n) => RandomNumberGenerator.GetBytes(n);

    /// <summary>seal = nonce(24) || ciphertext || tag(16), XChaCha20-Poly1305.</summary>
    public static byte[] Seal(byte[] key, byte[] plaintext, byte[] aad, byte[]? nonce = null)
    {
        Sodium.EnsureReady();
        if (key.Length != 32) throw new SyncCryptoException("bad key");
        nonce ??= Random(24);
        var c = new byte[plaintext.Length + 16];
        Sodium.crypto_aead_xchacha20poly1305_ietf_encrypt(c, out _, plaintext, (ulong)plaintext.Length, aad, (ulong)aad.Length, IntPtr.Zero, nonce, key);
        return Cat(nonce, c);
    }

    public static byte[] Open(byte[] key, byte[] sealedBox, byte[] aad)
    {
        Sodium.EnsureReady();
        if (key.Length != 32 || sealedBox.Length < 40) throw new SyncCryptoException("bad length");
        var nonce = sealedBox[..24];
        var c = sealedBox[24..];
        var m = new byte[c.Length - 16];
        if (Sodium.crypto_aead_xchacha20poly1305_ietf_decrypt(m, out _, IntPtr.Zero, c, (ulong)c.Length, aad, (ulong)aad.Length, nonce, key) != 0)
            throw new SyncCryptoException("decryption failed");
        return m;
    }

    static readonly int[] Buckets = [256, 1024, 4096, 16384, 65536];

    public static byte[] Pad(byte[] p)
    {
        var n = Buckets.FirstOrDefault(b => p.Length + 1 <= b);
        if (n == 0) throw new SyncCryptoException("record too large");
        return PadTo(p, n);
    }

    public static byte[] Pad64(byte[] p) => PadTo(p, (p.Length / 65536 + 1) * 65536);

    static byte[] PadTo(byte[] p, int n)
    {
        var o = new byte[n];
        p.CopyTo(o, 0);
        o[p.Length] = 0x80;
        return o;
    }

    public static byte[] Unpad(byte[] p)
    {
        int i = p.Length - 1;
        while (i >= 0 && p[i] == 0) i--;
        if (i < 0 || p[i] != 0x80) throw new SyncCryptoException("bad padding");
        return p[..i];
    }
}

public static class Bip39
{
    static string[]? _words;

    static string[] Words => _words ??= LoadWords();

    static string[] LoadWords()
    {
        using var s = typeof(Bip39).Assembly.GetManifestResourceStream("Kauri.Sync.bip39_english.txt")
            ?? throw new SyncCryptoException("word list missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string Mnemonic(byte[] entropy)
    {
        var bits = entropy.Append(KauriCrypto.Sha256(entropy)[0]).ToArray();
        int total = entropy.Length * 8 + entropy.Length * 8 / 32;
        var words = new List<string>();
        for (int i = 0; i < total; i += 11)
        {
            int idx = 0;
            for (int j = 0; j < 11; j++)
            {
                int b = i + j;
                if ((bits[b / 8] & (0x80 >> (b % 8))) != 0) idx |= 1 << (10 - j);
            }
            words.Add(Words[idx]);
        }
        return string.Join(' ', words);
    }

    public static byte[] Entropy(string mnemonic)
    {
        var words = mnemonic.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length != 24) throw new SyncCryptoException("A sync code has 24 words.");
        var bits = new byte[33];
        for (int i = 0; i < words.Length; i++)
        {
            int idx = Array.IndexOf(Words, words[i]);
            if (idx < 0) throw new SyncCryptoException($"“{words[i]}” is not a sync code word.");
            for (int j = 0; j < 11; j++)
            {
                if ((idx & (1 << (10 - j))) == 0) continue;
                int b = i * 11 + j;
                bits[b / 8] |= (byte)(0x80 >> (b % 8));
            }
        }
        var entropy = bits[..32];
        if (KauriCrypto.Sha256(entropy)[0] != bits[32]) throw new SyncCryptoException("The sync code has a typo.");
        return entropy;
    }
}

/// <summary>Ed25519 key pair from a 32-byte seed.</summary>
public sealed class SigningKey
{
    public byte[] Seed { get; }
    public byte[] PublicKey { get; }
    readonly byte[] _secret;

    public SigningKey(byte[] seed)
    {
        Sodium.EnsureReady();
        Seed = seed;
        PublicKey = new byte[32];
        _secret = new byte[64];
        Sodium.crypto_sign_seed_keypair(PublicKey, _secret, seed);
    }

    public byte[] Sign(byte[] message)
    {
        var sig = new byte[64];
        Sodium.crypto_sign_detached(sig, out _, message, (ulong)message.Length, _secret);
        return sig;
    }

    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature) =>
        signature.Length == 64 && Sodium.crypto_sign_verify_detached(signature, message, (ulong)message.Length, publicKey) == 0;
}

public static class X25519
{
    public static byte[] PublicKey(byte[] priv)
    {
        Sodium.EnsureReady();
        var q = new byte[32];
        Sodium.crypto_scalarmult_base(q, priv);
        return q;
    }

    /// <summary>Fails when the shared secret is all zeros.</summary>
    public static byte[] Agree(byte[] priv, byte[] pub)
    {
        Sodium.EnsureReady();
        var q = new byte[32];
        if (pub.Length != 32 || Sodium.crypto_scalarmult(q, priv, pub) != 0) throw new SyncCryptoException("bad X25519 key");
        return q;
    }
}

public sealed class KauriChain
{
    public byte[] Secret { get; }
    public byte[] Id { get; }
    public SigningKey AuthKey { get; }
    public byte[] WrapKey { get; }
    public byte[] IdKey { get; }
    public byte[] NameKey { get; }

    public KauriChain(byte[] secret)
    {
        if (secret.Length != 32) throw new SyncCryptoException("bad chain secret");
        Secret = secret;
        byte[] D(string info) => KauriCrypto.Hkdf(secret, KauriCrypto.Salt, KauriCrypto.Utf8(info));
        Id = D("chain-id");
        AuthKey = new SigningKey(D("chain-auth"));
        WrapKey = D("wrap-key");
        IdKey = D("record-id-key");
        NameKey = D("device-name-key");
    }

    public static KauriChain FromWords(string words) => new(Bip39.Entropy(words));
    public static KauriChain New() => new(KauriCrypto.Random(32));

    public string Words => Bip39.Mnemonic(Secret);
    public byte[] IdHash => KauriCrypto.Sha256(Id);

    public static byte[] AddMessage(byte[] chainId, byte[] device) => KauriCrypto.Cat(KauriCrypto.Utf8("kauri-sync/v1/add\0"), chainId, device);
    public byte[] AddSignature(byte[] device) => AuthKey.Sign(AddMessage(Id, device));

    public byte[] RecordId(string kind, string local) => KauriCrypto.Hmac(IdKey, KauriCrypto.Utf8($"{kind}:{local.ToLowerInvariant()}"))[..16];
    public byte[] BlobId(byte[] content) => KauriCrypto.Hmac(IdKey, KauriCrypto.Cat(KauriCrypto.Utf8("blob:"), KauriCrypto.Sha256(content)))[..16];
    public byte[] RecordAad(byte[] recordId, uint epoch) => KauriCrypto.Cat(Id, recordId, KauriCrypto.U32(epoch));

    public byte[] SealRecord(byte[] dataKey, uint epoch, byte[] recordId, byte[] plaintext) =>
        KauriCrypto.Seal(dataKey, KauriCrypto.Pad(plaintext), RecordAad(recordId, epoch));

    public byte[] OpenRecord(byte[] dataKey, uint epoch, byte[] recordId, byte[] body) =>
        KauriCrypto.Unpad(KauriCrypto.Open(dataKey, body, RecordAad(recordId, epoch)));

    byte[] KeyringAad(uint epoch) => KauriCrypto.Cat(KauriCrypto.Utf8("kauri-sync/v1/keyring"), Id, KauriCrypto.U32(epoch));
    public byte[] Wrap(byte[] dataKey, uint epoch) => KauriCrypto.Seal(WrapKey, dataKey, KeyringAad(epoch));

    public byte[] Unwrap(byte[] wrapped, uint epoch)
    {
        var k = KauriCrypto.Open(WrapKey, wrapped, KeyringAad(epoch));
        return k.Length == 32 ? k : throw new SyncCryptoException("bad data key");
    }

    public byte[] SealName(byte[] deviceId, string name) =>
        KauriCrypto.Seal(NameKey, KauriCrypto.Utf8(name), KauriCrypto.Cat(KauriCrypto.Utf8("kauri-sync/v1/name"), deviceId));

    public string? OpenName(byte[] deviceId, byte[] sealedName)
    {
        try { return Encoding.UTF8.GetString(KauriCrypto.Open(NameKey, sealedName, KauriCrypto.Cat(KauriCrypto.Utf8("kauri-sync/v1/name"), deviceId))); }
        catch (SyncCryptoException) { return null; }
    }
}

public sealed record KauriRecipient(byte[] Id, byte[] X25519Key, byte[] MLKemKey);

public sealed class KauriDevice
{
    public SigningKey Sign { get; }
    public byte[] X25519Private { get; }
    /// <summary>64 bytes (d || z), or null where ML-KEM is not available.</summary>
    public byte[]? MLKemSeed { get; }

    public KauriDevice(byte[] signSeed, byte[] x25519Private, byte[]? mlkemSeed)
    {
        Sign = new SigningKey(signSeed);
        X25519Private = x25519Private;
        MLKemSeed = mlkemSeed;
    }

    public static KauriDevice New() =>
        new(KauriCrypto.Random(32), KauriCrypto.Random(32), MLKem.IsSupported ? KauriCrypto.Random(64) : null);

    public byte[] PublicKey => Sign.PublicKey;
    public byte[] Id => KauriCrypto.Sha256(PublicKey)[..16];

    public byte[] MLKemPublic
    {
        get
        {
            if (MLKemSeed == null || !MLKem.IsSupported) return [];
            using var k = MLKem.ImportPrivateSeed(MLKemAlgorithm.MLKem768, MLKemSeed);
            return k.ExportEncapsulationKey();
        }
    }

    public KauriRecipient Recipient => new(Id, X25519.PublicKey(X25519Private), MLKemPublic);

    public byte[] SignRequest(string method, string path, long time, byte[] nonce, byte[] body) =>
        Sign.Sign(KauriRequest.Message(method, path, time, nonce, body));

    public byte[] OpenEnvelope(byte[] chainId, uint epoch, byte[] envelope)
    {
        if (envelope.Length <= 33) throw new SyncCryptoException("bad envelope");
        var e = envelope[1..33];
        var rest = envelope[33..];
        byte[] ctk = [], ssk = [];
        switch (envelope[0])
        {
            case 0x01:
                break;
            case 0x02:
                if (MLKemSeed == null || !MLKem.IsSupported || rest.Length <= 1088) throw new SyncCryptoException("no ML-KEM key");
                ctk = rest[..1088];
                rest = rest[1088..];
                using (var k = MLKem.ImportPrivateSeed(MLKemAlgorithm.MLKem768, MLKemSeed)) ssk = k.Decapsulate(ctk);
                break;
            default:
                throw new SyncCryptoException("unknown envelope");
        }
        var ssx = X25519.Agree(X25519Private, e);
        var key = KauriEnvelope.Key(ssx, ssk, e, ctk, X25519.PublicKey(X25519Private), Id);
        return KauriCrypto.Open(key, rest, KauriCrypto.Cat(chainId, KauriCrypto.U32(epoch), Id));
    }
}

public static class KauriEnvelope
{
    public static byte[] Key(byte[] ssx, byte[] ssk, byte[] e, byte[] ctk, byte[] recipientX, byte[] deviceId) =>
        KauriCrypto.Hkdf(KauriCrypto.Cat(ssx, ssk), KauriCrypto.Utf8("kauri-sync/v1/envelope"), KauriCrypto.Cat(e, ctk, recipientX, deviceId));

    public static byte[] Seal(byte[] chainId, uint epoch, KauriRecipient r, byte[] payload)
    {
        var eph = KauriCrypto.Random(32);
        var e = X25519.PublicKey(eph);
        var ssx = X25519.Agree(eph, r.X25519Key);
        byte[] ctk = [], ssk = [];
        byte kind = 0x01;
        if (r.MLKemKey.Length > 0)
        {
            if (!MLKem.IsSupported) throw new SyncCryptoException("ML-KEM is not available on this PC");
            using var pub = MLKem.ImportEncapsulationKey(MLKemAlgorithm.MLKem768, r.MLKemKey);
            pub.Encapsulate(out ctk, out ssk);
            kind = 0x02;
        }
        var key = Key(ssx, ssk, e, ctk, r.X25519Key, r.Id);
        return KauriCrypto.Cat([kind], e, ctk, KauriCrypto.Seal(key, payload, KauriCrypto.Cat(chainId, KauriCrypto.U32(epoch), r.Id)));
    }
}

public static class KauriRequest
{
    public static byte[] Message(string method, string path, long time, byte[] nonce, byte[] body) =>
        KauriCrypto.Cat(KauriCrypto.Utf8($"kauri-sync/v1/request\0{method}\0{path}\0{time}\0"), nonce, KauriCrypto.Sha256(body));
}

/// <summary>Pairing (docs/sync.md §8.1): SPAKE2 (RFC 9382) over edwards25519.</summary>
public sealed class KauriPairing
{
    const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    static readonly byte[] PointM = KauriCrypto.Hex("d048032c6ea0b6d697ddc2e86bda85a33adac920f1bf18e1b0c6d166a5cecdaf");
    static readonly byte[] PointN = KauriCrypto.Hex("d3bfb518f44f3430f29d0c92af503865a1ed3281dc69b35dd868ba85f886c4ab");

    public string Slot { get; }
    public bool Existing { get; }
    public byte[] Message { get; }
    readonly byte[] _w, _x;

    public sealed record Keys(byte[] ConfirmB, byte[] BoxKey, int Number)
    {
        public bool CheckConfirm(byte[] c) => CryptographicOperations.FixedTimeEquals(c, ConfirmB);
    }

    public static string NewCode()
    {
        var r = KauriCrypto.Random(12);
        var sb = new StringBuilder();
        for (int i = 0; i < 12; i++)
        {
            if (i == 4 || i == 8) sb.Append('-');
            sb.Append(Crockford[r[i] & 31]);
        }
        return sb.ToString();
    }

    /// <summary>Upper-cases a typed code, maps O to 0 and I, L to 1, drops separators.</summary>
    public static string? Normalize(string code)
    {
        var sb = new StringBuilder();
        foreach (var ch in code.ToUpperInvariant())
        {
            switch (ch)
            {
                case '-' or ' ': continue;
                case 'O': sb.Append('0'); break;
                case 'I' or 'L': sb.Append('1'); break;
                default:
                    if (!Crockford.Contains(ch)) return null;
                    sb.Append(ch);
                    break;
            }
        }
        return sb.Length == 12 ? sb.ToString() : null;
    }

    public static string SlotFor(string code) => KauriCrypto.ToHex(KauriCrypto.Sha256(KauriCrypto.Utf8("kauri-pair-slot-v1\0" + code[..4])));

    static byte[] Reduce(byte[] wide)
    {
        var r = new byte[32];
        Sodium.crypto_core_ed25519_scalar_reduce(r, wide);
        return r;
    }

    static byte[]? Mul(byte[] n, byte[] p)
    {
        var q = new byte[32];
        return Sodium.crypto_scalarmult_ed25519_noclamp(q, n, p) == 0 ? q : null;
    }

    /// <summary><paramref name="code"/> must be normalized. <paramref name="existing"/> is true on the device already in the chain.</summary>
    public KauriPairing(string code, bool existing, byte[]? seed = null)
    {
        Sodium.EnsureReady();
        Slot = SlotFor(code);
        Existing = existing;
        _w = Reduce(SHA512.HashData(KauriCrypto.Utf8($"kauri-pair-v1\0{Slot}\0{code[4..]}")));
        _x = Reduce(seed ?? KauriCrypto.Random(64));
        var mask = existing ? PointM : PointN;
        var gx = new byte[32];
        Sodium.crypto_scalarmult_ed25519_base_noclamp(gx, _x);
        var msg = new byte[32];
        Sodium.crypto_core_ed25519_add(msg, Mul(_w, mask)!, gx);
        Message = msg;
    }

    public Keys Finish(byte[] peer)
    {
        if (peer.Length != 32 || Sodium.crypto_core_ed25519_is_valid_point(peer) != 1) throw new SyncCryptoException("bad point");
        var peerMask = Existing ? PointN : PointM;
        var t = new byte[32];
        if (Sodium.crypto_core_ed25519_sub(t, peer, Mul(_w, peerMask)!) != 0) throw new SyncCryptoException("bad point");
        var eight = new byte[32];
        eight[0] = 8;
        var x8 = new byte[32];
        Sodium.crypto_core_ed25519_scalar_mul(x8, eight, _x);
        var k = Mul(x8, t) ?? throw new SyncCryptoException("bad point");
        var (pA, pB) = Existing ? (Message, peer) : (peer, Message);
        var tt = new List<byte>();
        foreach (var part in new[] { KauriCrypto.Utf8("kauri-pair-existing"), KauriCrypto.Utf8("kauri-pair-new"), pA, pB, k, _w.Reverse().ToArray() })
        {
            tt.AddRange(BitConverter.GetBytes((ulong)part.Length)); // little-endian on Windows
            tt.AddRange(part);
        }
        var transcript = tt.ToArray();
        var h = KauriCrypto.Sha256(transcript);
        var ke = h[..16];
        var ka = h[16..];
        var kc = KauriCrypto.Hkdf(ka, new byte[32], KauriCrypto.Utf8("ConfirmationKeys" + Slot));
        var confirm = KauriCrypto.Hmac(kc[16..], transcript);
        var salt = KauriCrypto.Utf8("kauri-pair-v1");
        var box = KauriCrypto.Hkdf(ke, salt, KauriCrypto.Utf8("box"));
        var v = KauriCrypto.Hkdf(ke, salt, KauriCrypto.Utf8("verify"), 4);
        int number = (int)(((uint)v[0] << 24 | (uint)v[1] << 16 | (uint)v[2] << 8 | v[3]) % 10000);
        return new Keys(confirm, box, number);
    }

    public static byte[] SealJoin(Keys k, string slot, byte[] info) => KauriCrypto.Seal(k.BoxKey, info, KauriCrypto.Utf8("kauri-pair-v1/join" + slot));
    public static byte[] OpenJoin(Keys k, string slot, byte[] box) => KauriCrypto.Open(k.BoxKey, box, KauriCrypto.Utf8("kauri-pair-v1/join" + slot));

    public static byte[] SealApproval(Keys k, string slot, byte[] secret, byte[]? nonce = null) =>
        KauriCrypto.Seal(k.BoxKey, secret, KauriCrypto.Utf8("kauri-pair-v1/approve" + slot), nonce);

    public static byte[] OpenApproval(Keys k, string slot, byte[] box)
    {
        var s = KauriCrypto.Open(k.BoxKey, box, KauriCrypto.Utf8("kauri-pair-v1/approve" + slot));
        return s.Length == 32 ? s : throw new SyncCryptoException("bad approval");
    }
}
