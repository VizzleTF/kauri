using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kauri.Sync;

// The HTTP side of Kauri Sync v1 (docs/sync.md §7): signed requests, the
// endpoints, Server-Sent Events and the unsigned pairing requests.

public sealed class SyncApiException(int status, string message) : Exception(message.Length == 0 ? $"HTTP {status}" : $"{message} (HTTP {status})")
{
    public int Status { get; } = status;
    public string Error { get; } = message;
}

public sealed record DeviceInfo(
    [property: JsonPropertyName("sign")] string Sign,
    [property: JsonPropertyName("x25519")] string X25519,
    [property: JsonPropertyName("mlkem")] string MLKem,
    [property: JsonPropertyName("name")] string Name);

public sealed record DeviceEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("sign")] string Sign,
    [property: JsonPropertyName("x25519")] string X25519,
    [property: JsonPropertyName("mlkem")] string? MLKem,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("added")] long Added,
    [property: JsonPropertyName("removed")] long? Removed);

public sealed record RecordOut(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("epoch")] uint Epoch,
    [property: JsonPropertyName("deleted")] bool Deleted,
    [property: JsonPropertyName("body")] string Body);

public sealed record Changes(
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("records")] List<RecordOut> Records,
    [property: JsonPropertyName("more")] bool More);

public sealed record RecordIn(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("epoch")] uint Epoch,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("deleted")] bool Deleted,
    [property: JsonPropertyName("if_version")] long? IfVersion);

public sealed record PairSlotState(
    [property: JsonPropertyName("msg")] string? Msg,
    [property: JsonPropertyName("joined")] bool? Joined,
    [property: JsonPropertyName("bmsg")] string? BMsg,
    [property: JsonPropertyName("confirm")] string? Confirm,
    [property: JsonPropertyName("box")] string? Box,
    [property: JsonPropertyName("approval")] string? Approval);

public sealed class SyncClient(Uri server, KauriChain chain, KauriDevice device)
{
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public Uri Server { get; } = server;
    public KauriChain Chain { get; } = chain;
    public KauriDevice Device { get; } = device;

    public static Uri Url(Uri server, string path) => new(server.AbsoluteUri.TrimEnd('/') + path);

