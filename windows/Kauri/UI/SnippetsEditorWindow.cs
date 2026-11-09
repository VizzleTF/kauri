using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Xml.Linq;
using Kauri.Core;

namespace Kauri.UI;

/// <summary>Port of CPYSnippetsEditorWindowController (always light, like the macOS build).</summary>
public sealed class SnippetsEditorWindow : MacWindow
{
    static SnippetsEditorWindow? _shared;
    static Brush TitleColor => Theme.TextPrimary;
    static Brush BlueText => Theme.Accent;
    static Brush Bg => Theme.IsDark ? Theme.Gray(0x20) : Theme.Gray(0xF3);
    static Brush PaneBg => Theme.Window.Card;

    readonly List<SnippetFolder> _folders;
    readonly HashSet<Guid> _expanded = new();
    object? _selected;
    readonly StackPanel _rows = new();
    readonly ScrollViewer _treeScroll;
    readonly Grid _right = new();
    readonly Grid _folderPanel;
    readonly TextBlock _folderTitle;
    readonly MacShortcutRecorder _recorder;
    readonly TextBox _text;
    readonly TextBlock _placeholder;
    readonly Grid _textHost = new();
    bool _suppressText;
    readonly ColumnDefinition _leftCol = new() { Width = new GridLength(260) };

