using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Kauri.Core;

namespace Kauri.UI;

public sealed class MacMenuItem
{
    public string Title { get; set; } = "";
    /// <summary>When set, the title is rendered as "&lt;n&gt;  Title" with tabular digits.</summary>
    public int? Number { get; set; }
    /// <summary>When set, the title is rendered as "a – b" with tabular digits.</summary>
    public (int First, int Last)? Range { get; set; }
    /// <summary>Segoe Fluent Icons glyph shown in the icon column.</summary>
    public string? Glyph { get; set; }
    public ImageSource? Image { get; set; }
    /// <summary>Template images are tinted with the text colour.</summary>
    public bool ImageIsTemplate { get; set; }
    public Size ImageSize { get; set; }
    public Color? Swatch { get; set; }
    /// <summary>Key that is pressed together with Ctrl (shown as "Ctrl+key").</summary>
    public string? KeyEquivalent { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsChecked { get; set; }
    public bool IsHeader { get; set; }
    public bool IsSeparator { get; set; }
    public List<MacMenuItem>? Submenu { get; set; }
    public Action? Action { get; set; }
    public string? ToolTip { get; set; }

    public bool IsSelectable => !IsSeparator && !IsHeader && IsEnabled;
    public string PlainTitle => Range is { } r ? $"{r.First} - {r.Last}" : Number is int n ? $"{n}. {Title}" : Title;

    public static MacMenuItem Separator() => new() { IsSeparator = true, IsEnabled = false };
    public static MacMenuItem Header(string title) => new() { Title = title, IsHeader = true, IsEnabled = false };
}

/// <summary>Renders menu items as a Windows 11 menu flyout, keeping Clipy's menu structure.</summary>
public sealed class MacMenuView : Border
{
    public const double Padding = 4;
    public const double RowHeight = 30;
    public const double HeaderHeight = 28;
    public const double SeparatorHeight = 9;
    public const double CornerRadiusValue = 8;
    const double TextInset = 12;
    const double IconColumn = 28;
    const double Trailing = 12;
    const double ColumnGap = 28;
    const double HighlightInset = 4;
    const double HighlightRadius = 4;

    public IReadOnlyList<MacMenuItem> Items { get; }
    readonly List<FrameworkElement> _rows = new();
    readonly List<Border> _highlights = new();
    readonly StackPanel _stack = new();
    public ScrollViewer Scroller { get; } = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
    };
    int _selected = -1;
    bool _hasIconColumn, _hasStateColumn;
    public double MenuWidth { get; private set; }
    public double ContentHeight { get; private set; }

    public event Action<int>? RowHovered;
    public event Action<int>? RowClicked;

    public int SelectedIndex => _selected;

    public MacMenuView(IReadOnlyList<MacMenuItem> items, bool isSubmenu = false, double minWidth = 0)
    {
        Items = items;
        CornerRadius = new CornerRadius(CornerRadiusValue);
        Background = Theme.Menu.Background;
        BorderBrush = Theme.Menu.Border;
        BorderThickness = new Thickness(1);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        Child = Scroller;
        Scroller.Content = _stack;
        _stack.Margin = new Thickness(0, Padding - 1, 0, Padding - 1);
        Build(minWidth);
    }

    static TextBlock MakeText(string text, Brush fg, double size = 14, FontWeight? weight = null) => new()
    {
        Text = text,
        FontFamily = Theme.Font,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        Foreground = fg,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.None,
        Margin = new Thickness(0, 0, 0, 1),
    };

    static void AddTabularNumber(InlineCollection inlines, int n)
    {
        var run = new Run(n.ToString(CultureInfo.InvariantCulture));
        Typography.SetNumeralAlignment(run, FontNumeralAlignment.Tabular);
        inlines.Add(run);
    }

    int _digits = 1;

