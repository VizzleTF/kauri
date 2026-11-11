using System.Windows;
using Kauri.Core;
using Kauri.UI;

namespace Kauri;

public partial class App : Application
{
    Mutex? _mutex;
    TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        if (args.Length > 0 && args[0] == "--render")
        {
            DevTools.Run(args);
            Shutdown();
            return;
        }
        if (args.Length > 1 && args[0] == "--sync-check")
        {
            int i = Array.IndexOf(args, "--out");
            using var log = i > 0 && i + 1 < args.Length ? new System.IO.StreamWriter(args[i + 1]) : new System.IO.StreamWriter(Console.OpenStandardOutput());
            Shutdown(Kauri.Sync.SyncCheck.Run(args[1], log));
            return;
        }
#if DEBUG
        if (args.Length > 0 && args[0] == "--focus-dummy") { UI.MenuFocusTest.Dummy(); return; }
        if (args.Length > 0 && args[0] == "--menu-focus-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int o = Array.IndexOf(args, "--out");
            UI.MenuFocusTest.Run(o > 0 && o + 1 < args.Length ? args[o + 1] : "menu-focus.txt", 10);
            return;
        }
        // Sync test mode: KAURI_SYNC_TEST_SERVER and KAURI_SYNC_TEST_WORDS join that chain.
        // No single-instance lock, clipboard monitoring, hot keys or tray icon.
        if (Environment.GetEnvironmentVariable("KAURI_SYNC_TEST_SERVER") is string testServer &&
            Environment.GetEnvironmentVariable("KAURI_SYNC_TEST_WORDS") is string testWords)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AppSettings.Load();
            Database.Open();
            var engine = Kauri.Sync.SyncEngine.Shared;
            engine.Start();
            if (!engine.IsOn) _ = engine.Join(testServer, testWords);
            return;
        }
#endif

        _mutex = new Mutex(true, "Kauri.SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        AppSettings.Load();
        Database.Open();
        Theme.Watch();

        var menus = MenuManager.Shared;
        menus.ClearHistory = ClearHistory;
        menus.EditSnippets = () => SnippetsEditorWindow.ShowShared();
        menus.OpenSettings = () => SettingsWindow.ShowShared();
        menus.Quit = Quit;

        ClipService.Shared.StartMonitoring();
        Kauri.Sync.SyncEngine.Shared.NewSyncCode += code => Dispatcher.BeginInvoke(() => SyncPane.ShowSyncCode(code));
        Kauri.Sync.SyncEngine.Shared.Removed += () => Dispatcher.BeginInvoke(() =>
            MacAlert.Show("This PC was removed from sync.", "Another device removed it. Your snippets stay on this PC.", new[] { "OK" }));
        Kauri.Sync.SyncEngine.Shared.Start();
        HotKeyService.Shared.Setup();
        HotKeyService.Shared.Triggered += a =>
        {
            switch (a)
            {
                case HotKeyAction.Main: menus.PopUp(MenuType.Main); break;
                case HotKeyAction.History: menus.PopUp(MenuType.History); break;
                case HotKeyAction.Snippet: menus.PopUp(MenuType.Snippet); break;
                case HotKeyAction.ClearHistory: ClearHistory(); break;
                case HotKeyAction.EditSnippets: SnippetsEditorWindow.ShowShared(); break;
                case HotKeyAction.Settings: SettingsWindow.ShowShared(); break;
            }
        };
        HotKeyService.Shared.FolderTriggered += id => menus.PopUpSnippetFolder(id);

        _tray = new TrayIcon();
        _tray.Clicked += () => menus.PopUp(MenuType.Main, MenuManager.CursorDip(), anchorBottom: true);

        LoginItem.Sync();
        if (!AppSettings.Shared.SuppressLoginItemAlert && !AppSettings.Shared.LaunchAtLogin)
            Dispatcher.BeginInvoke(() => MacAlert.LoginItemPrompt());
    }

    public static void ClearHistory()
    {
        if (AppSettings.Shared.ShowsClearHistoryAlert)
        {
            var (result, suppress) = MacAlert.Show(
                "Clear History",
                "Are you sure you want to clear your clipboard history?",
                new[] { "Clear History", "Cancel" },
                showsSuppression: true);
            if (result != 0) return;
            if (suppress) AppSettings.Update(nameof(AppSettings.ShowsClearHistoryAlert), s => s.ShowsClearHistoryAlert = false);
        }
        Database.Shared.DeleteAllHistories();
    }

    void Quit()
    {
        ClipService.Shared.ApplicationWillTerminate();
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
