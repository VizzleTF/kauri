using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Kauri.Core;

namespace Kauri.UI;

/// <summary>Clipy's alerts drawn as a Windows 11 ContentDialog.</summary>
public sealed class MacAlert : Window
{
    public const double BodyWidth = 420;
    const double Pad = 32;
    int _result = -1;
    readonly MacCheckBox? _suppress;
    public bool Suppressed => _suppress?.IsChecked == true;
    public FrameworkElement Body { get; }

    public MacAlert(string message, string info, IReadOnlyList<string> buttons, bool showsSuppression, string suppressionTitle = "Don't ask again")
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = true;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = message;
        Icon = Theme.Image("32");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        bool dark = Theme.IsDark;

        var content = new StackPanel { Margin = new Thickness(24, 22, 24, 24) };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new Image { Source = Theme.Image("128"), Width = 28, Height = 28, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        header.Children.Add(icon);
        var title = new TextBlock
        {
            Text = message,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = BodyWidth - 48 - 40,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(title);
        content.Children.Add(header);
        if (!string.IsNullOrEmpty(info))
        {
            var t = Mac.Text(info, 13, Theme.TextPrimary);
            t.TextWrapping = TextWrapping.Wrap;
            t.MaxWidth = BodyWidth - 48;
            t.Margin = new Thickness(0, 12, 0, 0);
            t.HorizontalAlignment = HorizontalAlignment.Left;
            content.Children.Add(t);
        }
        if (showsSuppression)
        {
            _suppress = new MacCheckBox(suppressionTitle, false) { Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            content.Children.Add(_suppress);
        }

        var footer = new Grid { Margin = new Thickness(24) };
        for (int i = 0; i < buttons.Count; i++)
        {
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            if (i < buttons.Count - 1) footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        }
        for (int i = 0; i < buttons.Count; i++)
        {
            int idx = i;
            var b = new MacButton(buttons[i], prominent: i == 0) { HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += () => { _result = idx; Close(); };
            Grid.SetColumn(b, i * 2);
            footer.Children.Add(b);
        }
        var footerHost = new Border
        {
            Child = footer,
            Background = dark ? Theme.Gray(0x20) : Theme.Gray(0xF3),
            BorderBrush = Theme.Window.Separator,
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 7, 7),
        };

        var stack = new StackPanel();
        stack.Children.Add(content);
        stack.Children.Add(footerHost);

        var body = new Border
        {
            Width = BodyWidth,
            CornerRadius = new CornerRadius(8),
            Background = dark ? Theme.Gray(0x2B) : Theme.Gray(0xFF),
            BorderBrush = dark ? Theme.Gray(0xFF, 0.08) : Theme.Gray(0x00, 0.1),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
        Body = body;
        var root = new Grid { Margin = new Thickness(Pad) };
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = body.Background,
            Effect = new DropShadowEffect { BlurRadius = 48, ShadowDepth = 12, Direction = 270, Opacity = dark ? 0.5 : 0.22 },
        });
        root.Children.Add(body);
        Content = root;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _result = buttons.Count - 1; Close(); }
            if (e.Key == Key.Enter) { _result = 0; Close(); }
        };
        Loaded += (_, _) => Native.ForceForeground(new WindowInteropHelper(this).Handle);
    }

    public static (int Result, bool Suppressed) Show(string message, string info, IReadOnlyList<string> buttons, bool showsSuppression = false)
    {
        var a = new MacAlert(message, info, buttons, showsSuppression);
        a.ShowDialog();
        return (a._result, a.Suppressed);
    }

    public static void LoginItemPrompt()
    {
        var (r, suppressed) = Show($"Launch {AppInfo.Name} on system startup?", "You can change this later in Settings.",
            new[] { "Launch on startup", "Don't launch" }, showsSuppression: true);
        AppSettings.Update(nameof(AppSettings.LaunchAtLogin), s =>
        {
            s.LaunchAtLogin = r == 0;
            s.SuppressLoginItemAlert = suppressed;
        });
        LoginItem.Apply(r == 0);
    }
}