    TextBlock TitleBlock(MacMenuItem item, Brush fg)
    {
        if (item.IsHeader) return MakeText(item.Title, Theme.Menu.Secondary, 12, FontWeights.SemiBold);
        var t = MakeText("", fg);
        t.Text = null;
        if (item.Range is { } r)
        {
            AddTabularNumber(t.Inlines, r.First);
            t.Inlines.Add(new Run(" – "));
            AddTabularNumber(t.Inlines, r.Last);
        }
        else if (item.Number is int n)
        {
            // Right-align numbers in a fixed column using figure spaces (same width as tabular digits).
            var digits = n.ToString(CultureInfo.InvariantCulture);
            var num = new Run(new string(' ', Math.Max(0, _digits - digits.Length)) + digits) { Foreground = Theme.Menu.Secondary };
            Typography.SetNumeralAlignment(num, FontNumeralAlignment.Tabular);
            t.Inlines.Add(num);
            t.Inlines.Add(new Run("   " + item.Title));
        }
        else
        {
            t.Inlines.Add(new Run(item.Title));
        }
        return t;
    }

    static double Measure(FrameworkElement e)
    {
        e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return e.DesiredSize.Width;
    }

    static double IconWidth(MacMenuItem item) =>
        item.Glyph != null ? 16 : (item.Image != null || item.Swatch != null) ? item.ImageSize.Width : 0;

    double ItemTextStart(MacMenuItem item)
    {
        double x = TextInset + (_hasStateColumn ? 24 : 0);
        double iw = IconWidth(item);
        if (iw > 20) return x + iw + 12;
        if (_hasIconColumn && !item.IsHeader) return x + IconColumn;
        return x;
    }

    static double ItemRowHeight(MacMenuItem item)
    {
        if (item.IsSeparator) return SeparatorHeight;
        if (item.IsHeader) return HeaderHeight;
        if ((item.Image != null || item.Swatch != null) && item.ImageSize.Height > 20) return Math.Max(RowHeight, item.ImageSize.Height + 8);
        return RowHeight;
    }

    void Build(double minWidth)
    {
        _hasStateColumn = Items.Any(i => i.IsChecked);
        _digits = Items.Where(i => i.Number != null).Select(i => i.Number!.Value.ToString(CultureInfo.InvariantCulture).Length).DefaultIfEmpty(1).Max();
        _hasIconColumn = Items.Any(i => !i.IsHeader && IconWidth(i) is > 0 and <= 20);
        double maxTitleEnd = 0, rightCol = 0;
        foreach (var item in Items)
        {
            if (item.IsSeparator) continue;
            maxTitleEnd = Math.Max(maxTitleEnd, ItemTextStart(item) + Measure(TitleBlock(item, Theme.Menu.Text)));
            if (item.KeyEquivalent != null)
                rightCol = Math.Max(rightCol, Measure(ShortcutText(item.KeyEquivalent, Theme.Menu.Secondary)));
            if (item.Submenu != null)
                rightCol = Math.Max(rightCol, 12);
        }
        MenuWidth = Math.Ceiling(maxTitleEnd + (rightCol > 0 ? ColumnGap + rightCol : 0) + Trailing + 4);
        MenuWidth = Math.Max(MenuWidth, Math.Max(minWidth, 120));
        Width = MenuWidth;

        double h = 0;
        for (int i = 0; i < Items.Count; i++)
        {
            var row = BuildRow(i);
            _rows.Add(row);
            _stack.Children.Add(row);
            h += row.Height;
        }
        ContentHeight = h + Padding * 2;
    }

    static TextBlock ShortcutText(string key, Brush fg) => MakeText("Ctrl+" + key, fg, 12);

