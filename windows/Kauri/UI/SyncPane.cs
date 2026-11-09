using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kauri.Core;
using Kauri.Sync;

namespace Kauri.UI;

/// <summary>The Sync pane of Settings: turn sync on, add and remove devices, choose what syncs.</summary>
public sealed class SyncPane : ContentControl
{
    static SyncEngine E => SyncEngine.Shared;
    readonly bool _dark;
    bool _busy;

    public SyncPane(bool dark)
    {
        _dark = dark;
        Rebuild();
        Loaded += (_, _) => { E.Changed += OnChanged; _ = E.RefreshDevices(); };
        Unloaded += (_, _) => E.Changed -= OnChanged;
    }

    void OnChanged() => Dispatcher.BeginInvoke(Rebuild);

    void Rebuild()
    {
        Content = E.IsOn ? OnView() : OffView();
        IsEnabled = !_busy;
    }

    static SettingsSection Section(string? title, bool divider, params SettingsItem[] items)
    {
        var s = new SettingsSection { Title = title, BottomDivider = divider };
        s.Items.AddRange(items);
        return s;
    }

    MacButton Button(string title, Action act, bool prominent = false)
    {
        var b = new MacButton(title, prominent) { HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += act;
        return b;
    }

    // ---------------- Off ----------------

    FrameworkElement OffView()
    {
        var server = new MacTextField(E.State.Server, "https://sync.example.com", 320);
        server.Committed += t => E.SetServer(t);
        var buttons = new StackPanel();
        void Add(string title, Action act, bool prominent = false)
        {
            var b = Button(title, act, prominent);
            b.Margin = new Thickness(0, 0, 0, 8);
            buttons.Children.Add(b);
        }
        Add("Start Sync…", () => Run(async () =>
        {
            E.SetServer(server.Text);
            if (Confirm("Start Sync", "Kauri Clip will create a new sync chain on this server and show you its 24-word sync code.", "Start"))
                await E.CreateChain(server.Text);
        }), prominent: true);
        Add("Join with a Pairing Code…", () => { E.SetServer(server.Text); JoinWithCode(server.Text); });
        Add("Join with the Sync Code…", () => { E.SetServer(server.Text); JoinWithWords(server.Text); });

        var sections = new List<SettingsSection>
        {
            Section("Server:", true,
                new SettingsItem(ItemKind.PopUp, server),
                SettingsLayout.Description("Everything is encrypted on this PC before it leaves. The server sees neither your snippets nor their titles.", _dark)),
            Section("Sync:", false, new SettingsItem(ItemKind.Custom, buttons)),
        };
        return SettingsLayout.Build(sections, _dark);
    }

    // ---------------- On ----------------

    FrameworkElement OnView()
    {
        var (color, text) = E.Status switch
        {
            SyncEngine.SyncStatus.Synced => (Color.FromRgb(0x2E, 0xA0, 0x43), "Up to date"),
            SyncEngine.SyncStatus.Syncing or SyncEngine.SyncStatus.Connecting => (Color.FromRgb(0xD8, 0xA0, 0x00), E.Status == SyncEngine.SyncStatus.Syncing ? "Syncing…" : "Connecting…"),
            SyncEngine.SyncStatus.Offline => (Color.FromRgb(0xE0, 0x6C, 0x00), "Offline, will retry"),
            _ => (Colors.Gray, "Sync is off"),
        };
        var status = new StackPanel { Orientation = Orientation.Horizontal };
        status.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        status.Children.Add(Mac.Text(text, 13, Theme.TextPrimary));

        var history = new MacCheckBox("Clipboard history", E.State.SyncsHistory);
        history.Changed += E.SetSyncsHistory;
        var snippets = new MacCheckBox("Snippets and folders", true) { IsEnabled = false };

        var devices = new StackPanel();
        foreach (var d in E.Devices)
        {
            var row = new Grid { Width = 380, Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(Mac.Icon(d.IsThisDevice ? "" : "", 14, Theme.TextSecondary));
            var n = Mac.Text(d.Name, 13, Theme.TextPrimary);
            n.Margin = new Thickness(8, 0, 0, 0);
            name.Children.Add(n);
            if (d.IsThisDevice) { var t = Mac.Text("  This PC", 13, Theme.TextSecondary); name.Children.Add(t); }
            if (!d.IsPostQuantum) { var t = Mac.Text("  Not post-quantum", 11, new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x00))); t.VerticalAlignment = VerticalAlignment.Center; name.Children.Add(t); }
            row.Children.Add(name);
            if (!d.IsThisDevice)
            {
                var remove = new MacButton("Remove…", height: 28);
                remove.Click += () => RemoveDevice(d);
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
            }
            devices.Children.Add(row);
        }
        var add = Button("Add a Device…", AddDevice);
        add.Margin = new Thickness(0, 4, 0, 0);
        devices.Children.Add(add);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var show = Button("Show Sync Code…", () => { if (E.SyncCode is string c) ShowSyncCode(c); });
        show.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(show);
        actions.Children.Add(Button("Turn Off Sync…", () =>
        {
            if (Confirm("Turn Off Sync?", "This PC leaves the sync chain. Your snippets stay on this PC and on your other devices.", "Turn Off"))
                Run(E.TurnOff);
        }));

        var sections = new List<SettingsSection>
        {
            Section("Status:", true,
                new SettingsItem(ItemKind.Text, status),
                SettingsLayout.Description(E.Status == SyncEngine.SyncStatus.Offline && E.LastError != null ? E.LastError : E.State.Server, _dark)),
            Section("What syncs:", true,
                new SettingsItem(ItemKind.CheckBox, snippets),
                new SettingsItem(ItemKind.CheckBox, history),
                SettingsLayout.Description("Text only, up to 500 items. Confidential content and excluded apps are never synced.", _dark)),
            Section("Devices:", true, new SettingsItem(ItemKind.Custom, devices)),
            Section(null, false, new SettingsItem(ItemKind.Custom, actions)),
        };
        return SettingsLayout.Build(sections, _dark);
    }

