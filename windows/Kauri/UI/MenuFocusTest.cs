#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Kauri.Core;

namespace Kauri.UI;

/// <summary>
/// Debug check that a hot-key menu stays open when another process has the foreground.
/// Usage: Kauri.exe --menu-focus-test --out result.txt (with KAURI_DATA_DIR set).
/// A second Kauri.exe started with --focus-dummy plays the app the user was in.
/// </summary>
public static class MenuFocusTest
{
    public static async void Run(string outPath, int rounds)
    {
        var log = new List<string>();
        int passed = 0;
        AppSettings.Load();
        Database.Open();
        for (int r = 0; r < rounds; r++)
        {
            using var dummy = Process.Start(Environment.ProcessPath!, "--focus-dummy");
            IntPtr dummyWindow = IntPtr.Zero;
            for (int i = 0; i < 100 && dummyWindow == IntPtr.Zero; i++)
            {
                await Task.Delay(50);
                var fg = Native.GetForegroundWindow();
                Native.GetWindowThreadProcessId(fg, out var pid);
                if (pid == dummy.Id) dummyWindow = fg;
            }
            if (dummyWindow == IntPtr.Zero) { log.Add($"round {r}: the dummy never got the foreground"); dummy.Kill(); continue; }
            await Task.Delay(300);

            MenuManager.Shared.PopUp(MenuType.Main);
            await Task.Delay(1500);
            var menu = Application.Current.Windows.OfType<MacMenuWindow>().FirstOrDefault(w => w.IsVisible);
            bool open = menu != null;
            bool foreground = open && Native.GetForegroundWindow() == new WindowInteropHelper(menu!).Handle;
            log.Add($"round {r}: menu open={open} foreground={foreground}");
            if (open && foreground) passed++;
            menu?.CloseAll();
            dummy.Kill();
            await Task.Delay(300);
        }
        log.Add($"{passed}/{rounds} rounds kept the menu open and in front");
        File.WriteAllLines(outPath, log);
        Application.Current.Shutdown(passed == rounds ? 0 : 1);
    }

    /// <summary>The stand-in for the user's app: a plain window that takes the foreground.</summary>
    public static void Dummy()
    {
        var w = new Window { Title = "focus dummy", Width = 300, Height = 200, Left = 100, Top = 100, WindowStartupLocation = WindowStartupLocation.Manual };
        w.Closed += (_, _) => Application.Current.Shutdown();
        w.Show();
        Native.ForceForeground(new WindowInteropHelper(w).Handle);
        w.Activate();
    }
}
#endif
