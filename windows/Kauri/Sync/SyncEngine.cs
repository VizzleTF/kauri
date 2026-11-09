using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Kauri.Core;

namespace Kauri.Sync;

/// <summary>
/// Keeps snippets, folders and (if turned on) text clips in sync with a Kauri Sync
/// server. The local database stays the source of truth for the app; this class
/// compares it with the records it knows, pushes local edits and applies remote
/// ones (docs/sync.md). Everything runs on the UI thread, between awaits.
/// </summary>
public sealed class SyncEngine
{
    public static SyncEngine Shared { get; } = new();

    public enum SyncStatus { Off, Connecting, Syncing, Synced, Offline }

    public sealed record DeviceRow(string Id, string Name, bool IsThisDevice, bool IsPostQuantum);

    public SyncStatus Status { get; private set; } = SyncStatus.Off;
    public string? LastError { get; private set; }
    public IReadOnlyList<DeviceRow> Devices { get; private set; } = [];
    public SyncState State { get; private set; } = SyncState.Load();
    /// <summary>Raised on the UI thread when the status, the devices or the state change.</summary>
    public event Action? Changed;
    /// <summary>The chain moved; the argument is the new sync code to show.</summary>
    public event Action<string>? NewSyncCode;
    /// <summary>Another device removed this one.</summary>
    public event Action? Removed;

    public const int HistoryCap = 500;
    static readonly string[] Kinds = ["folder", "snippet", "clip"];

    SyncSecrets? _secrets;
    KauriChain? _chain;
    KauriDevice? _device;
    readonly Dictionary<uint, byte[]> _dataKeys = new();
    uint _epoch;
    CancellationTokenSource? _loop;
    readonly HashSet<string> _deletedHistory = new();
    readonly DispatcherTimer _debounce;
    bool _running, _again, _hooked;

