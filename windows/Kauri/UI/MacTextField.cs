using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Kauri.UI;

/// <summary>A Windows 11 text box: filled field, accent underline when focused.</summary>
public sealed class MacTextField : Border
{
    readonly TextBox _box;
    readonly TextBlock _placeholder;
    readonly Border _underline;
    public event Action<string>? Committed;

    public MacTextField(string text, string placeholder, double width, bool multiline = false, bool monospace = false)
    {
        _box = new TextBox
        {
            Text = text,
            FontFamily = monospace ? new FontFamily("Cascadia Mono, Consolas") : Theme.Font,
            FontSize = 14,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            Padding = new Thickness(0, multiline ? 6 : 0, 0, 0),
            Margin = new Thickness(10, 0, 10, 0),
            FocusVisualStyle = null,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        _placeholder = Mac.Text(placeholder, 14, Theme.TextTertiary);
        _placeholder.Margin = new Thickness(10, multiline ? 6 : 0, 10, 0);
        _placeholder.VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center;
        _placeholder.IsHitTestVisible = false;
        if (monospace) _placeholder.FontFamily = _box.FontFamily;
        _underline = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        var host = new Grid();
        host.Children.Add(_placeholder);
        host.Children.Add(_box);
        host.Children.Add(_underline);
        Child = host;
        Width = width;
        Height = multiline ? 96 : 32;
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        ClipToBounds = true;
        HorizontalAlignment = HorizontalAlignment.Left;
        _box.TextChanged += (_, _) => Refresh();
        _box.GotKeyboardFocus += (_, _) => Refresh();
        _box.LostKeyboardFocus += (_, _) => { Refresh(); Committed?.Invoke(Text); };
        IsEnabledChanged += (_, _) => Refresh();
        Refresh();
    }

    public string Text
    {
        get => _box.Text;
        set => _box.Text = value;
    }

    public void FocusField()
    {
        _box.Focus();
        _box.SelectAll();
    }

    void Refresh()
    {
        bool focused = _box.IsKeyboardFocused;
        _placeholder.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _box.Foreground = IsEnabled ? Theme.TextPrimary : Theme.TextDisabled;
        _box.CaretBrush = Theme.TextPrimary;
        _box.SelectionBrush = Theme.Accent;
        Background = focused ? Theme.Control.InputFillFocused : IsEnabled ? Theme.Control.InputFill : Theme.Control.FillDisabled;
        BorderBrush = Theme.Control.Border;
        _underline.Height = focused ? 2 : 1;
        _underline.Background = focused ? Theme.Accent : Theme.Control.InputBottom;
    }
}
