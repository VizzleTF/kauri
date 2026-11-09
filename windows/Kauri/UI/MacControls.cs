using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Kauri.Core;

namespace Kauri.UI;

/// <summary>Tracks whether the hosting window is active so controls can refresh their styling.</summary>
public static class KeyWindowState
{
    public static bool? ForceActive { get; set; }

    public static void Attach(FrameworkElement e, Action refresh)
    {
        e.Loaded += (_, _) =>
        {
            var w = Window.GetWindow(e);
            if (w == null) return;
            w.Activated += (_, _) => refresh();
            w.Deactivated += (_, _) => refresh();
            refresh();
        };
    }

    public static bool IsActive(FrameworkElement e) => ForceActive ?? (Window.GetWindow(e)?.IsActive ?? true);

    public static bool IsDark(FrameworkElement e) => Theme.IsDark;
}

/// <summary>Text and colour helpers (Fluent type ramp).</summary>
public static class Mac
{
    /// <summary>Create text; <paramref name="size"/> is Clipy's macOS size, mapped to the Windows ramp.</summary>
    public static TextBlock Text(string text, double size = 13, Brush? fg = null, FontWeight? weight = null)
    {
        var t = new TextBlock
        {
            Text = text,
            FontFamily = Theme.Font,
            FontSize = Theme.Size(size),
            FontWeight = weight ?? FontWeights.Normal,
        };
        if (fg != null) t.Foreground = fg;
        return t;
    }

    public static TextBlock Wrapping(string text, double width, double size = 13, Brush? fg = null)
    {
        var t = Text(text, size, fg);
        t.TextWrapping = TextWrapping.Wrap;
        t.Width = width;
        return t;
    }

    public static TextBlock Icon(string glyph, double size, Brush fg) => new()
    {
        Text = glyph,
        FontFamily = Theme.Icons,
        FontSize = size,
        Foreground = fg,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    public static Brush Label(bool dark) => Theme.TextPrimary;
    public static Brush Secondary(bool dark) => Theme.TextSecondary;
    public static Brush DisabledText(bool dark) => Theme.TextDisabled;
    public static Brush ControlFill(bool dark) => Theme.Control.Fill;

    /// <summary>Fluent "elevation" border: lighter top, darker bottom edge.</summary>
    public static Brush ElevationBorder(bool accent = false)
    {
        var dark = Theme.IsDark;
        Color top, bottom;
        if (accent)
        {
            top = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
            bottom = Color.FromArgb(0x66, 0x00, 0x00, 0x00);
        }
        else if (dark)
        {
            top = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
            bottom = Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF);
        }
        else
        {
            top = Color.FromArgb(0x0F, 0x00, 0x00, 0x00);
            bottom = Color.FromArgb(0x29, 0x00, 0x00, 0x00);
        }
        var b = new LinearGradientBrush(new GradientStopCollection
        {
            new(top, 0), new(top, 0.92), new(bottom, 1),
        }, 90);
        b.Freeze();
        return b;
    }
}

/// <summary>Windows 11 checkbox.</summary>
public sealed class MacCheckBox : Grid
{
    readonly Border _box = new() { Width = 20, Height = 20, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(1) };
    readonly TextBlock _check = new()
    {
        Text = "\uE73E",
        FontFamily = Theme.Icons,
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    readonly TextBlock _label;
    bool _checked, _pressed, _hover;
    public event Action<bool>? Changed;

    public MacCheckBox(string title, bool isChecked, double wrapWidth = 0)
    {
        _checked = isChecked;
        _label = wrapWidth > 0 ? Mac.Wrapping(title, wrapWidth - 28) : Mac.Text(title);
        _label.Margin = new Thickness(28, 0.5, 0, 0);
        _label.VerticalAlignment = VerticalAlignment.Top;
        _label.LineHeight = 20;
        _label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _box.Child = _check;
        Children.Add(_box);
        Children.Add(_label);
        Background = Brushes.Transparent;
        Cursor = Cursors.Arrow;
        MouseEnter += (_, _) => { _hover = true; Refresh(); };
        MouseLeave += (_, _) => { _hover = false; Refresh(); };
        MouseLeftButtonDown += (_, e) => { if (IsEnabled) { _pressed = true; CaptureMouse(); Refresh(); e.Handled = true; } };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_pressed) return;
            _pressed = false;
            ReleaseMouseCapture();
            if (IsMouseOver && IsEnabled) { IsChecked = !IsChecked; Changed?.Invoke(_checked); }
            Refresh();
            e.Handled = true;
        };
        IsEnabledChanged += (_, _) => Refresh();
        KeyWindowState.Attach(this, Refresh);
        Refresh();
    }

    public void SetWrapWidth(double width)
    {
        _label.TextWrapping = TextWrapping.Wrap;
        _label.MaxWidth = Math.Max(10, width - 28);
    }

    public bool IsChecked
    {
        get => _checked;
        set { _checked = value; Refresh(); }
    }

    void Refresh()
    {
        bool enabled = IsEnabled;
        _label.Foreground = enabled ? Theme.TextPrimary : Theme.TextDisabled;
        _check.Visibility = _checked ? Visibility.Visible : Visibility.Hidden;
        if (_checked)
        {
            _box.Background = !enabled ? Theme.TextDisabled : _pressed ? Theme.AccentPressed : Theme.Accent;
            _box.BorderBrush = _box.Background;
            _check.Foreground = Theme.OnAccent;
        }
        else
        {
            _box.Background = _pressed ? Theme.Control.FillPressed : _hover && enabled ? Theme.Control.FillHover : Theme.Control.CheckFill;
            _box.BorderBrush = enabled ? Theme.Control.StrongBorder : Theme.TextDisabled;
        }
    }
}

