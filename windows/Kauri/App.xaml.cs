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