    FrameworkElement BuildRow(int index)
    {
        var item = Items[index];
        var grid = new Grid { Height = ItemRowHeight(item), Background = Brushes.Transparent, Tag = index };
        if (item.IsSeparator)
        {
            grid.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Background = Theme.Menu.Separator });
            _highlights.Add(new Border());
            return grid;
        }

        var hl = new Border
        {
            CornerRadius = new CornerRadius(HighlightRadius),
            Margin = new Thickness(HighlightInset, 1, HighlightInset, 1),
            Background = Brushes.Transparent,
        };
        _highlights.Add(hl);
        grid.Children.Add(hl);
        Populate(grid, item);

        if (!string.IsNullOrEmpty(item.ToolTip))
        {
            grid.ToolTip = new MacToolTip(item.ToolTip);
            ToolTipService.SetInitialShowDelay(grid, 800);
            ToolTipService.SetShowDuration(grid, 60000);
            ToolTipService.SetPlacement(grid, System.Windows.Controls.Primitives.PlacementMode.Mouse);
        }

        grid.MouseEnter += (_, _) => RowHovered?.Invoke(index);
        grid.MouseLeftButtonUp += (_, e) => { RowClicked?.Invoke(index); e.Handled = true; };
        return grid;
    }

    void Populate(Grid grid, MacMenuItem item)
    {
        Brush fg = item.IsEnabled || item.IsHeader ? Theme.Menu.Text : Theme.Menu.Disabled;
        double left = TextInset + (_hasStateColumn ? 24 : 0);

        if (item.IsChecked)
        {
            var check = Mac.Icon("\uE73E", 12, fg);
            check.HorizontalAlignment = HorizontalAlignment.Left;
            check.Margin = new Thickness(TextInset + 2, 0, 0, 0);
            grid.Children.Add(check);
        }

        if (item.Glyph != null)
        {
            var g = Mac.Icon(item.Glyph, 16, fg);
            g.HorizontalAlignment = HorizontalAlignment.Left;
            g.Margin = new Thickness(left, 0, 0, 0);
            grid.Children.Add(g);
        }
        else if (item.Image != null || item.Swatch != null)
        {
            FrameworkElement img;
            if (item.Swatch is Color c)
            {
                img = new Border { Background = new SolidColorBrush(c), Width = item.ImageSize.Width, Height = item.ImageSize.Height, CornerRadius = new CornerRadius(3), BorderBrush = Theme.Menu.Separator, BorderThickness = new Thickness(1) };
            }
            else if (item.ImageIsTemplate)
            {
                img = new Border
                {
                    Width = item.ImageSize.Width,
                    Height = item.ImageSize.Height,
                    Background = fg,
                    OpacityMask = new ImageBrush(item.Image) { Stretch = Stretch.Fill },
                };
            }
            else
            {
                img = new Image { Source = item.Image, Width = item.ImageSize.Width, Height = item.ImageSize.Height, Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            }
            img.HorizontalAlignment = HorizontalAlignment.Left;
            img.VerticalAlignment = VerticalAlignment.Center;
            img.Margin = new Thickness(left, 0, 0, 0);
            grid.Children.Add(img);
        }

        var title = TitleBlock(item, fg);
        title.HorizontalAlignment = HorizontalAlignment.Left;
        title.Margin = new Thickness(ItemTextStart(item), item.IsHeader ? 4 : 0, 0, 1);
        grid.Children.Add(title);

        if (item.KeyEquivalent != null)
        {
            var ke = ShortcutText(item.KeyEquivalent, Theme.Menu.Secondary);
            ke.HorizontalAlignment = HorizontalAlignment.Right;
            ke.Margin = new Thickness(0, 0, Trailing + 4, 1);
            grid.Children.Add(ke);
        }
        if (item.Submenu != null)
        {
            var chevron = Mac.Icon("\uE76C", 12, Theme.Menu.Secondary);
            chevron.HorizontalAlignment = HorizontalAlignment.Right;
            chevron.Margin = new Thickness(0, 0, Trailing + 2, 0);
            grid.Children.Add(chevron);
        }
    }

    public void SetSelected(int index, bool openSubmenuGrey = false)
    {
        if (_selected == index) return;
        var prev = _selected;
        _selected = index;
        if (prev >= 0 && prev < _rows.Count) Apply(prev);
        if (index >= 0) { Apply(index); _rows[index].BringIntoView(); }
    }

    void Apply(int i)
    {
        if (Items[i].IsSeparator) return;
        _highlights[i].Background = i == _selected ? Theme.Menu.Hover : Brushes.Transparent;
    }

    public double RowTop(int index)
    {
        double y = Padding;
        for (int i = 0; i < index; i++) y += _rows[i].Height;
        return y - Scroller.VerticalOffset;
    }
}