/// <summary>Windows NumberBox: text field with inline spin buttons.</summary>
public sealed class MacNumberField : StackPanel
{
    readonly TextBox _box;
    readonly Border _field;
    readonly Border _underline;
    readonly TextBlock _up, _down;
    readonly int _min, _max;
    int _value;
    bool _devFocused;
    public event Action<int>? ValueChanged;
    public TextBlock? UnitLabel { get; }
    public TextBlock? CaptionLabel { get; }
    public const double FieldWidth = 96;
    public const double FieldHeight = 32;

    public MacNumberField(int value, int min, int max, string? unit = "items", string? caption = null)
    {
        Orientation = Orientation.Horizontal;
        _min = min;
        _max = max;
        _value = value;
        _box = new TextBox
        {
            Text = value.ToString(CultureInfo.InvariantCulture),
            FontFamily = Theme.Font,
            FontSize = 14,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextAlignment = TextAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0),
            Margin = new Thickness(10, 0, 0, 0),
            FocusVisualStyle = null,
        };
        _box.LostKeyboardFocus += (_, _) => { Commit(); Refresh(); };
        _box.GotKeyboardFocus += (_, _) => { _box.SelectAll(); Refresh(); };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); _box.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Up) { Step(1); e.Handled = true; }
            else if (e.Key == Key.Down) { Step(-1); e.Handled = true; }
        };
        _box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        _box.MouseWheel += (_, e) => { if (_box.IsKeyboardFocused) { Step(e.Delta > 0 ? 1 : -1); e.Handled = true; } };

        _up = Mac.Icon("\uE70E", 10, Theme.TextSecondary);
        _down = Mac.Icon("\uE70D", 10, Theme.TextSecondary);
        Border Spin(TextBlock glyph, int d)
        {
            var b = new Border { Width = 26, Height = 24, CornerRadius = new CornerRadius(3), Background = Brushes.Transparent, Child = glyph, Margin = new Thickness(0, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
            b.MouseEnter += (_, _) => b.Background = Theme.Control.FillHover;
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonDown += (_, e) => { Step(d); e.Handled = true; };
            return b;
        }
        var spins = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        spins.Children.Add(Spin(_up, 1));
        spins.Children.Add(Spin(_down, -1));
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(_box);
        Grid.SetColumn(spins, 1);
        g.Children.Add(spins);
        _underline = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(0, 0, 4, 4) };
        var host = new Grid();
        host.Children.Add(g);
        host.Children.Add(_underline);
        _field = new Border
        {
            Width = FieldWidth + 30,
            Height = FieldHeight,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Child = host,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
        };

        var col = new StackPanel { Orientation = Orientation.Vertical };
        col.Children.Add(_field);
        if (caption != null)
        {
            CaptionLabel = Mac.Text(caption, 11);
            CaptionLabel.Width = FieldWidth + 30;
            CaptionLabel.TextAlignment = TextAlignment.Center;
            CaptionLabel.HorizontalAlignment = HorizontalAlignment.Left;
            CaptionLabel.Margin = new Thickness(0, 3, 0, 0);
            col.Children.Add(CaptionLabel);
        }
        Children.Add(col);
        if (unit != null)
        {
            UnitLabel = Mac.Text(unit);
            UnitLabel.Margin = new Thickness(10, 6, 0, 0);
            UnitLabel.VerticalAlignment = VerticalAlignment.Top;
            Children.Add(UnitLabel);
        }
        IsEnabledChanged += (_, _) => Refresh();
        KeyWindowState.Attach(this, Refresh);
        Refresh();
    }

    public void FocusField()
    {
        _box.Focus();
        Keyboard.Focus(_box);
        _box.SelectAll();
    }

    public void DevShowFocus() { _devFocused = true; Refresh(); }

    public int Value
    {
        get => _value;
        set { _value = value; _box.Text = value.ToString(CultureInfo.InvariantCulture); }
    }

    void Step(int d)
    {
        if (!IsEnabled) return;
        Commit();
        var v = Math.Clamp(_value + d, _min, _max);
        if (v == _value) return;
        Value = v;
        ValueChanged?.Invoke(v);
    }

    void Commit()
    {
        if (int.TryParse(_box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= _min && v <= _max)
        {
            if (v != _value) { _value = v; ValueChanged?.Invoke(v); }
        }
        _box.Text = _value.ToString(CultureInfo.InvariantCulture);
    }

    void Refresh()
    {
        bool enabled = IsEnabled;
        bool focused = (_box.IsKeyboardFocused || _devFocused) && KeyWindowState.IsActive(this);
        _box.Foreground = enabled ? Theme.TextPrimary : Theme.TextDisabled;
        _box.CaretBrush = Theme.TextPrimary;
        _box.SelectionBrush = Theme.Accent;
        _field.Background = focused ? Theme.Control.InputFillFocused : enabled ? Theme.Control.InputFill : Theme.Control.FillDisabled;
        _field.BorderBrush = Theme.Control.Border;
        _underline.Height = focused ? 2 : 1;
        _underline.Background = focused ? Theme.Accent : enabled ? Theme.Control.InputBottom : Brushes.Transparent;
        _up.Foreground = _down.Foreground = enabled ? Theme.TextSecondary : Theme.TextDisabled;
        if (UnitLabel != null) UnitLabel.Foreground = enabled ? Theme.TextPrimary : Theme.TextDisabled;
        if (CaptionLabel != null) CaptionLabel.Foreground = enabled ? Theme.TextSecondary : Theme.TextDisabled;
    }
}