    SyncEngine()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await Sync(pull: false); };
    }

    public bool IsOn => State.Enabled && _secrets != null;
    public string? SyncCode => _chain?.Words;

    void SetStatus(SyncStatus s, string? error = null)
    {
        Status = s;
        LastError = error;
        Changed?.Invoke();
    }

    // ---------------- Lifecycle ----------------

    public void Start()
    {
        if (!_hooked)
        {
            _hooked = true;
            Database.Shared.SnippetsChanged += Touch;
            Database.Shared.HistoriesChanged += () => { if (State.SyncsHistory) Touch(); };
            Database.Shared.HistoryDeleted += id => _deletedHistory.Add(id);
        }
        _secrets = SyncSecrets.Load();
        if (!State.Enabled || _secrets == null) { SetStatus(SyncStatus.Off); return; }
        _device = _secrets.Device;
        _chain = new KauriChain(_secrets.ChainSecret);
        _loop?.Cancel();
        _loop = new CancellationTokenSource();
        _ = Run(_loop.Token);
    }

    void Stop()
    {
        _loop?.Cancel();
        _loop = null;
        SetStatus(SyncStatus.Off);
    }

    /// <summary>Local edits are batched over 2 seconds (docs/sync.md §10).</summary>
    void Touch()
    {
        if (!IsOn) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
    }

    SyncClient? Client() =>
        _chain != null && _device != null && Uri.TryCreate(State.Server, UriKind.Absolute, out var u) ? new SyncClient(u, _chain, _device) : null;

    async Task Run(CancellationToken ct)
    {
        int delay = 2;
        while (!ct.IsCancellationRequested && IsOn && Client() is SyncClient client)
        {
            try
            {
                SetStatus(SyncStatus.Connecting);
                await LoadDataKey(client);
                await RefreshDevices();
                await foreach (var (ev, _) in client.Events(ct))
                {
                    delay = 2;
                    switch (ev)
                    {
                        case "ready" or "changed": await Sync(pull: true); break;
                        case "devices": await RefreshDevices(); break;
                        case "keyring": await LoadDataKey(client); break;
                        case "removed": Forget(removed: true); return;
                        case "forwarded": await FollowForward(); return;
                    }
                }
            }
            catch (SyncApiException e) when (e.Status == 403) { Forget(removed: true); return; }
            catch (SyncApiException e) when (e.Status == 410 && e.Error == "forwarded")
            {
                try { await FollowForward(); return; } catch (Exception f) { SetStatus(SyncStatus.Offline, f.Message); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { SetStatus(SyncStatus.Offline, e.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { return; }
            delay = Math.Min(delay * 2, 300);
        }
    }

    async Task LoadDataKey(SyncClient client)
    {
        var (e, key) = await client.DataKey();
        _epoch = e;
        _dataKeys[e] = key;
    }

    // ---------------- Turning sync on and off ----------------

    public static Uri CheckServer(string server)
    {
        if (!Uri.TryCreate(server.Trim(), UriKind.Absolute, out var u) ||
            !(u.Scheme == "https" || (u.Scheme == "http" && u.IsLoopback)))
            throw new SyncApiException(0, "The server must be an https:// address.");
        return u;
    }

    /// <summary>Creates a chain on the server with this PC and turns sync on.</summary>
    public async Task CreateChain(string server)
    {
        var url = CheckServer(server);
        var chain = KauriChain.New();
        var device = KauriDevice.New();
        var key = await new SyncClient(url, chain, device).CreateChain(State.DeviceName);
        Enable(url, chain, device);
        _dataKeys.Clear();
        _dataKeys[1] = key;
        _epoch = 1;
        Start();
        NewSyncCode?.Invoke(chain.Words);
    }

    /// <summary>Joins the chain of the 24 words (§8.2).</summary>
    public async Task Join(string server, string words)
    {
        var url = CheckServer(server);
        var chain = KauriChain.FromWords(words);
        var device = KauriDevice.New();
        await new SyncClient(url, chain, device).JoinWithSyncCode(State.DeviceName);
        Enable(url, chain, device);
        Start();
    }

    void Enable(Uri server, KauriChain chain, KauriDevice device)
    {
        SyncSecrets.Of(chain, device).Save();
        State.Server = server.AbsoluteUri.TrimEnd('/');
        State.Enabled = true;
        State.Since = 0;
        foreach (var r in State.Records.Values)
        {
            r.Version = 0;
            r.Dirty = !RecordBody.Parse(r.Body).IsDeleted;
        }
        State.Save();
        _chain = chain;
        _device = device;
    }

    /// <summary>Leaves the chain. Local data stays.</summary>
    public async Task TurnOff()
    {
        if (Client() is SyncClient c && _device != null)
            try { await c.RemoveDevice(KauriCrypto.B64(_device.Id)); } catch (Exception) { }
        Forget(removed: false);
    }

    void Forget(bool removed)
    {
        Stop();
        SyncSecrets.Delete();
        _secrets = null;
        _chain = null;
        _device = null;
        _dataKeys.Clear();
        Devices = [];
        State = new SyncState { Server = State.Server, SyncsHistory = State.SyncsHistory };
        State.Save();
        Changed?.Invoke();
        if (removed) Removed?.Invoke();
    }

    public void SetSyncsHistory(bool on)
    {
        State.SyncsHistory = on;
        State.Save();
        _ = Sync(pull: false);
    }

    public void SetServer(string server)
    {
        State.Server = server.Trim();
        State.Save();
    }

    // ---------------- Devices ----------------

    public async Task RefreshDevices()
    {
        if (Client() is not SyncClient c || _chain == null || _device == null) return;
        List<DeviceEntry> list;
        try { list = await c.Devices(); } catch (Exception) { return; }
        var me = KauriCrypto.B64(_device.Id);
        Devices = list.Where(d => d.Removed == null).Select(d =>
            {
                var name = KauriCrypto.UnB64(d.Name) is byte[] n && KauriCrypto.UnB64(d.Id) is byte[] id ? _chain.OpenName(id, n) : null;
                return new DeviceRow(d.Id, name ?? "?", d.Id == me, !string.IsNullOrEmpty(d.MLKem));
            })
            .OrderBy(d => d.IsThisDevice ? 0 : 1).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Changed?.Invoke();
    }

    /// <summary>Removes another device and moves the chain to a new sync code (§9).</summary>
    public async Task RemoveDevice(string id)
    {
        if (Client() is not SyncClient old || _device == null) return;
        await old.RemoveDevice(id);
        var me = KauriCrypto.B64(_device.Id);
        var remaining = (await old.Devices()).Where(d => d.Removed == null && d.Id != me && d.Id != id).ToList();

        var next = KauriChain.New();
        var nextClient = new SyncClient(old.Server, next, _device);
        var key = await nextClient.CreateChain(State.DeviceName);
        _loop?.Cancel();
        Rekey(next);
        _chain = next;
        _dataKeys.Clear();
        _dataKeys[1] = key;
        _epoch = 1;
        await Push(nextClient);

        var envelopes = remaining.Select(d =>
        {
            var sign = KauriCrypto.UnB64(d.Sign) ?? throw new SyncCryptoException("bad device");
            var r = new KauriRecipient(KauriCrypto.Sha256(sign)[..16], KauriCrypto.UnB64(d.X25519) ?? [], KauriCrypto.UnB64(d.MLKem) ?? []);
            return (r.Id, KauriEnvelope.Seal(old.Chain.Id, 0, r, next.Secret));
        }).ToList();
        if (envelopes.Count > 0) await old.Forward(envelopes);
        SyncSecrets.Of(next, _device).Save();
        Start();
        NewSyncCode?.Invoke(next.Words);
    }

    /// <summary>Another device moved the chain: open our envelope and join the new one.</summary>
    async Task FollowForward()
    {
        if (Client() is not SyncClient old || _device == null) return;
        var next = new KauriChain(await old.ForwardedSecret());
        await new SyncClient(old.Server, next, _device).JoinWithSyncCode(State.DeviceName);
        SyncSecrets.Of(next, _device).Save();
        Rekey(next);
        _chain = next;
        _dataKeys.Clear();
        Start();
        NewSyncCode?.Invoke(next.Words);
    }

    /// <summary>Moves every live record to the IDs of the next chain; all of them need uploading.</summary>
    void Rekey(KauriChain next)
    {
        var map = new Dictionary<string, string>();
        foreach (var (id, r) in State.Records)
            if (!RecordBody.Parse(r.Body).IsDeleted) map[id] = KauriCrypto.B64(next.RecordId(r.Kind, r.Local));
        var records = new Dictionary<string, SyncedRecord>();
        foreach (var (id, r) in State.Records)
        {
            if (!map.TryGetValue(id, out var newId)) continue;
            var body = RecordBody.Parse(r.Body);
            if (body.Fields.TryGetValue("folder", out var f) && f.Value is JsonValue v && v.TryGetValue(out string? folder) && map.TryGetValue(folder, out var nf))
                body.Fields["folder"] = (JsonValue.Create(nf), f.Hlc);
            r.Body = body.ToJson();
            r.Version = 0;
            r.Dirty = true;
            records[newId] = r;
        }
        State.Records = records;
        State.Since = 0;
        State.Save();
    }

    // ---------------- Pairing (§8.1) ----------------

    public sealed record PairingRequest(string Code, KauriPairing Pairing);
    public sealed record PairingJoin(string Name, int Number, KauriPairing.Keys Keys, string Slot, DeviceInfo Info);
    public sealed record PendingJoin(int Number, KauriPairing.Keys Keys, string Slot, KauriDevice Device, Uri Server);

    /// <summary>On this (existing) device: shows a code for a new device.</summary>
    public async Task<PairingRequest> OpenPairing()
    {
        var c = Client() ?? throw new InvalidOperationException("sync is off");
        var code = KauriPairing.NewCode();
        var p = new KauriPairing(KauriPairing.Normalize(code)!, existing: true);
        await c.OpenPairing(p.Slot, p.Message);
        return new PairingRequest(code, p);
    }

    /// <summary>Waits until the new device joins the slot and checks its confirmation.</summary>
    public async Task<PairingJoin> WaitForJoin(PairingRequest req, CancellationToken ct)
    {
        var url = CheckServer(State.Server);
        var slot = req.Pairing.Slot;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var s = await SyncClient.PairingState(url, slot);
            if (s.Joined == true && KauriCrypto.UnB64(s.BMsg) is byte[] bmsg && KauriCrypto.UnB64(s.Confirm) is byte[] confirm && KauriCrypto.UnB64(s.Box) is byte[] box)
            {
                var keys = req.Pairing.Finish(bmsg);
                if (!keys.CheckConfirm(confirm))
                {
                    try { await Client()!.ClosePairing(slot); } catch (Exception) { }
                    throw new SyncCryptoException("The pairing was not confirmed. Try a new code.");
                }
                var info = JsonSerializer.Deserialize<DeviceInfo>(KauriPairing.OpenJoin(keys, slot, box)) ?? throw new SyncCryptoException("bad join");
                return new PairingJoin(info.Name, keys.Number, keys, slot, info);
            }
            await Task.Delay(2000, ct);
        }
    }

    public async Task Allow(PairingJoin join)
    {
        var c = Client() ?? throw new InvalidOperationException("sync is off");
        if (!_dataKeys.TryGetValue(_epoch, out var key)) { await LoadDataKey(c); key = _dataKeys[_epoch]; }
        var sign = KauriCrypto.UnB64(join.Info.Sign) ?? throw new SyncCryptoException("bad device");
        var name = join.Name.Length > 64 ? join.Name[..64] : join.Name;
        var info = join.Info with { Name = KauriCrypto.B64(c.Chain.SealName(KauriCrypto.Sha256(sign)[..16], name)) };
        await c.AddDevice(info, _epoch, key);
        await c.ApprovePairing(join.Slot, KauriPairing.SealApproval(join.Keys, join.Slot, c.Chain.Secret));
        await RefreshDevices();
    }

    public async Task ClosePairing(string slot)
    {
        try { if (Client() is SyncClient c) await c.ClosePairing(slot); } catch (Exception) { }
    }

    /// <summary>On the new device: answers the slot of the code.</summary>
    public async Task<PendingJoin> JoinWithCode(string server, string code)
    {
        var url = CheckServer(server);
        var normalized = KauriPairing.Normalize(code) ?? throw new SyncCryptoException("The code is not valid.");
        var slot = KauriPairing.SlotFor(normalized);
        var s = await SyncClient.PairingState(url, slot);
        var pA = KauriCrypto.UnB64(s.Msg) ?? throw new SyncCryptoException("The code is not valid.");
        var p = new KauriPairing(normalized, existing: false);
        var keys = p.Finish(pA);
        var device = KauriDevice.New();
        var r = device.Recipient;
        var info = new DeviceInfo(KauriCrypto.B64(device.PublicKey), KauriCrypto.B64(r.X25519Key), KauriCrypto.B64(r.MLKemKey), State.DeviceName);
        var box = KauriPairing.SealJoin(keys, slot, JsonSerializer.SerializeToUtf8Bytes(info));
        await SyncClient.JoinPairing(url, slot, p.Message, keys.ConfirmB, box);
        return new PendingJoin(keys.Number, keys, slot, device, url);
    }

    /// <summary>Waits for the other device to allow this one, then turns sync on.</summary>
    public async Task WaitForApproval(PendingJoin join, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var s = await SyncClient.PairingState(join.Server, join.Slot);
            if (KauriCrypto.UnB64(s.Approval) is { Length: > 0 } box)
            {
                var secret = KauriPairing.OpenApproval(join.Keys, join.Slot, box);
                Enable(join.Server, new KauriChain(secret), join.Device);
                Start();
                return;
            }
            await Task.Delay(2000, ct);
        }
    }

    // ---------------- Sync ----------------

    /// <summary>Pulls (if asked), compares the local data with the records and pushes.</summary>
    public async Task Sync(bool pull)
    {
        if (!IsOn || Client() is not SyncClient client) return;
        if (_running) { _again = true; return; }
        _running = true;
        try
        {
            do
            {
                _again = false;
                SetStatus(SyncStatus.Syncing);
                try
                {
                    if (!_dataKeys.ContainsKey(_epoch)) await LoadDataKey(client);
                    if (pull || State.Since == 0) await Pull(client);
                    Scan();
                    await Push(client);
                    SetStatus(SyncStatus.Synced);
                }
                catch (SyncApiException e) when (e.Status == 403) { Forget(removed: true); return; }
                catch (SyncApiException e) when (e.Status == 410 && e.Error == "forwarded") { await FollowForward(); return; }
                catch (Exception e) { SetStatus(SyncStatus.Offline, e.Message); }
                State.Save();
            } while (_again);
        }
        finally { _running = false; }
    }

    async Task Pull(SyncClient client)
    {
        bool full = State.Since == 0;
        long cursor = State.Since;
        var records = new List<RecordOut>();
        while (true)
        {
            Changes page;
            try { page = await client.GetChanges(cursor); }
            catch (SyncApiException e) when (e.Status == 410 && e.Error == "resync")
            {
                State.Since = 0;
                await Pull(client);
                return;
            }
            records.AddRange(page.Records);
            cursor = Math.Max(cursor, page.Version);
            if (!page.More) break;
        }
        // From here on nothing awaits, so no local edit can slip in between
        // reading the database and writing the merged records to it.
        Scan();
        bool changed = false;
        var seen = new HashSet<string>();
        foreach (var r in records)
        {
            seen.Add(r.Id);
            if (Merge(r)) changed = true;
        }
        State.Since = cursor;
        if (full)
        {
            // Records the server no longer has were deleted long ago (§5.3).
            foreach (var (id, r) in State.Records)
            {
                if (seen.Contains(id) || r.Dirty || r.Version == 0) continue;
                var body = RecordBody.Parse(r.Body);
                if (!body.IsDeleted) { body.Deleted = Tick(); r.Body = body.ToJson(); changed = true; }
            }
        }
        if (changed) ApplyAll();
    }

    /// <summary>Merges one remote record into the state. Returns true if it changed.</summary>
    bool Merge(RecordOut r)
    {
        if (_chain == null || KauriCrypto.UnB64(r.Id) is not byte[] rid || KauriCrypto.UnB64(r.Body) is not byte[] sealedBody) return false;
        if (State.Records.TryGetValue(r.Id, out var mine) && r.Version <= mine.Version) return false;
        if (!_dataKeys.TryGetValue(r.Epoch, out var key)) return false;
        RecordBody remote;
        string plain;
        try
        {
            plain = Encoding.UTF8.GetString(_chain.OpenRecord(key, r.Epoch, rid, sealedBody));
            remote = RecordBody.Parse(plain);
        }
        catch (Exception e) when (e is SyncCryptoException or JsonException or InvalidOperationException) { return false; }
        if (!Kinds.Contains(remote.Kind)) return false;
        if (remote.Newest is Hlc n) State.Clock.Observe(n);
        if (mine == null)
        {
            if (LocalId(remote) is not string local) return false;
            State.Records[r.Id] = new SyncedRecord { Kind = remote.Kind, Local = local, Version = r.Version, Epoch = r.Epoch, Body = remote.ToJson() };
            return true;
        }
        var old = RecordBody.Parse(mine.Body);
        var merged = old.Merged(remote);
        mine.Version = r.Version;
        mine.Epoch = r.Epoch;
        mine.Dirty = mine.Dirty && !merged.SameAs(remote);
        mine.Body = merged.ToJson();
        return !merged.SameAs(old);
    }

    static string? LocalId(RecordBody body) => body.Kind switch
    {
        "clip" => body.Str("text") is string t ? TextHash(t) : null,
        _ => body.Extra.TryGetValue("u", out var u) && u is JsonValue v && v.TryGetValue(out string? s) && Guid.TryParse(s, out var g) ? g.ToString() : null,
    };

    static string TextHash(string text) => KauriCrypto.ToHex(KauriCrypto.Sha256(Encoding.UTF8.GetBytes(text)));

    Hlc Tick() => State.Clock.Tick(_device != null ? KauriCrypto.B64(_device.Id) : "");

    async Task Push(SyncClient client)
    {
        if (_chain == null || !_dataKeys.TryGetValue(_epoch, out var key)) return;
        var dirty = State.Records.Where(e => e.Value.Dirty).OrderBy(e => e.Value.Kind == "folder" ? 0 : 1).ToList();
        foreach (var batch in dirty.Chunk(200))
        {
            var records = new List<RecordIn>();
            foreach (var (id, r) in batch)
            {
                if (KauriCrypto.UnB64(id) is not byte[] rid) continue;
                var sealedBody = _chain.SealRecord(key, _epoch, rid, Encoding.UTF8.GetBytes(r.Body));
                records.Add(new RecordIn(id, _epoch, KauriCrypto.B64(sealedBody), RecordBody.Parse(r.Body).IsDeleted, r.Version));
            }
            List<long> versions;
            try { versions = await client.Write(records); }
            catch (SyncApiException e) when (e.Status == 409)
            {
                // Someone wrote first: merge their version and retry next round.
                await Pull(client);
                _again = true;
                return;
            }
            for (int i = 0; i < records.Count && i < versions.Count; i++)
            {
                var r = State.Records[records[i].Id];
                r.Version = versions[i];
                r.Epoch = _epoch;
                r.Dirty = false;
            }
        }
        State.Save();
    }

    // ---------------- Local data → records ----------------

    /// <summary>Records every local change since the last scan with a fresh HLC.</summary>
    void Scan()
    {
        if (_chain == null) return;
        var folders = Database.Shared.FetchFolders();
        var byLocal = State.Records.ToDictionary(e => $"{e.Value.Kind}:{e.Value.Local}", e => e.Key);

        (string Id, RecordBody Body) Record(string kind, string local)
        {
            if (byLocal.TryGetValue($"{kind}:{local}", out var id) && State.Records.TryGetValue(id, out var r)) return (id, RecordBody.Parse(r.Body));
            id = KauriCrypto.B64(_chain.RecordId(kind, local));
            var body = new RecordBody(kind);
            if (kind != "clip") body.Extra["u"] = local;
            State.Records[id] = new SyncedRecord { Kind = kind, Local = local, Body = body.ToJson() };
            byLocal[$"{kind}:{local}"] = id;
            return (id, body);
        }

        void Save(string id, RecordBody body, bool changed)
        {
            var r = State.Records[id];
            if (r.Kind != "clip") r.Materialized = true;
            if (!changed) return;
            r.Body = body.ToJson();
            r.Dirty = true;
        }

        var present = new HashSet<string>();
        var folderRecords = folders.Select(f => Record("folder", f.Id.ToString())).ToList();
        var folderOrder = OrderKey.Assign(folderRecords.Select(r => r.Body.Str("order")).ToList());
        for (int i = 0; i < folders.Count; i++)
        {
            var f = folders[i];
            var (id, body) = folderRecords[i];
            present.Add(id);
            bool changed = body.Set("title", f.Title, Tick);
            changed |= body.Set("enabled", f.IsEnabled, Tick);
            changed |= body.Set("order", folderOrder[i], Tick);
            Save(id, body, changed);

            var snippets = f.Snippets.OrderBy(s => s.Index).ToList();
            var recs = snippets.Select(s => Record("snippet", s.Id.ToString())).ToList();
            var order = OrderKey.Assign(recs.Select(r => r.Body.Str("folder") == id ? r.Body.Str("order") : null).ToList());
            for (int j = 0; j < snippets.Count; j++)
            {
                var s = snippets[j];
                var (sid, sbody) = recs[j];
                present.Add(sid);
                bool sc = sbody.Set("folder", id, Tick);
                sc |= sbody.Set("title", s.Title, Tick);
                sc |= sbody.Set("content", s.Content, Tick);
                sc |= sbody.Set("enabled", s.IsEnabled, Tick);
                sc |= sbody.Set("order", order[j], Tick);
                Save(sid, sbody, sc);
            }
        }

        if (State.SyncsHistory) present.UnionWith(ScanHistory(Record, Save));

        // Local items that are gone were deleted.
        foreach (var (id, r) in State.Records)
        {
            if (present.Contains(id)) continue;
            var body = RecordBody.Parse(r.Body);
            if (body.IsDeleted) continue;
            if (r.Kind == "clip")
            {
                // Only clips the user deleted are deleted everywhere. A clip that
                // fell out of the local history stays in sync for the others.
                if (!State.SyncsHistory || string.IsNullOrEmpty(r.History)) continue;
                if (!_deletedHistory.Contains(r.History)) { r.History = ""; continue; }
            }
            else
            {
                // A remote item that was never written here (its folder is
                // missing, say) was not deleted by the user.
                if (!r.Materialized) continue;
                r.Materialized = false;
            }
            body.Deleted = Tick();
            r.Body = body.ToJson();
            r.Dirty = true;
        }
        _deletedHistory.Clear();
    }

    HashSet<string> ScanHistory(Func<string, string, (string Id, RecordBody Body)> record, Action<string, RecordBody, bool> save)
    {
        var histories = Database.Shared.FetchHistoryDetails(sortsByCreatedAt: false, includesThumbnail: false, limit: HistoryCap);
        var byHistory = State.Records.Where(e => e.Value.Kind == "clip" && !string.IsNullOrEmpty(e.Value.History))
            .GroupBy(e => e.Value.History!).ToDictionary(g => g.Key, g => g.First().Key);
        var present = new HashSet<string>();
        foreach (var d in histories)
        {
            var h = d.History;
            if (!h.Types.Contains(ClipTypes.String)) continue;
            string rid;
            RecordBody body;
            if (byHistory.TryGetValue(h.Id, out var known) && State.Records.TryGetValue(known, out var kr))
                (rid, body) = (known, RecordBody.Parse(kr.Body));
            else
            {
                var text = Database.Shared.FetchAssets(h.Id).FirstOrDefault(a => a.Type == ClipTypes.String) is ClipAsset a ? Encoding.UTF8.GetString(a.Data) : null;
                if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) > 60_000) continue;
                (rid, body) = record("clip", TextHash(text));
                State.Records[rid].History = h.Id;
                bool changed = body.Set("types", new JsonArray("text"), Tick);
                changed |= body.Set("text", text, Tick);
                changed |= body.Set("title", TextUtil.Prefix(h.Title, 200), Tick);
                changed |= body.Set("created", h.CreatedAt, Tick);
                save(rid, body, changed);
            }
            present.Add(rid);
            if ((body.Long("lastUsed") ?? 0) < h.UpdateAt)
            {
                body.Set("lastUsed", h.UpdateAt, Tick);
                save(rid, body, true);
            }
        }
        return present;
    }

    // ---------------- Records → local data ----------------

    /// <summary>Writes the state of every record to the local database.</summary>
    void ApplyAll()
    {
        var folders = new List<(Guid Id, string Rid, RecordBody Body)>();
        var snippets = new List<(Guid Id, string Rid, RecordBody Body)>();
        var clips = new List<(string Rid, RecordBody Body)>();
        foreach (var (rid, r) in State.Records)
        {
            var body = RecordBody.Parse(r.Body);
            switch (r.Kind)
            {
                case "folder" when Guid.TryParse(r.Local, out var g): folders.Add((g, rid, body)); break;
                case "snippet" when Guid.TryParse(r.Local, out var g): snippets.Add((g, rid, body)); break;
                case "clip": clips.Add((rid, body)); break;
            }
        }
        static (string, string) Key((Guid Id, string Rid, RecordBody Body) x) => (x.Body.Str("order") ?? "", x.Rid);
        var cmp = Comparer<(string, string)>.Create((a, b) =>
        {
            int c = string.CompareOrdinal(a.Item1, b.Item1);
            return c != 0 ? c : string.CompareOrdinal(a.Item2, b.Item2);
        });

        var liveFolders = folders.Where(f => !f.Body.IsDeleted).OrderBy(Key, cmp).ToList();
        var folderByRecord = liveFolders.ToDictionary(f => f.Rid, f => f.Id);
        var outFolders = liveFolders.Select((f, i) =>
        {
            State.Records[f.Rid].Materialized = true;
            return new SnippetFolder { Id = f.Id, Title = f.Body.Str("title") ?? "", Index = i, IsEnabled = f.Body.Bool("enabled") ?? true };
        }).ToList();
        var outSnippets = new List<Snippet>();
        foreach (var group in snippets.Where(s => !s.Body.IsDeleted).GroupBy(s => s.Body.Str("folder") ?? ""))
        {
            if (!folderByRecord.TryGetValue(group.Key, out var folderId)) continue;
            int i = 0;
            foreach (var s in group.OrderBy(Key, cmp))
            {
                State.Records[s.Rid].Materialized = true;
                outSnippets.Add(new Snippet
                {
                    Id = s.Id, FolderId = folderId, Title = s.Body.Str("title") ?? "", Content = s.Body.Str("content") ?? "",
                    Index = i++, IsEnabled = s.Body.Bool("enabled") ?? true,
                });
            }
        }
        var deadSnippets = snippets.Where(s => s.Body.IsDeleted).Select(s => { State.Records[s.Rid].Materialized = false; return s.Id; }).ToList();
        var deadFolders = folders.Where(f => f.Body.IsDeleted).Select(f => { State.Records[f.Rid].Materialized = false; return f.Id; }).ToList();
        Database.Shared.ApplySync(outFolders, outSnippets, deadSnippets, deadFolders);
        if (State.SyncsHistory) ApplyClips(clips);
    }

    void ApplyClips(List<(string Rid, RecordBody Body)> clips)
    {
        foreach (var (rid, body) in clips)
        {
            var r = State.Records[rid];
            if (body.IsDeleted)
            {
                if (!string.IsNullOrEmpty(r.History))
                {
                    Database.Shared.DeleteHistoryQuietly(r.History);
                    r.History = null;
                }
                continue;
            }
            if (r.History != null || r.Version == 0 || body.Str("text") is not string text) continue;
            var content = new ClipContent { Assets = new() { new ClipAsset(ClipTypes.String, Encoding.UTF8.GetBytes(text)) } };
            var id = content.Hash;
            long updateAt = body.Long("lastUsed") ?? body.Long("created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Database.Shared.SaveHistory(id, text, content.Types, content.Assets, null, updateAt);
            r.History = id;
        }
    }
}