/// <summary>Windows 11 style tooltip.</summary>
public sealed class MacToolTip : ToolTip
{
    public MacToolTip(string text)
    {
        OverridesDefaultStyle = true;
        HasDropShadow = false;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        var t = new TextBlock
        {
            Text = text,
            FontFamily = Theme.Font,
            FontSize = 12,
            Foreground = Theme.TextPrimary,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
        };
        var b = new Border
        {
            Child = t,
            Padding = new Thickness(9, 6, 9, 8),
            CornerRadius = new CornerRadius(4),
            Background = Theme.IsDark ? Theme.Gray(0x2C) : Theme.Gray(0xF9),
            BorderBrush = Theme.IsDark ? Theme.Gray(0x00, 0.3) : Theme.Gray(0x00, 0.1),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 8),
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Direction = 270, Opacity = 0.18 },
        };
        Template = new ControlTemplate(typeof(ToolTip)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        Content = b;
    }
}

/// <summary>Popup window hosting a <see cref="MacMenuView"/>; behaves like NSMenu tracking.</summary>
public sealed class MacMenuWindow : Window
{
    public const double ShadowPad = 25;
    public MacMenuView View { get; }
    readonly MacMenuWindow? _parent;
    MacMenuWindow? _child;
    int _childIndex = -1;
    bool _closing;
    string _typeSelect = "";
    DateTime _typeSelectTime;
    readonly DispatcherTimer _submenuTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    int _pendingSubmenu = -1;
    Point _lastMouse;

    public event Action? MenuClosed;

    readonly Window? _popUpOwner;

