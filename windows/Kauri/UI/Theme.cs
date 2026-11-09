using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Kauri.UI;

/// <summary>
/// Windows 11 flavoured palette. Layout follows Clipy for Mac; typography, colours and
/// controls follow Fluent so the app feels at home on Windows.
/// </summary>
public static class Theme
{
    public static bool? ForcedDark { get; set; }

    public static bool IsDark
    {
        get
        {
            if (ForcedDark is bool b) return b;
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }
    }

    public static event Action? Changed;

    public static void Watch()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            {
                _accent = null;
                Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
            }
        };
    }

    public static SolidColorBrush Brush(byte r, byte g, byte b, double a = 1)
    {
        var br = new SolidColorBrush(Color.FromArgb((byte)Math.Round(a * 255), r, g, b));
        br.Freeze();
        return br;
    }

    public static SolidColorBrush Brush(Color c)
    {
        var br = new SolidColorBrush(c);
        br.Freeze();
        return br;
    }

    public static SolidColorBrush Gray(byte v, double a = 1) => Brush(v, v, v, a);

    // ---- Accent (the user's Windows accent colour) ----
    static Color? _accent;

    public static Color AccentColor
    {
        get
        {
            if (_accent is Color c) return c;
            c = Color.FromRgb(0x00, 0x78, 0xD4);
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
                if (k?.GetValue("AccentColorMenu") is int abgr)
                    c = Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
            }
            catch { }
            _accent = c;
            return c;
        }
    }

    /// <summary>Accent for fills: in dark mode Windows uses a lighter tint of the accent.</summary>
    public static Color AccentFillColor => IsDark ? Mix(AccentColor, Colors.White, 0.38) : AccentColor;
    public static SolidColorBrush Accent => Brush(AccentFillColor);
    public static SolidColorBrush ControlAccent => Accent;
    public static SolidColorBrush AccentPressed => Brush(Mix(AccentFillColor, IsDark ? Colors.Black : Colors.White, 0.2));
    public static Brush OnAccent => IsDark ? Brushes.Black : Brushes.White;

    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    // ---- Fonts ----
    static FontFamily? _family;

    public static FontFamily Font => _family ??= new FontFamily("Segoe UI Variable Text, Segoe UI, Yu Gothic UI, Microsoft YaHei UI");

    /// <summary>Segoe Fluent Icons (Windows 11) with MDL2 fallback (Windows 10).</summary>
    public static FontFamily Icons { get; } = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>Map Clipy's macOS point sizes onto Windows type ramp sizes.</summary>
    public static double Size(double macSize) => macSize switch
    {
        <= 9.5 => 11,
        <= 10.5 => 11,
        <= 11.5 => 12,
        <= 13.5 => 14,
        _ => macSize + 1,
    };

    public static double TextScaleX => 1;
    public static double ScaleFor(double size) => 1;
    public static double MenuTextScaleX => 1;
    public static FontWeight TextWeight => FontWeights.Normal;

    static readonly Dictionary<string, BitmapImage> ImageCache = new();

    public static BitmapImage Image(string name)
    {
        if (ImageCache.TryGetValue(name, out var img)) return img;
        img = new BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri($"pack://application:,,,/Assets/{name}.png");
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        ImageCache[name] = img;
        return img;
    }

    // ---- Text ----
    public static Brush TextPrimary => IsDark ? Gray(0xFF) : Gray(0x00, 0.894);
    public static Brush TextSecondary => IsDark ? Gray(0xFF, 0.786) : Gray(0x00, 0.62);
    public static Brush TextTertiary => IsDark ? Gray(0xFF, 0.545) : Gray(0x00, 0.447);
    public static Brush TextDisabled => IsDark ? Gray(0xFF, 0.363) : Gray(0x00, 0.361);

    // ---- Menu palette (Windows 11 flyout) ----
    public static class Menu
    {
        public static Brush Background => IsDark ? Gray(0x2C) : Gray(0xF9);
        public static Brush Text => TextPrimary;
        public static Brush Disabled => TextDisabled;
        public static Brush Secondary => TextSecondary;
        public static Brush Separator => IsDark ? Gray(0xFF, 0.0837) : Gray(0x00, 0.0803);
        public static Brush Hover => IsDark ? Gray(0xFF, 0.0605) : Gray(0x00, 0.0373);
        public static Brush OpenSubmenuHighlight => Hover;
        public static Brush Border => IsDark ? Gray(0x00, 0.4) : Gray(0x00, 0.14);
        public static Brush InnerBorder => IsDark ? Gray(0xFF, 0.07) : Gray(0xFF, 0.0);
    }

    // ---- Window palette ----
    public static class Window
    {
        public static Brush Background => IsDark ? Gray(0x20) : Gray(0xF3);
        public static Brush Card => IsDark ? Gray(0xFF, 0.051) : Gray(0xFF, 0.7);
        public static Brush CardBorder => IsDark ? Gray(0x00, 0.1) : Gray(0x00, 0.0578);
        public static Brush Separator => IsDark ? Gray(0xFF, 0.0837) : Gray(0x00, 0.0803);
    }

    // ---- Control palette (Fluent) ----
    public static class Control
    {
        public static Brush Fill => IsDark ? Gray(0xFF, 0.0605) : Gray(0xFF, 0.7);
        public static Brush FillHover => IsDark ? Gray(0xFF, 0.0837) : Gray(0xF9, 0.5);
        public static Brush FillPressed => IsDark ? Gray(0xFF, 0.0326) : Gray(0xF9, 0.3);
        public static Brush FillDisabled => IsDark ? Gray(0xFF, 0.0419) : Gray(0xF9, 0.3);
        public static Brush InputFill => IsDark ? Gray(0xFF, 0.0605) : Gray(0xFF, 0.7);
        public static Brush InputFillFocused => IsDark ? Gray(0x1F, 0.7) : Gray(0xFF);
        public static Brush Border => IsDark ? Gray(0xFF, 0.0698) : Gray(0x00, 0.0578);
        public static Brush BorderBottom => IsDark ? Gray(0xFF, 0.0465) : Gray(0x00, 0.1622);
        public static Brush InputBottom => IsDark ? Gray(0xFF, 0.5442) : Gray(0x00, 0.4458);
        public static Brush StrongBorder => IsDark ? Gray(0xFF, 0.5442) : Gray(0x00, 0.4458);
        public static Brush CheckFill => IsDark ? Gray(0x00, 0.1) : Gray(0x00, 0.0241);
    }
}
