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
        MacMenuWindow.Trace = m => log.Add("  " + m);
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

            // Open the menu away from the dummy window, so the test click lands outside it.
            Native.GetWindowRect(dummyWindow, out var area);
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(Application.Current.Windows.Count > 0 ? Application.Current.Windows[0] : new Window());
            MenuManager.Shared.PopUp(MenuType.Main, new Point(area.Right / dpi.DpiScaleX + 60, area.Top / dpi.DpiScaleY));
            await Task.Delay(1500);
            var menu = Application.Current.Windows.OfType<MacMenuWindow>().FirstOrDefault(w => w.IsVisible);
            bool open = menu != null;
            bool foreground = open && Native.GetForegroundWindow() == new WindowInteropHelper(menu!).Handle;
            // A click inside the menu, on its top padding where there is no item, must not close it.
            bool keptOnInsideClick = false;
            if (open && Native.GetWindowRect(new WindowInteropHelper(menu!).Handle, out var mr))
            {
                Native.GetCursorPos(out var saved0);
                double sc = System.Windows.Media.VisualTreeHelper.GetDpi(menu!).DpiScaleY;
                Native.SetCursorPos((mr.Left + mr.Right) / 2, mr.Top + (int)((MacMenuWindow.ShadowPad + 2) * sc));
                await Task.Delay(50);
                Click();
                await Task.Delay(300);
                Native.SetCursorPos(saved0.X, saved0.Y);
                keptOnInsideClick = menu!.IsVisible;
            }
            // A click on the other app's window must close the menu.
            bool closedByClick = false;
            if (open && Native.GetWindowRect(dummyWindow, out var dr))
            {
                Native.GetCursorPos(out var saved);
                Native.SetCursorPos((dr.Left + dr.Right) / 2, (dr.Top + dr.Bottom) / 2);
                await Task.Delay(50);
                Click();
                await Task.Delay(400);
                Native.SetCursorPos(saved.X, saved.Y);
                closedByClick = !menu!.IsVisible;
            }
            log.Add($"round {r}: menu open={open} foreground={foreground} kept on a click inside={keptOnInsideClick} closed by a click outside={closedByClick}");
            if (open && foreground && keptOnInsideClick && closedByClick) passed++;
            if (menu?.IsVisible == true) menu.CloseAll();
            dummy.Kill();
            await Task.Delay(300);
        }
        log.Add($"{passed}/{rounds} rounds kept the menu open and in front, and closed it on a click outside");
        File.WriteAllLines(outPath, log);
        Application.Current.Shutdown(passed == rounds ? 0 : 1);
    }

    static void Click() => Native.SendInput(2,
    [
        new Native.INPUT { type = 0, U = new Native.InputUnion { mi = new Native.MOUSEINPUT { dwFlags = 0x2 } } },
        new Native.INPUT { type = 0, U = new Native.InputUnion { mi = new Native.MOUSEINPUT { dwFlags = 0x4 } } },
    ], System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());

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