public sealed record PopUpOption(object Tag, string? Title, ImageSource? Image = null, Size ImageSize = default);

/// <summary>Windows 11 ComboBox (drop-down button).</summary>
public sealed class MacPopUp : Border
{
    readonly IReadOnlyList<PopUpOption> _options;
    readonly Grid _content = new();
    readonly TextBlock _chevron;
    int _index;
    bool _hover;
    public event Action<object>? SelectionChanged;

    public MacPopUp(IReadOnlyList<PopUpOption> options, object selected, double? minWidth = null)
    {
        _options = options;
        _index = Math.Max(0, options.ToList().FindIndex(o => Equals(o.Tag, selected)));
        Height = 32;
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        _chevron = Mac.Icon("\uE70D", 12, Theme.TextSecondary);
        _chevron.HorizontalAlignment = HorizontalAlignment.Right;
        _chevron.Margin = new Thickness(0, 0, 12, 0);
        var g = new Grid();
        g.Children.Add(_content);
        g.Children.Add(_chevron);
        Child = g;
        double widest = options.Max(o => o.Image != null ? Math.Max(o.ImageSize.Width + 8 + Measure(o.Title ?? ""), o.ImageSize.Width) : Measure(o.Title ?? ""));
        Width = Math.Ceiling(11 + widest + 12 + 12 + 14);
        Width = Math.Max(Width, minWidth ?? 120);
        MouseEnter += (_, _) => { _hover = true; Refresh(); };
        MouseLeave += (_, _) => { _hover = false; Refresh(); };
        MouseLeftButtonDown += (_, e) => { if (IsEnabled) { OpenMenu(); e.Handled = true; } };
        IsEnabledChanged += (_, _) => Refresh();
        KeyWindowState.Attach(this, Refresh);
        Refresh();
    }

    static double Measure(string s)
    {
        var t = Mac.Text(s);
        t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return t.DesiredSize.Width;
    }

    public object SelectedTag => _options[_index].Tag;