    public SnippetsEditorWindow() : base(minimizable: true, zoomable: true)
    {
        Resizable = true;
        MacTitle = AppInfo.DisplayName + " – Snippets";
        MinWidth = 560;
        MinHeight = 380;
        Width = 860;
        Height = 620;

        _folders = Database.Shared.FetchFolders();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        // ---- Toolbar (command bar) ----
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
        (string Label, string Glyph, Action Act)[] buttons =
        {
            ("Add Snippet", "\uE710", AddSnippet),
            ("Add Folder", "\uE8F4", AddFolder),
            ("Delete", "\uE74D", DeleteItem),
            ("Enable/Disable", "\uE7B3", ToggleEnabled),
            ("Import", "\uE8B5", Import),
            ("Export", "\uEDE1", Export),
        };
        foreach (var b in buttons)
        {
            var g = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6, 10, 6), Background = Brushes.Transparent, Margin = new Thickness(0, 0, 2, 0), Cursor = Cursors.Hand };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Mac.Icon(b.Glyph, 16, Theme.TextPrimary);
            icon.Margin = new Thickness(0, 0, 8, 0);
            sp.Children.Add(icon);
            var label = Mac.Text(b.Label, 13, Theme.TextPrimary);
            label.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(label);
            g.Child = sp;
            var act = b.Act;
            g.MouseEnter += (_, _) => g.Background = Theme.Control.FillHover;
            g.MouseLeave += (_, _) => g.Background = Brushes.Transparent;
            g.MouseLeftButtonDown += (_, e) => { e.Handled = true; g.Background = Theme.Control.FillPressed; g.CaptureMouse(); };
            g.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                g.ReleaseMouseCapture();
                g.Background = g.IsMouseOver ? Theme.Control.FillHover : Brushes.Transparent;
                if (g.IsMouseOver) act();
            };
            toolbar.Children.Add(g);
        }
        root.Children.Add(toolbar);

        // ---- Split view (two cards) ----
        var split = new Grid { Margin = new Thickness(12, 0, 12, 12) };
        Grid.SetRow(split, 1);
        split.ColumnDefinitions.Add(_leftCol);
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        split.ColumnDefinitions.Add(new ColumnDefinition());
        _treeScroll = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true,
            FocusVisualStyle = null,
        };
        _rows.Margin = new Thickness(0, 4, 0, 4);
        _treeScroll.PreviewKeyDown += TreeKeyDown;
        _treeScroll.MouseLeftButtonDown += (_, _) => { Select(null); };
        split.Children.Add(Card(_treeScroll));
        var divider = new Border { Background = Brushes.Transparent, Cursor = Cursors.SizeWE };
        Grid.SetColumn(divider, 1);
        divider.MouseLeftButtonDown += (_, e) => { divider.CaptureMouse(); e.Handled = true; };
        divider.MouseMove += (_, e) =>
        {
            if (!divider.IsMouseCaptured) return;
            var x = e.GetPosition(split).X;
            _leftCol.Width = new GridLength(Math.Clamp(x, 180, split.ActualWidth / 2));
        };
        divider.MouseLeftButtonUp += (_, _) => divider.ReleaseMouseCapture();
        split.Children.Add(divider);

        var rightCard = Card(_right);
        Grid.SetColumn(rightCard, 2);
        split.Children.Add(rightCard);

        // Folder settings view
        _folderPanel = new Grid { Width = 300, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden };
        var fp = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var fIcon = Mac.Icon("\uE8B7", 20, BlueText);
        fIcon.Margin = new Thickness(0, 0, 10, 0);
        titleRow.Children.Add(fIcon);
        _folderTitle = new TextBlock { FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = TitleColor, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(_folderTitle);
        fp.Children.Add(titleRow);
        var shortcutLabel = Mac.Text("Shortcut", 13, Theme.TextSecondary);
        shortcutLabel.Margin = new Thickness(0, 20, 0, 6);
        fp.Children.Add(shortcutLabel);
        _recorder = new MacShortcutRecorder(null, true, 300, 36)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            CanBeginRecording = () => SelectedFolder != null,
        };
        _recorder.Changed += c => { if (SelectedFolder is { } f) HotKeyService.Shared.ChangeFolder(f.Id, c); };
        fp.Children.Add(_recorder);
        _folderPanel.Children.Add(fp);
        _right.Children.Add(_folderPanel);

        // Snippet text view
        _text = new TextBox
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI"),
            FontSize = 14,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Theme.TextPrimary,
            CaretBrush = Theme.TextPrimary,
            Padding = new Thickness(12, 12, 12, 12),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FocusVisualStyle = null,
            SelectionBrush = Theme.Accent,
            SelectionOpacity = 0.4,
        };
        _placeholder = Mac.Text("Please fill in the contents of the snippet", 13, Theme.TextTertiary);
        _placeholder.IsHitTestVisible = false;
        _placeholder.Margin = new Thickness(16, 12, 0, 0);
        _placeholder.VerticalAlignment = VerticalAlignment.Top;
        _placeholder.HorizontalAlignment = HorizontalAlignment.Left;
        _textHost.Children.Add(_text);
        _textHost.Children.Add(_placeholder);
        _textHost.Visibility = Visibility.Hidden;
        _right.Children.Add(_textHost);
        _text.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _text.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
            if (_suppressText) return;
            if (_selected is Snippet s)
            {
                s.Content = _text.Text;
                Database.Shared.UpdateSnippet(s);
            }
        };
        root.Children.Add(split);
        BodyContent = root;

        Rebuild();
        if (_folders.Count > 0) Select(_folders[0]);
        else ChangeItemFocus();
    }

    static Border Card(UIElement child) => new()
    {
        Child = child,
        CornerRadius = new CornerRadius(8),
        Background = PaneBg,
        BorderBrush = Theme.Window.CardBorder,
        BorderThickness = new Thickness(1),
        ClipToBounds = true,
    };

    protected override Brush WindowBackground => Bg;

    internal void DevSelectFirstSnippet()
    {
        var f = _folders.FirstOrDefault(x => x.Snippets.Count > 0);
        if (f == null) return;
        _expanded.Add(f.Id);
        Select(f.Snippets[0]);
    }

    public static void ShowShared()
    {
        if (_shared == null)
        {
            _shared = new SnippetsEditorWindow();
            _shared.Closed += (_, _) => _shared = null;
            _shared.CenterOnScreen();
            _shared.Show();
        }
        _shared.Activate();
        Native.ForceForeground(new System.Windows.Interop.WindowInteropHelper(_shared).Handle);
    }

    SnippetFolder? SelectedFolder => _selected switch
    {
        SnippetFolder f => f,
        Snippet s => _folders.FirstOrDefault(f => f.Id == s.FolderId),
        _ => null,
    };

    // ---------------- Tree ----------------

    IEnumerable<object> Visible()
    {
        foreach (var f in _folders)
        {
            yield return f;
            if (_expanded.Contains(f.Id))
                foreach (var s in f.Snippets) yield return s;
        }
    }

    void Rebuild()
    {
        _rows.Children.Clear();
        foreach (var item in Visible()) _rows.Children.Add(Row(item));
    }

    const double RowPitch = 34;

    FrameworkElement Row(object item)
    {
        bool selected = ReferenceEquals(item, _selected);
        bool isFolder = item is SnippetFolder;
        var folder = item as SnippetFolder;
        var snippet = item as Snippet;
        bool enabled = folder?.IsEnabled ?? snippet!.IsEnabled;
        double level = isFolder ? 0 : 1;
        var g = new Grid { Height = RowPitch, Background = Brushes.Transparent, Tag = item };
        var hl = new Border
        {
            Margin = new Thickness(4, 1, 4, 1),
            CornerRadius = new CornerRadius(4),
            Background = selected ? Theme.Menu.Hover : Brushes.Transparent,
        };
        g.Children.Add(hl);
        if (selected)
            g.Children.Add(new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Background = Theme.Accent, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0, 0, 0) });
        g.MouseEnter += (_, _) => { if (!ReferenceEquals(item, _selected)) hl.Background = Theme.Control.FillHover; };
        g.MouseLeave += (_, _) => { if (!ReferenceEquals(item, _selected)) hl.Background = Brushes.Transparent; };
        Brush fg = !enabled ? Theme.TextDisabled : TitleColor;
        if (isFolder && folder!.Snippets.Count > 0)
        {
            bool open = _expanded.Contains(folder.Id);
            var chev = Mac.Icon(open ? "\uE70D" : "\uE76C", 10, Theme.TextSecondary);
            chev.HorizontalAlignment = HorizontalAlignment.Left;
            chev.Margin = new Thickness(14, 0, 0, 0);
            var hit = new Border { Width = 26, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 0, 0, 0) };
            hit.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (!_expanded.Remove(folder.Id)) _expanded.Add(folder.Id);
                Rebuild();
            };
            g.Children.Add(chev);
            g.Children.Add(hit);
        }
        double x = 34 + level * 22;
        var icon = Mac.Icon(isFolder ? "\uE8B7" : "\uE8A5", 16, !enabled ? Theme.TextDisabled : isFolder ? BlueText : Theme.TextSecondary);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.Margin = new Thickness(x, 0, 0, 0);
        g.Children.Add(icon);
        x += 26;
        var title = Mac.Text(folder?.Title ?? snippet!.Title, 13, fg);
        title.VerticalAlignment = VerticalAlignment.Center;
        title.HorizontalAlignment = HorizontalAlignment.Left;
        title.Margin = new Thickness(x, 0, 12, 1);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        g.Children.Add(title);

        g.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _treeScroll.Focus();
            if (e.ClickCount == 2) { BeginRename(g, item); return; }
            Select(item);
            _dragItem = item;
            _dragStart = e.GetPosition(_rows);
        };
        g.MouseMove += (_, e) => TryStartDrag(e);
        g.AllowDrop = true;
        g.DragOver += (_, e) => OnDragOver(e, item, g);
        g.Drop += (_, e) => OnDrop(e, item, g);
        return g;
    }

    void Select(object? item)
    {
        _selected = item;
        Rebuild();
        ChangeItemFocus();
    }

    void ChangeItemFocus()
    {
        switch (_selected)
        {
            case SnippetFolder f:
                _suppressText = true;
                _text.Text = "";
                _suppressText = false;
                _folderTitle.Text = f.Title;
                _recorder.KeyCombo = HotKeyService.Shared.FolderKeyCombo(f.Id);
                _folderPanel.Visibility = Visibility.Visible;
                _textHost.Visibility = Visibility.Hidden;
                break;
            case Snippet s:
                _suppressText = true;
                _text.Text = s.Content;
                _text.CaretIndex = 0;
                _suppressText = false;
                _placeholder.Visibility = s.Content.Length == 0 ? Visibility.Visible : Visibility.Hidden;
                _folderTitle.Text = "";
                _recorder.KeyCombo = null;
                _folderPanel.Visibility = Visibility.Hidden;
                _textHost.Visibility = Visibility.Visible;
                break;
            default:
                _folderPanel.Visibility = Visibility.Hidden;
                _textHost.Visibility = Visibility.Hidden;
                _recorder.KeyCombo = null;
                _folderTitle.Text = "";
                break;
        }
    }

    void TreeKeyDown(object sender, KeyEventArgs e)
    {
        if (_renaming != null) return;
        var vis = Visible().ToList();
        int i = _selected == null ? -1 : vis.FindIndex(o => ReferenceEquals(o, _selected));
        switch (e.Key)
        {
            case Key.Down when vis.Count > 0: Select(vis[Math.Min(vis.Count - 1, i + 1)]); e.Handled = true; break;
            case Key.Up when vis.Count > 0: Select(vis[Math.Max(0, i - 1)]); e.Handled = true; break;
            case Key.Right when _selected is SnippetFolder f: _expanded.Add(f.Id); Rebuild(); e.Handled = true; break;
            case Key.Left when _selected is SnippetFolder f: _expanded.Remove(f.Id); Rebuild(); e.Handled = true; break;
            case Key.Left when _selected is Snippet: Select(SelectedFolder); e.Handled = true; break;
            case Key.Enter when _selected != null:
                var row = _rows.Children.OfType<Grid>().FirstOrDefault(r => ReferenceEquals(r.Tag, _selected));
                if (row != null) BeginRename(row, _selected);
                e.Handled = true;
                break;
            case Key.Delete or Key.Back when _selected != null: DeleteItem(); e.Handled = true; break;
        }
    }

    // ---- Inline rename (NSOutlineView editable cell) ----
    TextBox? _renaming;

    void BeginRename(Grid row, object item)
    {
        Select(item);
        row = _rows.Children.OfType<Grid>().First(r => ReferenceEquals(r.Tag, item));
        var title = row.Children.OfType<TextBlock>().First();
        var box = new TextBox
        {
            Text = title.Text,
            FontFamily = Theme.Font,
            FontSize = 14,
            Foreground = TitleColor,
            Background = Theme.Control.InputFillFocused,
            CaretBrush = TitleColor,
            BorderBrush = Theme.Accent,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            Margin = new Thickness(title.Margin.Left - 2, 2, 12, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        title.Visibility = Visibility.Hidden;
        row.Children.Add(box);
        _renaming = box;
        box.Focus();
        box.SelectAll();
        void Commit(bool save)
        {
            if (_renaming != box) return;
            _renaming = null;
            var text = box.Text;
            if (save && text.Length > 0)
            {
                if (item is SnippetFolder f) { f.Title = text; Database.Shared.UpdateFolder(f); }
                else if (item is Snippet s) { s.Title = text; Database.Shared.UpdateSnippet(s); }
            }
            Rebuild();
            ChangeItemFocus();
            _treeScroll.Focus();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Commit(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Commit(false); }
        };
        box.LostKeyboardFocus += (_, _) => Commit(true);
    }

    // ---- Drag and drop ----
    object? _dragItem;
    Point _dragStart;
    const string DragFormat = "Kauri.SnippetItem";

    void TryStartDrag(MouseEventArgs e)
    {
        if (_dragItem == null || e.LeftButton != MouseButtonState.Pressed) { _dragItem = null; return; }
        var p = e.GetPosition(_rows);
        if (Math.Abs(p.Y - _dragStart.Y) < 4 && Math.Abs(p.X - _dragStart.X) < 4) return;
        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(_rows, new DataObject(DragFormat, item), DragDropEffects.Move);
        ClearDropIndicator();
    }

    Border? _indicator;

    void ClearDropIndicator()
    {
        if (_indicator?.Parent is Panel p) p.Children.Remove(_indicator);
        _indicator = null;
        foreach (var r in _rows.Children.OfType<Grid>())
            if (r.Children[0] is Border hl && !ReferenceEquals(r.Tag, _selected)) { hl.BorderThickness = new Thickness(0); }
    }

    (SnippetFolder? folder, int index, bool onto) DropTarget(object dragged, object target, Grid row, Point p)
    {
        bool upper = p.Y < row.ActualHeight / 2;
        if (dragged is SnippetFolder)
        {
            var tf = target as SnippetFolder ?? _folders.First(f => f.Id == ((Snippet)target).FolderId);
            int idx = _folders.IndexOf(tf) + (upper ? 0 : 1);
            return (null, idx, false);
        }
        if (target is SnippetFolder folder)
        {
            if (p.Y > row.ActualHeight * 0.25 && p.Y < row.ActualHeight * 0.75 || !_expanded.Contains(folder.Id))
                return (folder, folder.Snippets.Count, true);
            return (folder, upper ? -1 : 0, false);
        }
        var s = (Snippet)target;
        var f2 = _folders.First(f => f.Id == s.FolderId);
        return (f2, f2.Snippets.IndexOf(s) + (upper ? 0 : 1), false);
    }

    void OnDragOver(DragEventArgs e, object target, Grid row)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DragFormat)) { e.Effects = DragDropEffects.None; return; }
        var dragged = e.Data.GetData(DragFormat);
        var (folder, index, onto) = DropTarget(dragged!, target, row, e.GetPosition(row));
        if (dragged is Snippet && folder != null && index < 0) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        ClearDropIndicator();
        if (onto)
        {
            if (row.Children[0] is Border hl) { hl.BorderBrush = Theme.Accent; hl.BorderThickness = new Thickness(2); }
        }
        else
        {
            bool upper = e.GetPosition(row).Y < row.ActualHeight / 2;
            _indicator = new Border
            {
                Height = 2, Background = Theme.Accent, CornerRadius = new CornerRadius(1),
                VerticalAlignment = upper ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                Margin = new Thickness(dragged is Snippet ? 42 : 26, 0, 10, 0),
                IsHitTestVisible = false,
            };
            row.Children.Add(_indicator);
        }
    }

    void OnDrop(DragEventArgs e, object target, Grid row)
    {
        e.Handled = true;
        ClearDropIndicator();
        var dragged = e.Data.GetData(DragFormat);
        if (dragged == null) return;
        var (folder, index, _) = DropTarget(dragged, target, row, e.GetPosition(row));
        if (dragged is SnippetFolder df)
        {
            int from = _folders.IndexOf(df);
            if (index == from || index == from + 1) return;
            _folders.Insert(index, df);
            _folders.RemoveAt(index < from ? from + 1 : from);
            Database.Shared.UpdateFolderIndexes(_folders);
        }
        else if (dragged is Snippet ds && folder != null && index >= 0)
        {
            var fromFolder = _folders.First(f => f.Id == ds.FolderId);
            int from = fromFolder.Snippets.IndexOf(ds);
            if (fromFolder == folder)
            {
                if (index == from || index == from + 1) return;
                folder.Snippets.Insert(index, ds);
                folder.Snippets.RemoveAt(index < from ? from + 1 : from);
                Database.Shared.UpdateSnippetIndexes(folder.Id, folder.Snippets);
            }
            else
            {
                folder.Snippets.Insert(Math.Min(index, folder.Snippets.Count), ds);
                fromFolder.Snippets.Remove(ds);
                Database.Shared.UpdateSnippetIndexes(folder.Id, folder.Snippets);
                Database.Shared.UpdateSnippetIndexes(fromFolder.Id, fromFolder.Snippets);
                _expanded.Add(folder.Id);
            }
        }
        Select(dragged);
    }

    // ---------------- Actions ----------------

    void AddSnippet()
    {
        var folder = SelectedFolder;
        if (folder == null) { System.Media.SystemSounds.Beep.Play(); return; }
        var s = Database.Shared.InsertSnippet(folder.Id);
        folder.Snippets.Add(s);
        _expanded.Add(folder.Id);
        Select(s);
    }

    void AddFolder()
    {
        var f = Database.Shared.InsertFolder();
        _folders.Add(f);
        Select(f);
    }

    void DeleteItem()
    {
        if (_selected == null) { System.Media.SystemSounds.Beep.Play(); return; }
        var (r, _) = MacAlert.Show("Delete Item", "Are you sure want to delete this item?", new[] { "Delete Item", "Cancel" });
        if (r != 0) return;
        if (_selected is SnippetFolder f)
        {
            _folders.Remove(f);
            Database.Shared.DeleteFolder(f.Id);
            HotKeyService.Shared.ChangeFolder(f.Id, null);
        }
        else if (_selected is Snippet s)
        {
            _folders.FirstOrDefault(x => x.Id == s.FolderId)?.Snippets.Remove(s);
            Database.Shared.DeleteSnippet(s.Id);
        }
        Select(null);
    }

    void ToggleEnabled()
    {
        if (_selected is SnippetFolder f) { f.IsEnabled = !f.IsEnabled; Database.Shared.UpdateFolder(f); }
        else if (_selected is Snippet s) { s.IsEnabled = !s.IsEnabled; Database.Shared.UpdateSnippet(s); }
        else { System.Media.SystemSounds.Beep.Play(); return; }
        Rebuild();
        ChangeItemFocus();
    }

    void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "XML (*.xml)|*.xml",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var doc = XDocument.Load(dlg.FileName, LoadOptions.PreserveWhitespace);
            var rootEl = doc.Element("folders");
            if (rootEl == null) throw new InvalidDataException();
            foreach (var fe in rootEl.Elements())
            {
                var folder = Database.Shared.InsertFolder(fe.Element("title")?.Value ?? "untitled folder");
                foreach (var se in fe.Element("snippets")?.Elements("snippet") ?? Enumerable.Empty<XElement>())
                    folder.Snippets.Add(Database.Shared.InsertSnippet(folder.Id, se.Element("title")?.Value ?? "untitled snippet", se.Element("content")?.Value ?? ""));
                _folders.Add(folder);
            }
            Rebuild();
        }
        catch { System.Media.SystemSounds.Beep.Play(); }
    }

    void Export()
    {
        var root = new XElement("folders",
            _folders.Select(f => new XElement("folder",
                new XElement("title", f.Title),
                new XElement("snippets", f.Snippets.Select(s => new XElement("snippet",
                    new XElement("title", s.Title),
                    new XElement("content", s.Content)))))));
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "XML (*.xml)|*.xml",
            FileName = "snippets",
            DefaultExt = ".xml",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog(this) != true) return;
        try { new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(dlg.FileName); }
        catch { System.Media.SystemSounds.Beep.Play(); }
    }
}