    HttpRequestMessage Signed(string method, string path, byte[]? body)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), Url(Server, path));
        long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = KauriCrypto.Random(16);
        req.Headers.Add("Kauri-Protocol", "1");
        req.Headers.Add("Kauri-Chain", KauriCrypto.B64(Chain.Id));
        req.Headers.Add("Kauri-Device", KauriCrypto.B64(Device.PublicKey));
        req.Headers.Add("Kauri-Time", time.ToString());
        req.Headers.Add("Kauri-Nonce", KauriCrypto.B64(nonce));
        req.Headers.Add("Kauri-Signature", KauriCrypto.B64(Device.SignRequest(method, path, time, nonce, body ?? [])));
        if (body != null)
        {
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        return req;
    }

    static async Task Check(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        string msg = "";
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) msg = e.GetString() ?? "";
        }
        catch (JsonException) { }
        throw new SyncApiException((int)resp.StatusCode, msg);
    }

    async Task<T?> Call<T>(string method, string path, object? body = null, CancellationToken ct = default)
    {
        var bytes = body == null ? null : JsonSerializer.SerializeToUtf8Bytes(body, Json);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var resp = await Http.SendAsync(Signed(method, path, bytes), cts.Token);
        await Check(resp);
        if (typeof(T) == typeof(object) || resp.StatusCode == HttpStatusCode.NoContent) return default;
        return await resp.Content.ReadJson<T>(cts.Token);
    }

    Task Call(string method, string path, object? body = null) => Call<object>(method, path, body);

    // ---------------- Chain and devices ----------------

    public DeviceInfo Info(string name)
    {
        var r = Device.Recipient;
        return new DeviceInfo(KauriCrypto.B64(Device.PublicKey), KauriCrypto.B64(r.X25519Key), KauriCrypto.B64(r.MLKemKey),
            KauriCrypto.B64(Chain.SealName(Device.Id, name)));
    }

    /// <summary>Registers a new chain with this device; returns the data key of epoch 1.</summary>
    public async Task<byte[]> CreateChain(string deviceName)
    {
        var dk = KauriCrypto.Random(32);
        var env = KauriEnvelope.Seal(Chain.Id, 1, Device.Recipient, dk);
        await Call("POST", "/v1/chains", new Dictionary<string, object>
        {
            ["chain_key"] = KauriCrypto.B64(Chain.AuthKey.PublicKey),
            ["chain_sig"] = KauriCrypto.B64(Chain.AddSignature(Device.PublicKey)),
            ["device"] = Info(deviceName),
            ["keyring"] = new Dictionary<string, object> { ["epoch"] = 1, ["wrapped"] = KauriCrypto.B64(Chain.Wrap(dk, 1)), ["envelope"] = KauriCrypto.B64(env) },
        });
        return dk;
    }

    /// <summary>Adds this device with the sync code (§8.2).</summary>
    public Task JoinWithSyncCode(string deviceName) => Call("POST", "/v1/devices", new Dictionary<string, object>
    {
        ["device"] = Info(deviceName),
        ["chain_sig"] = KauriCrypto.B64(Chain.AddSignature(Device.PublicKey)),
    });

    /// <summary>Adds another device as a member, with its envelope of the data key.</summary>
    public Task AddDevice(DeviceInfo info, uint epoch, byte[] dataKey)
    {
        var sign = KauriCrypto.UnB64(info.Sign) ?? throw new SyncCryptoException("bad device");
        var r = new KauriRecipient(KauriCrypto.Sha256(sign)[..16], KauriCrypto.UnB64(info.X25519) ?? [], KauriCrypto.UnB64(info.MLKem) ?? []);
        var env = KauriEnvelope.Seal(Chain.Id, epoch, r, dataKey);
        return Call("POST", "/v1/devices", new Dictionary<string, object> { ["device"] = info, ["envelope"] = KauriCrypto.B64(env) });
    }

    sealed record DevicesAnswer([property: JsonPropertyName("devices")] List<DeviceEntry> Devices);

    public async Task<List<DeviceEntry>> Devices() => (await Call<DevicesAnswer>("GET", "/v1/devices"))?.Devices ?? [];

    public Task RemoveDevice(string id) => Call("DELETE", "/v1/devices/" + id);

    sealed record Keyring(
        [property: JsonPropertyName("epoch")] uint Epoch,
        [property: JsonPropertyName("wrapped")] string Wrapped,
        [property: JsonPropertyName("envelope")] string? Envelope);

    /// <summary>The current epoch and its data key, from this device's envelope or the wrapped copy.</summary>
    public async Task<(uint Epoch, byte[] Key)> DataKey()
    {
        var k = await Call<Keyring>("GET", "/v1/keyring") ?? throw new SyncCryptoException("no keyring");
        if (KauriCrypto.UnB64(k.Envelope) is { Length: > 0 } env)
        {
            try
            {
                var key = Device.OpenEnvelope(Chain.Id, k.Epoch, env);
                if (key.Length == 32) return (k.Epoch, key);
            }
            catch (SyncCryptoException) { }
        }
        return (k.Epoch, Chain.Unwrap(KauriCrypto.UnB64(k.Wrapped) ?? throw new SyncCryptoException("bad keyring"), k.Epoch));
    }

    // ---------------- Records ----------------

    public async Task<Changes> GetChanges(long since, int limit = 500) =>
        await Call<Changes>("GET", $"/v1/changes?since={since}&limit={limit}") ?? new Changes(since, [], false);

    sealed record WriteAnswer([property: JsonPropertyName("versions")] List<long> Versions);

    public async Task<List<long>> Write(IReadOnlyList<RecordIn> records) =>
        (await Call<WriteAnswer>("POST", "/v1/records", new Dictionary<string, object> { ["records"] = records }))?.Versions ?? [];

    // ---------------- Moving to a new chain (§9) ----------------

    public Task Forward(IEnumerable<(byte[] DeviceId, byte[] Envelope)> envelopes) =>
        Call("POST", "/v1/forward", new Dictionary<string, object>
        {
            ["envelopes"] = envelopes.Select(e => new Dictionary<string, string> { ["device"] = KauriCrypto.B64(e.DeviceId), ["envelope"] = KauriCrypto.B64(e.Envelope) }).ToList(),
        });

    sealed record ForwardAnswer([property: JsonPropertyName("envelope")] string Envelope);

    /// <summary>The sync code of the chain this one moved to.</summary>
    public async Task<byte[]> ForwardedSecret()
    {
        var a = await Call<ForwardAnswer>("GET", "/v1/forward") ?? throw new SyncCryptoException("no forward");
        var s = Device.OpenEnvelope(Chain.Id, 0, KauriCrypto.UnB64(a.Envelope) ?? []);
        return s.Length == 32 ? s : throw new SyncCryptoException("bad forward");
    }

    // ---------------- Events (§7.3) ----------------

    /// <summary>Streams (event, data) pairs until the connection closes.</summary>
    public async IAsyncEnumerable<(string Event, string Data)> Events([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var req = Signed("GET", "/v1/events", null);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await Check(resp);
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string ev = "", data = "";
        while (true)
        {
            // The server sends a comment every 25 s: 90 s of silence means the connection is gone.
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(TimeSpan.FromSeconds(90));
            var line = await reader.ReadLineAsync(idle.Token);
            if (line == null) yield break;
            if (line.StartsWith("event: ")) ev = line[7..];
            else if (line.StartsWith("data: ")) data = line[6..];
            else if (line.Length == 0 && ev.Length > 0)
            {
                yield return (ev, data);
                ev = data = "";
            }
        }
    }

    // ---------------- Pairing (§8.1) ----------------

    public Task OpenPairing(string slot, byte[] message) =>
        Call("POST", "/v1/pair", new Dictionary<string, string> { ["slot"] = slot, ["msg"] = KauriCrypto.B64(message) });

    public Task ApprovePairing(string slot, byte[] box) =>
        Call("POST", $"/v1/pair/{slot}/approve", new Dictionary<string, string> { ["box"] = KauriCrypto.B64(box) });

    public Task ClosePairing(string slot) => Call("DELETE", "/v1/pair/" + slot);

    /// <summary>Unsigned: anyone with the slot may read it.</summary>
    public static async Task<PairSlotState> PairingState(Uri server, string slot)
    {
        using var resp = await Http.GetAsync(Url(server, "/v1/pair/" + slot));
        await Check(resp);
        return await resp.Content.ReadJson<PairSlotState>() ?? throw new SyncCryptoException("bad slot");
    }

    /// <summary>Unsigned: the new device answers a slot.</summary>
    public static async Task JoinPairing(Uri server, string slot, byte[] message, byte[] confirm, byte[] box)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["msg"] = KauriCrypto.B64(message), ["confirm"] = KauriCrypto.B64(confirm), ["box"] = KauriCrypto.B64(box),
        });
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var resp = await Http.PostAsync(Url(server, $"/v1/pair/{slot}/join"), content);
        await Check(resp);
    }
}

static class HttpJsonExtensions
{
    public static async Task<T?> ReadJson<T>(this HttpContent content, CancellationToken ct = default) =>
        JsonSerializer.Deserialize<T>(await content.ReadAsStringAsync(ct));
}