    void Refresh()
    {
        bool enabled = IsEnabled;
        Background = !enabled ? Theme.Control.FillDisabled : _hover ? Theme.Control.FillHover : Theme.Control.Fill;
        BorderBrush = Mac.ElevationBorder();
        var fg = enabled ? Theme.TextPrimary : Theme.TextDisabled;
        _chevron.Foreground = enabled ? Theme.TextSecondary : Theme.TextDisabled;
        _content.Children.Clear();
        var o = _options[_index];
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(11, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center };
        if (o.Image != null)
        {
            row.Children.Add(new Border
            {
                Width = o.ImageSize.Width,
                Height = o.ImageSize.Height,
                Background = fg,
                OpacityMask = new ImageBrush(o.Image),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
        }
        row.Children.Add(Mac.Text(o.Title ?? "", 13, fg));
        _content.Children.Add(row);
    }

    void OpenMenu()
    {
        var items = new List<MacMenuItem>();
        for (int i = 0; i < _options.Count; i++)
        {
            var o = _options[i];
            int idx = i;
            var item = new MacMenuItem
            {
                Title = o.Title ?? "",
                IsChecked = i == _index,
                Action = () =>
                {
                    _index = idx;
                    Refresh();
                    SelectionChanged?.Invoke(o.Tag);
                },
            };
            if (o.Image != null)
            {
                item.Image = o.Image;
                item.ImageIsTemplate = true;
                item.ImageSize = o.ImageSize;
            }
            items.Add(item);
        }
        var w = new MacMenuWindow(items, null, popUpOwner: Window.GetWindow(this), minWidth: ActualWidth);
        var origin = PointToScreen(new Point(0, 0));
        var src = PresentationSource.FromVisual(this);
        var m = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var dip = m.Transform(origin);
        // Like a WinUI ComboBox, the list opens with the selected item over the control.
        double y = dip.Y + ActualHeight / 2 - MacMenuView.Padding - _index * MacMenuView.RowHeight - MacMenuView.RowHeight / 2;
        w.ShowAt(new Point(dip.X, y), selectIndex: _index);
    }
}

/// <summary>Windows 11 button.</summary>
public sealed class MacButton : Border
{
    readonly TextBlock _label;
    readonly bool _prominent;
    bool _pressed, _hover;
    public event Action? Click;

    public MacButton(string title, bool prominent = false, double height = 32, double fontSize = 13, double hPad = 12, bool capsule = false)
    {
        _prominent = prominent;
        _label = Mac.Text(title, fontSize);
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        _label.VerticalAlignment = VerticalAlignment.Center;
        Child = _label;
        Height = Math.Max(height, 32);
        MinWidth = 96;
        Padding = new Thickness(hPad, 0, hPad, 1);
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        HorizontalAlignment = HorizontalAlignment.Left;
        MouseEnter += (_, _) => { _hover = true; Refresh(); };
        MouseLeave += (_, _) => { _hover = false; Refresh(); };
        MouseLeftButtonDown += (_, e) => { if (IsEnabled) { _pressed = true; CaptureMouse(); Refresh(); e.Handled = true; } };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!_pressed) return;
            _pressed = false;
            ReleaseMouseCapture();
            Refresh();
            if (IsMouseOver) Click?.Invoke();
            e.Handled = true;
        };
        IsEnabledChanged += (_, _) => Refresh();
        KeyWindowState.Attach(this, Refresh);
        Refresh();
    }

    public bool IsDefault { get; set; }

    void Refresh()
    {
        bool enabled = IsEnabled;
        if ((_prominent || IsDefault) && enabled)
        {
            Background = _pressed ? Theme.AccentPressed : _hover ? Theme.Brush(Theme.Mix(Theme.AccentFillColor, Theme.IsDark ? Colors.Black : Colors.White, 0.1)) : Theme.Accent;
            BorderBrush = Mac.ElevationBorder(accent: true);
            _label.Foreground = Theme.OnAccent;
        }
        else
        {
            Background = !enabled ? Theme.Control.FillDisabled : _pressed ? Theme.Control.FillPressed : _hover ? Theme.Control.FillHover : Theme.Control.Fill;
            BorderBrush = Mac.ElevationBorder();
            _label.Foreground = !enabled ? Theme.TextDisabled : _pressed ? Theme.TextSecondary : Theme.TextPrimary;
        }
    }
}

public sealed class MacDivider : Border
{
    public MacDivider(bool dark)
    {
        Height = 1;
        Background = Theme.Window.Separator;
        VerticalAlignment = VerticalAlignment.Center;
    }
}

/// <summary>Shortcut recorder drawn PowerToys-style: each key is a keycap.</summary>
public sealed class MacShortcutRecorder : Border
{
    KeyCombo? _combo;
    bool _recording, _hover;
    readonly StackPanel _keys = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    readonly Border _clear;
    readonly bool _canClear;
    MacModifiers _liveMods;
    public Color Tint { get; set; } = Theme.AccentColor;
    public Brush? CustomBorder { get; set; }
    public double GlyphSize { get; set; } = 19;
    public bool AlwaysShowsClear { get; set; }
    public event Action<KeyCombo?>? Changed;
    public Func<bool>? CanBeginRecording { get; set; }

