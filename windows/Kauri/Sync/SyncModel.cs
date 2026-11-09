using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kauri.Core;

namespace Kauri.Sync;

// Records, clocks and order keys of Kauri Sync v1 (docs/sync.md §4-5), and
// the state this PC keeps between launches.

/// <summary>Hybrid logical clock value (§5.1): compared by wall, counter, device.</summary>
public readonly record struct Hlc(long Wall, int Counter, string Device) : IComparable<Hlc>
{
    public int CompareTo(Hlc o)
    {
        int c = Wall.CompareTo(o.Wall);
        if (c != 0) return c;
        c = Counter.CompareTo(o.Counter);
        return c != 0 ? c : string.CompareOrdinal(Device, o.Device);
    }

    public static bool operator <(Hlc a, Hlc b) => a.CompareTo(b) < 0;
    public static bool operator >(Hlc a, Hlc b) => a.CompareTo(b) > 0;

    public static Hlc? From(JsonNode? n)
    {
        if (n is not JsonArray a || a.Count != 3) return null;
        try { return new Hlc(a[0]!.GetValue<long>(), (int)a[1]!.GetValue<long>(), a[2]!.GetValue<string>()); }
        catch (Exception e) when (e is InvalidOperationException or FormatException) { return null; }
    }

    public JsonArray ToJson() => new(Wall, Counter, Device);
}

public sealed class HlcClock
{
    public long Wall { get; set; }
    public int Counter { get; set; }

    public Hlc Tick(string device, DateTimeOffset? now = null)
    {
        long ms = (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        if (ms > Wall) { Wall = ms; Counter = 0; }
        else if (++Counter > 65535) { Wall++; Counter = 0; }
        return new Hlc(Wall, Counter, device);
    }

    public void Observe(Hlc h)
    {
        if (h.Wall > Wall) { Wall = h.Wall; Counter = h.Counter; }
        else if (h.Wall == Wall) Counter = Math.Max(Counter, h.Counter);
    }
}

/// <summary>The plaintext of a record (§4.2).</summary>
public sealed class RecordBody
{
    public string Kind { get; }
    public Dictionary<string, (JsonNode? Value, Hlc Hlc)> Fields { get; } = new();
    public Hlc? Deleted { get; set; }
    /// <summary>Top-level keys this version does not know, written back unchanged.</summary>
    public Dictionary<string, JsonNode?> Extra { get; } = new();

    public RecordBody(string kind) => Kind = kind;

    public static RecordBody Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject o || o["k"]?.GetValue<string>() is not string kind) throw new SyncCryptoException("bad record");
        var b = new RecordBody(kind);
        foreach (var (name, node) in o)
        {
            switch (name)
            {
                case "k": break;
                case "d": b.Deleted = Hlc.From(node); break;
                case "f" when node is JsonObject f:
                    foreach (var (field, pair) in f)
                        if (pair is JsonArray a && a.Count == 2 && Hlc.From(a[1]) is Hlc h)
                            b.Fields[field] = (a[0]?.DeepClone(), h);
                    break;
                default: b.Extra[name] = node?.DeepClone(); break;
            }
        }
        return b;
    }

    public string ToJson()
    {
        var o = new JsonObject();
        foreach (var (k, v) in Extra.OrderBy(e => e.Key, StringComparer.Ordinal)) o[k] = v?.DeepClone();
        o["k"] = Kind;
        var f = new JsonObject();
        foreach (var (k, (v, h)) in Fields.OrderBy(e => e.Key, StringComparer.Ordinal)) f[k] = new JsonArray(v?.DeepClone(), h.ToJson());
        o["f"] = f;
        o["d"] = Deleted?.ToJson();
        return o.ToJsonString();
    }

    /// <summary>True if the deletion is newer than every field.</summary>
    public bool IsDeleted => Deleted is Hlc d && Fields.Values.All(f => f.Hlc < d);

    public Hlc? Newest
    {
        get
        {
            var all = Fields.Values.Select(f => f.Hlc).ToList();
            if (Deleted is Hlc d) all.Add(d);
            return all.Count == 0 ? null : all.Max();
        }
    }

    public JsonNode? this[string name] => Fields.TryGetValue(name, out var f) ? f.Value : null;
    public string? Str(string name) => this[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    public bool? Bool(string name) => this[name] is JsonValue v && v.TryGetValue(out bool b) ? b : null;
    public long? Long(string name) => this[name] is JsonValue v && v.TryGetValue(out long l) ? l : this[name] is JsonValue d && d.TryGetValue(out double x) ? (long)x : null;

    /// <summary>Sets a field to a new value at a fresh HLC, if the value differs.</summary>
    public bool Set(string name, JsonNode? value, Func<Hlc> hlc)
    {
        if (!IsDeleted && Fields.TryGetValue(name, out var f) && JsonNode.DeepEquals(f.Value, value)) return false;
        Fields[name] = (value, hlc());
        return true;
    }

    /// <summary>Field by field, the newer side wins (§5.1).</summary>
    public RecordBody Merged(RecordBody other)
    {
        var o = Parse(ToJson());
        foreach (var (name, f) in other.Fields)
            if (!o.Fields.TryGetValue(name, out var mine) || mine.Hlc < f.Hlc) o.Fields[name] = (f.Value?.DeepClone(), f.Hlc);
        if (other.Deleted is Hlc d && (o.Deleted is not Hlc md || md < d)) o.Deleted = d;
        foreach (var (k, v) in other.Extra) o.Extra[k] = v?.DeepClone();
        return o;
    }

    public bool SameAs(RecordBody other) => ToJson() == other.ToJson();
}

