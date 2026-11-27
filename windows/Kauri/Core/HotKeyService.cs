namespace Kauri.Core;

public enum HotKeyAction { Main = 1, History = 2, Snippet = 3, ClearHistory = 4, EditSnippets = 5, Settings = 6 }

public sealed class HotKeyService
{
    public static HotKeyService Shared { get; } = new();
    public event Action<HotKeyAction>? Triggered;
    public event Action<Guid>? FolderTriggered;
    /// <summary>Shortcuts Windows refused, usually because another app holds them.</summary>
    public List<KeyCombo> Refused { get; } = new();
    public event Action? RefusedChanged;
    readonly List<KeyCombo> _registered = new();
    readonly Dictionary<int, Guid> _folderIds = new();
    int _nextFolderId = 100;
    bool _suspended;

    public void Setup()
    {
        MessageWindow.Shared.HotKey += id =>
        {
            if (_suspended) return;
            if (id < 100) Triggered?.Invoke((HotKeyAction)id);
            else if (_folderIds.TryGetValue(id, out var folder)) FolderTriggered?.Invoke(folder);
        };
        RegisterAll();
    }

    /// <summary>While a shortcut recorder is active, global hot keys must not fire.</summary>
    public void Suspend(bool suspended)
    {
        _suspended = suspended;
        if (suspended) UnregisterAll(); else RegisterAll();
    }

    public void RegisterAll()
    {
        UnregisterAll();
        var s = AppSettings.Shared;
        Register((int)HotKeyAction.Main, s.MainKeyCombo);
        Register((int)HotKeyAction.History, s.HistoryKeyCombo);
        Register((int)HotKeyAction.Snippet, s.SnippetKeyCombo);
        Register((int)HotKeyAction.ClearHistory, s.ClearHistoryKeyCombo);
        Register((int)HotKeyAction.EditSnippets, s.EditSnippetsKeyCombo);
        Register((int)HotKeyAction.Settings, s.SettingsKeyCombo);
        foreach (var (folder, combo) in s.FolderKeyCombos)
        {
            if (!Guid.TryParse(folder, out var g)) continue;
            var id = _nextFolderId++;
            _folderIds[id] = g;
            Register(id, combo);
        }
        RefusedChanged?.Invoke();
    }

    /// <summary>True if the key, with the modifiers held now, is one of the registered shortcuts.</summary>
    public bool IsHotKey(uint vk)
    {
        var m = MacModifiers.None;
        if (Native.IsKeyDown(0x10)) m |= MacModifiers.Shift;
        if (Native.IsKeyDown(0x11)) m |= MacModifiers.Command;
        if (Native.IsKeyDown(0x12)) m |= MacModifiers.Option;
        if (Native.IsKeyDown(0x5B) || Native.IsKeyDown(0x5C)) m |= MacModifiers.Control;
        return _registered.Exists(c => c.VirtualKey == vk && c.Modifiers == m);
    }

    void UnregisterAll()
    {
        for (int i = 1; i <= 6; i++) Native.UnregisterHotKey(MessageWindow.Shared.Handle, i);
        foreach (var id in _folderIds.Keys) Native.UnregisterHotKey(MessageWindow.Shared.Handle, id);
        _folderIds.Clear();
        _nextFolderId = 100;
        _registered.Clear();
        Refused.Clear();
    }

    void Register(int id, KeyCombo? combo)
    {
        if (combo == null) return;
        if (Native.RegisterHotKey(MessageWindow.Shared.Handle, id, combo.Win32Modifiers, (uint)combo.VirtualKey)) _registered.Add(combo);
        else Refused.Add(combo);
    }

    public void Change(HotKeyAction action, KeyCombo? combo)
    {
        AppSettings.Update(action.ToString() + "KeyCombo", s =>
        {
            switch (action)
            {
                case HotKeyAction.Main: s.MainKeyCombo = combo; break;
                case HotKeyAction.History: s.HistoryKeyCombo = combo; break;
                case HotKeyAction.Snippet: s.SnippetKeyCombo = combo; break;
                case HotKeyAction.ClearHistory: s.ClearHistoryKeyCombo = combo; break;
                case HotKeyAction.EditSnippets: s.EditSnippetsKeyCombo = combo; break;
                case HotKeyAction.Settings: if (combo != null) s.SettingsKeyCombo = combo; break;
            }
        });
        if (!_suspended) RegisterAll();
    }

    public KeyCombo? FolderKeyCombo(Guid folder) =>
        AppSettings.Shared.FolderKeyCombos.TryGetValue(folder.ToString(), out var c) ? c : null;

    public void ChangeFolder(Guid folder, KeyCombo? combo)
    {
        AppSettings.Update(nameof(AppSettings.FolderKeyCombos), s =>
        {
            if (combo == null) s.FolderKeyCombos.Remove(folder.ToString());
            else s.FolderKeyCombos[folder.ToString()] = combo;
        });
        if (!_suspended) RegisterAll();
    }
}