    public MacShortcutRecorder(KeyCombo? combo, bool canClear = true, double width = 240, double height = 32, double radius = 8)
    {
        _combo = combo;
        _canClear = canClear;
        Width = width;
        Height = Math.Max(36, height);
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        var g = new Grid();
        g.Children.Add(_keys);
        _clear = new Border
        {
            Width = 28,
            Height = 26,
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Arrow,
            Child = Mac.Icon("\uE711", 10, Theme.TextSecondary),
            ToolTip = "Clear",
        };
        _clear.MouseEnter += (_, _) => _clear.Background = Theme.Control.FillHover;
        _clear.MouseLeave += (_, _) => _clear.Background = Brushes.Transparent;
        _clear.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _combo = null;
            EndRecording();
            Changed?.Invoke(null);
            Refresh();
        };
        g.Children.Add(_clear);
        Child = g;
        MouseEnter += (_, _) => { _hover = true; Refresh(); };
        MouseLeave += (_, _) => { _hover = false; Refresh(); };
        MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_recording) { EndRecording(); return; }
            if (CanBeginRecording != null && !CanBeginRecording()) return;
            _recording = true;
            _liveMods = MacModifiers.None;
            HotKeyService.Shared.Suspend(true);
            Focus();
            Keyboard.Focus(this);
            Refresh();
        };
        LostKeyboardFocus += (_, _) => EndRecording();
        PreviewKeyDown += OnKey;
        PreviewKeyUp += (_, _) => { if (_recording) { _liveMods = CurrentMods(); Refresh(); } };
        Unloaded += (_, _) => EndRecording();
        KeyWindowState.Attach(this, Refresh);
        Refresh();
    }

    public KeyCombo? KeyCombo
    {
        get => _combo;
        set { _combo = value; Refresh(); }
    }

    static MacModifiers CurrentMods() =>
        Core.KeyCombo.FromWpf(Keyboard.Modifiers, Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin));

    void OnKey(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        _liveMods = CurrentMods();
        if (KeyNames.IsModifierKey(key)) { Refresh(); return; }
        if (key == Key.Escape && _liveMods == MacModifiers.None) { EndRecording(); return; }
        if ((key == Key.Back || key == Key.Delete) && _liveMods == MacModifiers.None && _canClear)
        {
            _combo = null;
            EndRecording();
            Changed?.Invoke(null);
            return;
        }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        bool isFKey = vk is >= 0x70 and <= 0x87;
        if (_liveMods == MacModifiers.None && !isFKey) return;
        _combo = new KeyCombo(vk, _liveMods);
        EndRecording();
        Changed?.Invoke(_combo);
    }

    void EndRecording()
    {
        if (!_recording) return;
        _recording = false;
        HotKeyService.Shared.Suspend(false);
        Refresh();
    }

    FrameworkElement Keycap(string text, bool live)
    {
        var t = Mac.Text(text, 12, live ? Theme.TextPrimary : Theme.OnAccent, FontWeights.SemiBold);
        t.VerticalAlignment = VerticalAlignment.Center;
        return new Border
        {
            Child = t,
            Padding = new Thickness(8, 0, 8, 1),
            Height = 24,
            MinWidth = 24,
            CornerRadius = new CornerRadius(4),
            Background = live ? Theme.Control.FillHover : Theme.Accent,
            BorderBrush = live ? Theme.Control.Border : Mac.ElevationBorder(accent: true),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 4, 0),
        };
    }

    void Refresh()
    {
        bool focusedRec = _recording;
        Background = _hover || focusedRec ? Theme.Control.FillHover : Theme.Control.InputFill;
        BorderBrush = CustomBorder != null && !focusedRec ? CustomBorder : focusedRec ? Theme.Accent : Mac.ElevationBorder();
        BorderThickness = new Thickness(focusedRec ? 2 : 1);
        _keys.Children.Clear();
        var mods = _recording ? _liveMods : _combo?.Modifiers ?? MacModifiers.None;
        var names = Core.KeyCombo.ModifierNames(mods).ToList();
        if (!_recording && _combo != null) names.Add(_combo.KeyName);
        foreach (var n in names) _keys.Children.Add(Keycap(n, _recording));
        if (names.Count == 0)
        {
            var hint = Mac.Text(_recording ? "Press a shortcut…" : "Not set", 13, _recording ? Theme.TextSecondary : Theme.TextTertiary);
            hint.Margin = new Thickness(6, 0, 0, 1);
            hint.VerticalAlignment = VerticalAlignment.Center;
            _keys.Children.Add(hint);
        }
        _clear.Visibility = _canClear && _combo != null && !_recording ? Visibility.Visible : Visibility.Collapsed;
    }
}
