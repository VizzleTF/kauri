using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Kauri.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Kauri.UI;

/// <summary>Notification-area icon (the Windows equivalent of Clipy's NSStatusItem).</summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon = new() { Text = AppInfo.DisplayName };
    public event Action? Clicked;

    public TrayIcon()
    {
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right) Clicked?.Invoke();
        };
        Refresh();
        AppSettings.Changed += n => { if (n == nameof(AppSettings.StatusItemDisplayMode)) Refresh(); };
        SystemEvents.UserPreferenceChanged += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(Refresh);
    }

    static bool TaskbarIsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    void Refresh()
    {
        var mode = AppSettings.Shared.StatusItemDisplayMode;
        if (mode == 0)
        {
            _icon.Visible = false;
            return;
        }
        var name = mode == 2 ? "tray_reversed" : "tray";
        var old = _icon.Icon;
        _icon.Icon = MakeIcon(name, TaskbarIsLight() ? Color.Black : Color.White);
        old?.Dispose();
        _icon.Visible = true;
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    /// <summary>Tint a template PNG like macOS does for status bar images.</summary>
    static Icon MakeIcon(string name, Color tint)
    {
        var info = System.Windows.Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}.png"));
        using var src = new Bitmap(info!.Stream);
        int size = Forms.SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            var cm = new ColorMatrix(new[]
            {
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 },
                new float[] { tint.R / 255f, tint.G / 255f, tint.B / 255f, 0, 1 },
            });
            using var ia = new ImageAttributes();
            ia.SetColorMatrix(cm);
            g.DrawImage(src, new Rectangle(0, 0, size, size), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
