using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Kauri.UI;

/// <summary>
/// Standard Windows 11 window (native title bar, rounded corners and shadow from DWM)
/// that follows the system light/dark theme.
/// </summary>
public class MacWindow : Window
{
    public const double ShadowPad = 0;
    readonly Border _root = new();
    public bool Resizable { get; init; }
    public bool ForceLight { get; init; }
    public double TitleBarHeight { get; set; }

    public bool IsDarkAppearance => !ForceLight && Theme.IsDark;

    public MacWindow(bool minimizable = false, bool zoomable = false)
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = zoomable ? ResizeMode.CanResize : minimizable ? ResizeMode.CanMinimize : ResizeMode.NoResize;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        Icon = Theme.Image("32");
        FontFamily = Theme.Font;
        base.Content = _root;
        Theme.Changed += ApplyTheme;
        Closed += (_, _) => Theme.Changed -= ApplyTheme;
        SourceInitialized += (_, _) => ApplyFrame();
        Loaded += (_, _) => ApplyTheme();
        if (!minimizable && !zoomable)
            SourceInitialized += (_, _) =>
            {
                // Hide minimise/maximise like the macOS Settings window.
                var hwnd = new WindowInteropHelper(this).Handle;
                int style = Core.Native.GetWindowLongW(hwnd, -16);
                Core.Native.SetWindowLongW(hwnd, -16, style & ~0x20000 & ~0x10000);
            };
    }

    public UIElement? BodyContent
    {
        get => _root.Child;
        set => _root.Child = value;
    }

    public string MacTitle
    {
        get => Title;
        set => Title = value;
    }

    public void SetTitleStyle(double fontSize, FontWeight weight, HorizontalAlignment align, Thickness margin) { }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void ApplyFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDarkAppearance ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        // Caption colour matches the window background so the title bar blends in.
        var bg = ((SolidColorBrush)WindowBackground).Color;
        int caption = bg.R | (bg.G << 8) | (bg.B << 16);
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
    }

    protected virtual void ApplyTheme()
    {
        _root.Background = WindowBackground;
        Foreground = Theme.TextPrimary;
        ApplyFrame();
    }

    protected virtual Brush WindowBackground => IsDarkAppearance ? Theme.Gray(0x20) : Theme.Gray(0xF3);

    protected virtual double DragRegionHeight => 0;

    /// <summary>Set the client area size.</summary>
    public void SetBodySize(double width, double height)
    {
        _root.Width = width;
        _root.Height = height;
        SizeToContent = SizeToContent.WidthAndHeight;
        Loaded += (_, _) =>
        {
            // Release the fixed size so the user can resize afterwards.
            if (!Resizable) return;
            var w = ActualWidth; var h = ActualHeight;
            SizeToContent = SizeToContent.Manual;
            _root.Width = double.NaN;
            _root.Height = double.NaN;
            Width = w; Height = h;
        };
    }

    public void CenterOnScreen() => WindowStartupLocation = WindowStartupLocation.CenterScreen;
}
