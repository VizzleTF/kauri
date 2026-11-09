namespace Kauri.Core;

public enum HotKeyAction { Main = 1, History = 2, Snippet = 3, ClearHistory = 4, EditSnippets = 5, Settings = 6 }

public sealed class HotKeyService
{
    public static HotKeyService Shared { get; } = new();
    public event Action<HotKeyAction>? Triggered;
    public event Action<Guid>? FolderTriggered;
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
    }

    void UnregisterAll()
    {
        for (int i = 1; i <= 6; i++) Native.UnregisterHotKey(MessageWindow.Shared.Handle, i);
        foreach (var id in _folderIds.Keys) Native.UnregisterHotKey(MessageWindow.Shared.Handle, id);
        _folderIds.Clear();
        _nextFolderId = 100;
    }

    static void Register(int id, KeyCombo? combo)
    {
        if (combo == null) return;
        Native.RegisterHotKey(MessageWindow.Shared.Handle, id, combo.Win32Modifiers, (uint)combo.VirtualKey);
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