/// <summary>Fractional order keys (§5.2): strings of 0-9A-Za-z that sort by byte.</summary>
public static class OrderKey
{
    const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    static int Digit(string s, int i) => i < s.Length ? Math.Max(Digits.IndexOf(s[i]), 0) : 0;

    /// <summary>A key strictly between a and b (null: no bound). Never ends in "0".</summary>
    public static string Between(string? a, string? b)
    {
        a ??= "";
        var sb = new StringBuilder();
        bool bounded = b != null;
        for (int i = 0; i < 64; i++)
        {
            int da = Digit(a, i), db = bounded ? Digit(b!, i) : Digits.Length;
            if (db - da > 1)
            {
                sb.Append(Digits[(da + db) / 2]);
                return sb.ToString();
            }
            sb.Append(Digits[da]);
            if (db - da == 1) bounded = false;
        }
        return sb.Append(Digits[Digits.Length / 2]).ToString();
    }

    /// <summary>Keys for items in their local order: keeps the longest run already in order.</summary>
    public static List<string> Assign(IReadOnlyList<string?> keys)
    {
        var tails = new List<int>();
        var prev = Enumerable.Repeat(-1, keys.Count).ToArray();
        for (int i = 0; i < keys.Count; i++)
        {
            if (keys[i] is not string k) continue;
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (string.CompareOrdinal(keys[tails[mid]], k) < 0) lo = mid + 1; else hi = mid;
            }
            if (lo > 0) prev[i] = tails[lo - 1];
            if (lo == tails.Count) tails.Add(i); else tails[lo] = i;
        }
        var keep = new HashSet<int>();
        for (int j = tails.Count > 0 ? tails[^1] : -1; j >= 0; j = prev[j]) keep.Add(j);
        var o = new List<string>(keys.Count);
        string? lower = null;
        for (int i = 0; i < keys.Count; i++)
        {
            if (keep.Contains(i)) o.Add(keys[i]!);
            else
            {
                string? upper = null;
                for (int j = i + 1; j < keys.Count; j++) if (keep.Contains(j)) { upper = keys[j]; break; }
                o.Add(Between(lower, upper));
            }
            lower = o[^1];
        }
        return o;
    }
}

/// <summary>What this PC remembers about one record.</summary>
public sealed class SyncedRecord
{
    public string Kind { get; set; } = "";
    /// <summary>A folder or snippet UUID, or a clip's text hash.</summary>
    public string Local { get; set; } = "";
    public long Version { get; set; }
    public uint Epoch { get; set; }
    public bool Dirty { get; set; }
    public string Body { get; set; } = "";
    /// <summary>Clips: the local history item, or "" once it left the history without being deleted.</summary>
    public string? History { get; set; }
    /// <summary>Folders and snippets: true while the item exists locally.</summary>
    public bool Materialized { get; set; }
}

public sealed class SyncState
{
    public string Server { get; set; } = "";
    public string DeviceName { get; set; } = Environment.MachineName;
    public bool SyncsHistory { get; set; }
    public bool Enabled { get; set; }
    public long Since { get; set; }
    public HlcClock Clock { get; set; } = new();
    public Dictionary<string, SyncedRecord> Records { get; set; } = new();

    static string FilePath => Path.Combine(AppSettings.Directory, "sync.json");

    public static SyncState Load()
    {
        try { return JsonSerializer.Deserialize<SyncState>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppSettings.Directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

/// <summary>The chain secret and the device keys, encrypted with DPAPI for this Windows user.</summary>
public sealed class SyncSecrets
{
    public byte[] ChainSecret { get; set; } = [];
    public byte[] Sign { get; set; } = [];
    public byte[] X25519 { get; set; } = [];
    public byte[]? MLKemSeed { get; set; }

    static string FilePath => Path.Combine(AppSettings.Directory, "sync.keys");
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Kauri Clip sync keys");

    public static SyncSecrets Of(KauriChain chain, KauriDevice device) =>
        new() { ChainSecret = chain.Secret, Sign = device.Sign.Seed, X25519 = device.X25519Private, MLKemSeed = device.MLKemSeed };

    public KauriDevice Device => new(Sign, X25519, MLKemSeed);

    public static SyncSecrets? Load()
    {
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<SyncSecrets>(plain);
        }
        catch (Exception e) when (e is IOException or CryptographicException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppSettings.Directory);
        var data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(this), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, data);
    }

    public static void Delete()
    {
        try { File.Delete(FilePath); } catch (IOException) { }
    }
}