    public MacMenuWindow(IReadOnlyList<MacMenuItem> items, MacMenuWindow? parent = null, Window? popUpOwner = null, double minWidth = 0)
    {
        _parent = parent;
        _popUpOwner = popUpOwner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowActivated = parent == null;
        Focusable = true;

        View = new MacMenuView(items, isSubmenu: parent != null, minWidth: minWidth);
        Content = BuildHost(View);

        View.RowHovered += OnRowHovered;
        View.RowClicked += i => Activate(i);
        _submenuTimer.Tick += (_, _) =>
        {
            _submenuTimer.Stop();
            if (_pendingSubmenu >= 0 && _pendingSubmenu == View.SelectedIndex) OpenSubmenu(_pendingSubmenu, false);
        };
        MouseWheel += (_, e) => View.Scroller.ScrollToVerticalOffset(View.Scroller.VerticalOffset - e.Delta / 3.0);
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLongW(hwnd, -20);
            ex |= 0x80; // WS_EX_TOOLWINDOW
            if (_parent != null) ex |= 0x08000000; // WS_EX_NOACTIVATE
            Native.SetWindowLongW(hwnd, -20, ex);
        };
        if (parent == null)
        {
            Deactivated += (_, _) => CloseAll();
            PreviewKeyDown += OnKeyDown;
            TextInput += OnTextInput;
        }
        MouseMove += (_, e) => _lastMouse = e.GetPosition(this);
    }

    /// <summary>Menu body plus the soft shadow, inside a <see cref="ShadowPad"/> margin.</summary>
    public static Grid BuildHost(MacMenuView view)
    {
        var host = new Grid { Margin = new Thickness(ShadowPad) };
        host.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(MacMenuView.CornerRadiusValue),
            Background = Theme.Menu.Background,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = Theme.IsDark ? 0.4 : 0.16, Color = Colors.Black },
        });
        host.Children.Add(view);
        return host;
    }

    MacMenuWindow Root => _parent?.Root ?? this;

    MacMenuWindow Deepest
    {
        get
        {
            var m = this;
            while (m._child != null && m._child.View.SelectedIndex >= 0) m = m._child;
            return m;
        }
    }

    /// <summary>Show with the menu body's top-left at <paramref name="point"/> (DIPs), fitting the work area.</summary>
    public void ShowAt(Point point, bool anchorBottom = false, bool highlightFirst = false, int selectIndex = -1)
    {
        var size = MeasureBody();
        var wa = WorkAreaAt(point);
        double x = point.X, y = anchorBottom ? point.Y - size.Height : point.Y;
        if (x + size.Width > wa.Right) x = Math.Max(wa.Left, wa.Right - size.Width);
        if (y + size.Height > wa.Bottom) y = Math.Max(wa.Top, wa.Bottom - size.Height);
        if (y < wa.Top) y = wa.Top;
        if (size.Height > wa.Height) View.Height = wa.Height;
        Left = x - ShadowPad;
        Top = y - ShadowPad;
        Show();
        if (_parent == null)
        {
            Native.ForceForeground(new WindowInteropHelper(this).Handle);
            Activate();
            Focus();
        }
        if (highlightFirst) SelectFirst();
        else if (selectIndex >= 0) View.SetSelected(selectIndex);
    }

    Size MeasureBody() => new(View.MenuWidth, View.ContentHeight);

    static Rect WorkAreaAt(Point p)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)p.X, (int)p.Y));
        // Screen works in physical pixels; convert using the system DPI scale of the primary visual.
        var src = PresentationSource.FromVisual(Application.Current.MainWindow ?? new Window());
        double scale = 1;
        using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96.0;
        var wa = screen.WorkingArea;
        return new Rect(wa.Left / scale, wa.Top / scale, wa.Width / scale, wa.Height / scale);
    }

    void SelectFirst()
    {
        for (int i = 0; i < View.Items.Count; i++)
            if (View.Items[i].IsSelectable) { View.SetSelected(i); return; }
    }

    void OnRowHovered(int index)
    {
        var p = Mouse.GetPosition(this);
        if (p == _lastMouse && View.SelectedIndex >= 0 && Root.IsKeyboardDriven) return;
        Root.IsKeyboardDriven = false;
        var item = View.Items[index];
        if (_child != null && _childIndex != index) CloseChild();
        if (!item.IsSelectable)
        {
            View.SetSelected(-1);
            return;
        }
        // Moving back into a parent menu clears the child's highlight.
        if (_child != null) _child.View.SetSelected(-1);
        View.SetSelected(index);
        if (item.Submenu != null && _child == null)
        {
            _pendingSubmenu = index;
            _submenuTimer.Stop();
            _submenuTimer.Start();
        }
        _parent?.MarkSubmenuFocus(this);
    }

    internal bool IsKeyboardDriven { get; set; }

    void MarkSubmenuFocus(MacMenuWindow child)
    {
        if (_child == child && _childIndex >= 0) View.SetSelected(_childIndex, true);
        _parent?.MarkSubmenuFocus(this);
    }

    void OpenSubmenu(int index, bool selectFirst)
    {
        var item = View.Items[index];
        if (item.Submenu == null || item.Submenu.Count == 0) return;
        if (_child != null && _childIndex == index)
        {
            if (selectFirst) { _child.SelectFirst(); View.SetSelected(index, true); }
            return;
        }
        CloseChild();
        _childIndex = index;
        _child = new MacMenuWindow(item.Submenu, this) { Owner = this };
        var bodyLeft = Left + ShadowPad;
        var bodyTop = Top + ShadowPad;
        var w = _child.View.MenuWidth;
        var x = bodyLeft + View.MenuWidth + 2;
        var y = bodyTop + View.RowTop(index) - MacMenuView.Padding;
        var wa = WorkAreaAt(new Point(x, y));
        if (x + w > wa.Right) x = bodyLeft - w - 2;
        _child.ShowAt(new Point(x, y));
        if (selectFirst)
        {
            _child.SelectFirst();
            View.SetSelected(index, true);
        }
    }

    void CloseChild()
    {
        if (_child == null) return;
        _child.CloseChild();
        _child._closing = true;
        _child.Close();
        _child = null;
        _childIndex = -1;
    }

    public void CloseAll()
    {
        var root = Root;
        if (root._closing) return;
        root._closing = true;
        root.CloseChild();
        root.Close();
        root._popUpOwner?.Activate();
        root.MenuClosed?.Invoke();
    }

    void Activate(int index)
    {
        var item = View.Items[index];
        if (!item.IsSelectable) return;
        if (item.Submenu != null)
        {
            OpenSubmenu(index, false);
            return;
        }
        var action = item.Action;
        CloseAll();
        if (action != null) Application.Current.Dispatcher.BeginInvoke(action, DispatcherPriority.Input);
    }

    void Move(MacMenuWindow m, int dir)
    {
        var items = m.View.Items;
        int n = items.Count;
        int i = m.View.SelectedIndex;
        for (int step = 0; step < n; step++)
        {
            i = i < 0 ? (dir > 0 ? 0 : n - 1) : (i + dir + n) % n;
            if (items[i].IsSelectable)
            {
                if (m._child != null && m._childIndex != i) m.CloseChild();
                m.View.SetSelected(i);
                return;
            }
        }
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        IsKeyboardDriven = true;
        var m = Deepest;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape:
                if (m._parent != null)
                {
                    var p = m._parent;
                    var idx = p._childIndex;
                    p.CloseChild();
                    p.View.SetSelected(idx);
                }
                else CloseAll();
                e.Handled = true;
                return;
            case Key.Down: Move(m, 1); e.Handled = true; return;
            case Key.Up: Move(m, -1); e.Handled = true; return;
            case Key.Right:
                if (m.View.SelectedIndex >= 0 && m.View.Items[m.View.SelectedIndex].Submenu != null)
                    m.OpenSubmenu(m.View.SelectedIndex, true);
                e.Handled = true;
                return;
            case Key.Left:
                if (m._parent != null)
                {
                    var p = m._parent;
                    var idx = p._childIndex;
                    p.CloseChild();
                    p.View.SetSelected(idx);
                }
                e.Handled = true;
                return;
            case Key.Enter:
            case Key.Space when _typeSelect.Length == 0:
                if (m.View.SelectedIndex >= 0)
                {
                    var it = m.View.Items[m.View.SelectedIndex];
                    if (it.Submenu != null) m.OpenSubmenu(m.View.SelectedIndex, true);
                    else m.Activate(m.View.SelectedIndex);
                }
                e.Handled = true;
                return;
        }
        // Key equivalents: ⌘(Ctrl)+key, or the bare digit for numbered history items.
        var ch = KeyChar(key);
        if (ch != null)
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var target = FindKeyEquivalent(m, ch, ctrl);
            if (target.menu != null)
            {
                target.menu.Activate(target.index);
                e.Handled = true;
            }
        }
    }

    static (MacMenuWindow? menu, int index) FindKeyEquivalent(MacMenuWindow m, string ch, bool ctrl)
    {
        for (var w = m; w != null; w = w._parent)
            for (int i = 0; i < w.View.Items.Count; i++)
            {
                var it = w.View.Items[i];
                if (it.KeyEquivalent == ch && it.IsSelectable && (ctrl || char.IsDigit(ch[0]))) return (w, i);
            }
        return (null, -1);
    }

    static string? KeyChar(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        Key.OemComma => ",",
        _ => null,
    };

    void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var m = Deepest;
        if ((DateTime.Now - _typeSelectTime).TotalSeconds > 1) _typeSelect = "";
        _typeSelectTime = DateTime.Now;
        _typeSelect += e.Text;
        if (char.IsDigit(e.Text[0]) && _typeSelect.Length == 1 && FindKeyEquivalent(m, e.Text, false).menu != null) { _typeSelect = ""; return; }
        for (int i = 0; i < m.View.Items.Count; i++)
        {
            var it = m.View.Items[i];
            if (it.IsSelectable && it.PlainTitle.StartsWith(_typeSelect, StringComparison.CurrentCultureIgnoreCase))
            {
                if (m._child != null && m._childIndex != i) m.CloseChild();
                m.View.SetSelected(i);
                break;
            }
        }
        e.Handled = true;
    }
}