    // ---------------- Actions ----------------

    async void Run(Func<Task> work)
    {
        _busy = true;
        Rebuild();
        try { await work(); }
        catch (Exception e) { MacAlert.Show("Sync failed", e.Message, new[] { "OK" }); }
        finally { _busy = false; Rebuild(); }
    }

    static bool Confirm(string title, string message, string action) =>
        MacAlert.Show(title, message, new[] { action, "Cancel" }).Result == 0;

    public static void ShowSyncCode(string code)
    {
        var words = code.Split(' ');
        var grid = new UniformGrid4();
        for (int i = 0; i < words.Length; i++)
        {
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var num = Mac.Text($"{i + 1}.", 12, Theme.TextSecondary);
            num.Width = 22;
            cell.Children.Add(num);
            var w = Mac.Text(words[i], 13, Theme.TextPrimary);
            w.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            cell.Children.Add(w);
            grid.Children.Add(cell);
        }
        var box = new Border { Child = grid, Padding = new Thickness(12), CornerRadius = new CornerRadius(6), Background = Theme.Control.InputFill, BorderBrush = Theme.Control.Border, BorderThickness = new Thickness(1) };
        var alert = new MacAlert("Sync Code",
            "These 24 words open your synced data. Write them down and keep them safe: anyone who has them can read your snippets. You need them if you lose all your devices.",
            new[] { "Copy", "Done" }, false, extra: box);
        alert.ShowDialog();
        if (alert.Result == 0) CopyConcealed(code);
    }

    /// <summary>Copies text marked so that clipboard managers and Windows clipboard history skip it.</summary>
    static void CopyConcealed(string text)
    {
        var data = new DataObject();
        data.SetText(text);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new System.IO.MemoryStream([0]));
        data.SetData("CanIncludeInClipboardHistory", new System.IO.MemoryStream([0, 0, 0, 0]));
        data.SetData("CanUploadToCloudClipboard", new System.IO.MemoryStream([0, 0, 0, 0]));
        try { Clipboard.SetDataObject(data, true); } catch (System.Runtime.InteropServices.ExternalException) { }
    }

    void JoinWithWords(string server)
    {
        var field = new MacTextField("", "Enter the 24 words", MacAlert.BodyWidth - 48, multiline: true, monospace: true);
        var alert = new MacAlert("Join with the Sync Code", "Enter the 24 words shown on your other device.", new[] { "Join", "Cancel" }, false, extra: field);
        alert.Loaded += (_, _) => field.FocusField();
        alert.ShowDialog();
        if (alert.Result == 0 && field.Text.Trim().Length > 0) Run(() => E.Join(server, field.Text));
    }

    void JoinWithCode(string server)
    {
        var field = new MacTextField("", "XXXX-XXXX-XXXX", 220, monospace: true);
        var alert = new MacAlert("Join with a Pairing Code", "Enter the code shown on your other device.", new[] { "Join", "Cancel" }, false, extra: field);
        alert.Loaded += (_, _) => field.FocusField();
        alert.ShowDialog();
        if (alert.Result != 0) return;
        var code = field.Text;
        Run(async () =>
        {
            var pending = await E.JoinWithCode(server, code);
            using var cts = new CancellationTokenSource();
            var number = $"{pending.Number:D4}";
            var wait = new MacAlert("Waiting for the other device…", $"Check that your other device shows the number {number}, then allow this PC there.",
                new[] { "Cancel" }, false, extra: BigText(number, 40));
            Exception? error = null;
            wait.Loaded += async (_, _) =>
            {
                try { await E.WaitForApproval(pending, cts.Token); wait.Finish(0); }
                catch (OperationCanceledException) { }
                catch (Exception e) { error = e; wait.Finish(1); }
            };
            wait.ShowDialog();
            cts.Cancel();
            if (error != null) throw error;
        });
    }

    void AddDevice()
    {
        Run(async () =>
        {
            var req = await E.OpenPairing();
            using var cts = new CancellationTokenSource();
            SyncEngine.PairingJoin? joined = null;
            Exception? error = null;
            var show = new MacAlert("Add a Device",
                "On the new device, choose Join with a Pairing Code and enter this code. It works for 5 minutes.",
                new[] { "Cancel" }, false, extra: BigText(req.Code, 32));
            show.Loaded += async (_, _) =>
            {
                try { joined = await E.WaitForJoin(req, cts.Token); show.Finish(0); }
                catch (OperationCanceledException) { }
                catch (Exception e) { error = e; show.Finish(1); }
            };
            show.ShowDialog();
            cts.Cancel();
            if (error != null) throw error;
            if (joined == null) { await E.ClosePairing(req.Pairing.Slot); return; }
            var number = $"{joined.Number:D4}";
            var allow = MacAlert.Show($"Allow “{joined.Name}” to sync?", $"Check that the other device shows the number {number}.", new[] { "Allow", "Deny" }).Result == 0;
            if (allow) await E.Allow(joined);
            else await E.ClosePairing(joined.Slot);
        });
    }

    void RemoveDevice(SyncEngine.DeviceRow d)
    {
        if (Confirm($"Remove “{d.Name}”?", "The device stops syncing at once. Your other devices move to a new sync code, which Kauri Clip will show you.", "Remove"))
            Run(() => E.RemoveDevice(d.Id));
    }

    static FrameworkElement BigText(string text, double size)
    {
        var t = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        return t;
    }

    /// <summary>Four equal columns.</summary>
    sealed class UniformGrid4 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid4() => Columns = 4;
    }
}
