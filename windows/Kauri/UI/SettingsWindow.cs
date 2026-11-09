using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Kauri.Core;

namespace Kauri.UI;

public enum SettingsPane { General, Menu, Type, Exclude, Shortcuts, Sync, Beta, About }

/// <summary>Port of Clipy's Settings window (sindresorhus/Settings toolbar style + SwiftUI panes).</summary>
public sealed class SettingsWindow : MacWindow
{
    public const double BodyWidth = SettingsLayout.ContentWidth + 56;
    public const double ToolbarHeight = 72;
    static SettingsWindow? _shared;

    // Segoe Fluent Icons glyphs standing in for Clipy's SF Symbols.
    static readonly (SettingsPane Pane, string Title, string Glyph)[] Panes =
    {
        (SettingsPane.General, "General", "\uE713"),
        (SettingsPane.Menu, "Menu", "\uE8FD"),
        (SettingsPane.Type, "Type", "\uE77F"),
        (SettingsPane.Exclude, "Exclude", "\uE733"),
        (SettingsPane.Shortcuts, "Shortcuts", "\uE765"),
        (SettingsPane.Sync, "Sync", "\uE895"),
        (SettingsPane.Beta, "Beta", "\uEA80"),
        (SettingsPane.About, "About", "\uE946"),
    };

    SettingsPane _pane;
    readonly Grid _layout = new();
    readonly Canvas _toolbar = new() { Height = ToolbarHeight };
    readonly Border _toolbarLine = new() { Height = 1, VerticalAlignment = VerticalAlignment.Top };
    readonly ContentControl _paneHost = new() { Focusable = false };

    public SettingsWindow(SettingsPane pane = SettingsPane.General) : base(minimizable: false, zoomable: false)
    {
        _pane = pane;
        ShowInTaskbar = true;
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ToolbarHeight) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.Children.Add(_toolbar);
        Grid.SetRow(_toolbarLine, 1);
        _layout.Children.Add(_toolbarLine);
        Grid.SetRow(_paneHost, 2);
        _layout.Children.Add(_paneHost);
        _layout.Width = BodyWidth;
        BodyContent = _layout;
        SizeToContent = SizeToContent.WidthAndHeight;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control) Close();
            if (e.Key == Key.Escape) Close();
        };
        Show(pane, initial: true);
    }

    public static void ShowShared(SettingsPane? pane = null)
    {
        if (_shared == null)
        {
            _shared = new SettingsWindow(pane ?? SettingsPane.General);
            _shared.Closed += (_, _) => _shared = null;
            _shared.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _shared.Show();
        }
        else if (pane is SettingsPane p) _shared.Show(p);
        _shared.Activate();
        Native.ForceForeground(new System.Windows.Interop.WindowInteropHelper(_shared).Handle);
    }

    protected override void ApplyTheme()
    {
        base.ApplyTheme();
        Rebuild();
    }

    void Rebuild()
    {
        BuildToolbar();
        _paneHost.Content = BuildPane(_pane);
    }

    public void Show(SettingsPane pane, bool initial = false)
    {
        _pane = pane;
        MacTitle = AppInfo.DisplayName + " Settings – " + Panes.First(p => p.Pane == pane).Title;
        if (!initial) Rebuild();
        else { BuildToolbar(); _paneHost.Content = BuildPane(pane); }
    }

    bool Dark => IsDarkAppearance;

    void BuildToolbar()
    {
        _toolbarLine.Background = Theme.Window.Separator;
        _toolbar.Children.Clear();
        const double itemW = 64, gap = 4;
        double total = Panes.Length * itemW + (Panes.Length - 1) * gap;
        double x = (BodyWidth - total) / 2;
        foreach (var (pane, title, glyph) in Panes)
        {
            bool selected = pane == _pane;
            var item = new Grid { Width = itemW, Height = 60, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var bg = new Border { CornerRadius = new CornerRadius(6), Background = selected ? Theme.Control.Fill : Brushes.Transparent };
            if (selected) bg.BorderBrush = Mac.ElevationBorder();
            bg.BorderThickness = new Thickness(selected ? 1 : 0);
            item.Children.Add(bg);
            var icon = Mac.Icon(glyph, 20, selected ? Theme.Accent : Theme.TextSecondary);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 9, 0, 0);
            item.Children.Add(icon);
            var label = Mac.Text(title, 11, selected ? Theme.TextPrimary : Theme.TextSecondary, selected ? FontWeights.SemiBold : FontWeights.Normal);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Top;
            label.Margin = new Thickness(0, 35, 0, 0);
            item.Children.Add(label);
            if (selected)
                item.Children.Add(new Border { Width = 16, Height = 3, CornerRadius = new CornerRadius(1.5), Background = Theme.Accent, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) });
            item.MouseEnter += (_, _) => { if (!selected) bg.Background = Theme.Control.FillHover; };
            item.MouseLeave += (_, _) => { if (!selected) bg.Background = Brushes.Transparent; };
            item.MouseLeftButtonUp += (_, e) => { e.Handled = true; Show(pane); };
            item.MouseLeftButtonDown += (_, e) => e.Handled = true;
            Canvas.SetLeft(item, x);
            Canvas.SetTop(item, 6);
            _toolbar.Children.Add(item);
            x += itemW + gap;
        }
    }


    // ---------------- Panes ----------------

    static AppSettings S => AppSettings.Shared;

    static void Set(string name, Action<AppSettings> a) => AppSettings.Update(name, a);

    MacCheckBox Check(string title, bool value, string name, Action<AppSettings, bool> set)
    {
        var cb = new MacCheckBox(title, value);
        cb.Changed += v => Set(name, s => set(s, v));
        return cb;
    }

    MacNumberField Number(int value, int min, int max, string name, Action<AppSettings, int> set, string? unit = "items", string? caption = null)
    {
        var f = new MacNumberField(value, min, max, unit, caption);
        f.ValueChanged += v => Set(name, s => set(s, v));
        return f;
    }

    FrameworkElement BuildPane(SettingsPane pane)
    {
        var el = BuildPaneContent(pane);
        // Like AppKit, the first text field becomes first responder when the pane appears.
        el.Loaded += (_, _) =>
        {
            var first = FindFirst<MacNumberField>(el);
            if (first == null) return;
            if (KeyWindowState.ForceActive != null) first.DevShowFocus();
            else Dispatcher.BeginInvoke(first.FocusField, System.Windows.Threading.DispatcherPriority.Input);
        };
        return el;
    }

    static T? FindFirst<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            var r = FindFirst<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    FrameworkElement BuildPaneContent(SettingsPane pane) => pane switch
    {
        SettingsPane.General => General(),
        SettingsPane.Menu => MenuPane(),
        SettingsPane.Type => TypePane(),
        SettingsPane.Exclude => ExcludePane(),
        SettingsPane.Shortcuts => ShortcutsPane(),
        SettingsPane.Sync => new SyncPane(Dark),
        SettingsPane.Beta => BetaPane(),
        _ => AboutPane(),
    };

    FrameworkElement General()
    {
        bool d = Dark;
        var periodic = new MacPopUp(Enum.GetValues<HistoryClearInterval>().Select(v => new PopUpOption(v, IntervalTitle(v))).ToList(), S.HistoryClearInterval);
        periodic.SelectionChanged += v => Set(nameof(S.HistoryClearInterval), s => s.HistoryClearInterval = (HistoryClearInterval)v);
        periodic.IsEnabled = S.ClearsHistoryPeriodically;
        var periodicCheck = Check("Clear History Periodically", S.ClearsHistoryPeriodically, nameof(S.ClearsHistoryPeriodically), (s, v) => s.ClearsHistoryPeriodically = v);
        periodicCheck.Changed += v => periodic.IsEnabled = v;

        var sort = new MacPopUp(new[] { new PopUpOption(false, "Date Created"), new PopUpOption(true, "Last Used") }, S.ReordersClipsAfterPasting);
        sort.SelectionChanged += v => Set(nameof(S.ReordersClipsAfterPasting), s => s.ReordersClipsAfterPasting = (bool)v);

        var iconSize = new Size(18, 18);
        var icon = new MacPopUp(new[]
        {
            new PopUpOption(1, "Standard", Theme.Image("tray"), iconSize),
            new PopUpOption(2, "Reversed", Theme.Image("tray_reversed"), iconSize),
            new PopUpOption(0, "Hidden"),
        }, S.StatusItemDisplayMode);
        icon.SelectionChanged += v => Set(nameof(S.StatusItemDisplayMode), s => s.StatusItemDisplayMode = (int)v);

        var login = Check("Launch on Login", S.LaunchAtLogin, nameof(S.LaunchAtLogin), (s, v) => s.LaunchAtLogin = v);
        login.Changed += LoginItem.Apply;

        var sections = new List<SettingsSection>
        {
            Section("Behavior:", true,
                new SettingsItem(ItemKind.CheckBox, login),
                new SettingsItem(ItemKind.CheckBox, Check("Paste automatically after selection", S.PastesAutomatically, nameof(S.PastesAutomatically), (s, v) => s.PastesAutomatically = v))),
            Section("History limit:", false,
                new SettingsItem(ItemKind.NumberField, Number(S.MaximumHistoryCount, 1, 1000, nameof(S.MaximumHistoryCount), (s, v) => s.MaximumHistoryCount = v))),
            Section("Sort history by:", true, new SettingsItem(ItemKind.PopUp, sort)),
            Section("Automatic History Deletion", true,
                new SettingsItem(ItemKind.CheckBox, Check($"Clear history when {AppInfo.Name} quits", S.ClearsHistoryOnQuit, nameof(S.ClearsHistoryOnQuit), (s, v) => s.ClearsHistoryOnQuit = v)),
                new SettingsItem(ItemKind.CheckBox, periodicCheck),
                new SettingsItem(ItemKind.PopUp, periodic, 28),
                SettingsLayout.Description($"Clears all history at the selected interval while {AppInfo.Name} is running. Snippets are not deleted.", d)),
            Section("Tray icon:", false, new SettingsItem(ItemKind.PopUp, icon)),
        };
        return SettingsLayout.Build(sections, d);
    }

    static string IntervalTitle(HistoryClearInterval v) => v switch
    {
        HistoryClearInterval.FiveMinutes => "Every 5 minutes",
        HistoryClearInterval.ThirtyMinutes => "Every 30 minutes",
        HistoryClearInterval.OneHour => "Every hour",
        HistoryClearInterval.FourHours => "Every 4 hours",
        HistoryClearInterval.EightHours => "Every 8 hours",
        HistoryClearInterval.OneDay => "Every day",
        HistoryClearInterval.ThreeDays => "Every 3 days",
        _ => "Every 7 days",
    };

    static SettingsSection Section(string? title, bool divider, params SettingsItem[] items)
    {
        var s = new SettingsSection { Title = title, BottomDivider = divider };
        s.Items.AddRange(items);
        return s;
    }

    FrameworkElement MenuPane()
    {
        bool d = Dark;
        var replace = Check("Replace the previous entry", S.OverwritesDuplicateHistory, nameof(S.OverwritesDuplicateHistory), (s, v) => s.OverwritesDuplicateHistory = v);
        replace.IsEnabled = S.AllowsDuplicateHistory;
        var dup = Check("Save duplicate clipboard content", S.AllowsDuplicateHistory, nameof(S.AllowsDuplicateHistory), (s, v) => s.AllowsDuplicateHistory = v);
        dup.Changed += v => replace.IsEnabled = v;

        var zero = Check("Start numbering at 0", S.StartsMenuItemTitlesAtZero, nameof(S.StartsMenuItemTitlesAtZero), (s, v) => s.StartsMenuItemTitlesAtZero = v);
        zero.IsEnabled = S.MarksMenuItemsWithNumbers;
        var numbers = Check("Mark menu items with numbers", S.MarksMenuItemsWithNumbers, nameof(S.MarksMenuItemsWithNumbers), (s, v) => s.MarksMenuItemsWithNumbers = v);
        numbers.Changed += v => zero.IsEnabled = v;

        var warn = Check("Show a warning before clearing history", S.ShowsClearHistoryAlert, nameof(S.ShowsClearHistoryAlert), (s, v) => s.ShowsClearHistoryAlert = v);
        warn.IsEnabled = S.ShowsClearHistoryMenuItem;
        var clear = Check("Add a menu item to clear clipboard history", S.ShowsClearHistoryMenuItem, nameof(S.ShowsClearHistoryMenuItem), (s, v) => s.ShowsClearHistoryMenuItem = v);
        clear.Changed += v => warn.IsEnabled = v;

        var tipRow = new StackPanel { Orientation = Orientation.Horizontal };
        var maxLabel = Mac.Text("Maximum:", 13, Mac.Label(d));
        maxLabel.Margin = new Thickness(0, 6, 10, 0);
        maxLabel.VerticalAlignment = VerticalAlignment.Top;
        var tipField = Number(S.MaximumToolTipLength, 1, 10000, nameof(S.MaximumToolTipLength), (s, v) => s.MaximumToolTipLength = v, "characters");
        tipRow.Children.Add(maxLabel);
        tipRow.Children.Add(tipField);
        tipRow.IsEnabled = S.ShowsToolTipsOnMenuItems;
        var tips = Check("Show tooltips on menu items", S.ShowsToolTipsOnMenuItems, nameof(S.ShowsToolTipsOnMenuItems), (s, v) => s.ShowsToolTipsOnMenuItems = v);
        tips.Changed += v => tipRow.IsEnabled = v;
        tipRow.IsEnabledChanged += (_, _) => maxLabel.Foreground = tipRow.IsEnabled ? Mac.Label(d) : Mac.DisabledText(d);

        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
        sizeRow.Children.Add(Number(S.ThumbnailWidth, 1, 512, nameof(S.ThumbnailWidth), (s, v) => s.ThumbnailWidth = v, "px", "Width"));
        var hField = Number(S.ThumbnailHeight, 1, 512, nameof(S.ThumbnailHeight), (s, v) => s.ThumbnailHeight = v, "px", "Height");
        hField.Margin = new Thickness(12, 0, 0, 0);
        sizeRow.Children.Add(hField);
        sizeRow.IsEnabled = S.ShowsImagesInMenu;
        var images = Check("Show images", S.ShowsImagesInMenu, nameof(S.ShowsImagesInMenu), (s, v) => s.ShowsImagesInMenu = v);
        images.Changed += v => sizeRow.IsEnabled = v;

        var sections = new List<SettingsSection>
        {
            Section("History items shown at the top level:", false,
                new SettingsItem(ItemKind.NumberField, Number(S.InlineMenuItemLimit, 0, 100, nameof(S.InlineMenuItemLimit), (s, v) => s.InlineMenuItemLimit = v)),
                SettingsLayout.Description("Additional history is grouped into folders. Set to 0 to group all history into folders.", d)),
            Section("Items per folder:", false,
                new SettingsItem(ItemKind.NumberField, Number(S.FolderMenuItemLimit, 1, 100, nameof(S.FolderMenuItemLimit), (s, v) => s.FolderMenuItemLimit = v))),
            Section("Maximum displayed text length:", true,
                new SettingsItem(ItemKind.NumberField, Number(S.MaximumMenuItemTitleLength, 1, 1000, nameof(S.MaximumMenuItemTitleLength), (s, v) => s.MaximumMenuItemTitleLength = v, "characters"))),
            Section("Clipboard History:", true,
                new SettingsItem(ItemKind.CheckBox, dup),
                new SettingsItem(ItemKind.CheckBox, replace, 28)),
            Section("Menu Items:", true,
                new SettingsItem(ItemKind.CheckBox, Check("Show icons in menu items", S.ShowsIconsInMenu, nameof(S.ShowsIconsInMenu), (s, v) => s.ShowsIconsInMenu = v)),
                new SettingsItem(ItemKind.CheckBox, numbers),
                new SettingsItem(ItemKind.CheckBox, zero, 28),
                new SettingsItem(ItemKind.CheckBox, Check("Use number keys as shortcuts", S.AddsNumericKeyEquivalents, nameof(S.AddsNumericKeyEquivalents), (s, v) => s.AddsNumericKeyEquivalents = v)),
                new SettingsItem(ItemKind.CheckBox, Check("Show snippets above history", S.ShowsSnippetsAboveHistory, nameof(S.ShowsSnippetsAboveHistory), (s, v) => s.ShowsSnippetsAboveHistory = v))),
            Section("Clear History:", true,
                new SettingsItem(ItemKind.CheckBox, clear),
                new SettingsItem(ItemKind.CheckBox, warn, 28)),
            Section("Tooltips:", true,
                new SettingsItem(ItemKind.CheckBox, tips),
                new SettingsItem(ItemKind.NumberField, tipRow, 28)),
            Section("Previews:", false,
                new SettingsItem(ItemKind.CheckBox, images),
                new SettingsItem(ItemKind.NumberField, sizeRow, 28, 52),
                new SettingsItem(ItemKind.CheckBox, Check("Show color code preview", S.ShowsColorPreviewInMenu, nameof(S.ShowsColorPreviewInMenu), (s, v) => s.ShowsColorPreviewInMenu = v))),
        };
        return SettingsLayout.Build(sections, d);
    }

    FrameworkElement TypePane()
    {
        bool d = Dark;
        MacCheckBox T(string title, string key) => Check(title, S.IsTypeEnabled(key), nameof(S.PasteboardTypes), (s, v) => s.PasteboardTypes[key] = v);
        var sections = new List<SettingsSection>
        {
            Section("Save to History:", true,
                new SettingsItem(ItemKind.CheckBox, T("Plain Text", ClipTypes.String)),
                new SettingsItem(ItemKind.CheckBox, T("Rich Text Format (RTF)", ClipTypes.Rtf)),
                new SettingsItem(ItemKind.CheckBox, T("PDF", ClipTypes.Pdf)),
                new SettingsItem(ItemKind.CheckBox, T("Files", ClipTypes.Filenames)),
                new SettingsItem(ItemKind.CheckBox, T("URL", ClipTypes.Url)),
                new SettingsItem(ItemKind.CheckBox, T("Images", ClipTypes.Image)),
                new SettingsItem(ItemKind.CheckBox, T("HTML", ClipTypes.Html))),
            Section(null, false,
                new SettingsItem(ItemKind.CheckBox, Check("Do not save content marked as confidential to history", S.IgnoresConcealedPasteboardTypes, nameof(S.IgnoresConcealedPasteboardTypes), (s, v) => s.IgnoresConcealedPasteboardTypes = v)),
                SettingsLayout.Description("Some apps, such as password managers, mark sensitive content as confidential.", d)),
        };
        return SettingsLayout.Build(sections, d);
    }

    FrameworkElement ExcludePane() => new ExcludedAppsView(Dark);

    FrameworkElement ShortcutsPane()
    {
        bool d = Dark;
        MacShortcutRecorder R(KeyCombo? combo, HotKeyAction action, bool canClear = true)
        {
            var r = new MacShortcutRecorder(combo, canClear, width: 300);
            r.Changed += c => HotKeyService.Shared.Change(action, c);
            return r;
        }
        var sections = new List<SettingsSection>
        {
            Section("Main Menu:", false, new SettingsItem(ItemKind.Recorder, R(S.MainKeyCombo, HotKeyAction.Main))),
            Section("History:", false, new SettingsItem(ItemKind.Recorder, R(S.HistoryKeyCombo, HotKeyAction.History))),
            Section("Snippets:", true, new SettingsItem(ItemKind.Recorder, R(S.SnippetKeyCombo, HotKeyAction.Snippet))),
            Section("Clear History:", false, new SettingsItem(ItemKind.Recorder, R(S.ClearHistoryKeyCombo, HotKeyAction.ClearHistory))),
            Section("Edit Snippets:", true, new SettingsItem(ItemKind.Recorder, R(S.EditSnippetsKeyCombo, HotKeyAction.EditSnippets))),
            Section("Open Settings", false,
                new SettingsItem(ItemKind.Recorder, R(S.SettingsKeyCombo, HotKeyAction.Settings, canClear: false)),
                SettingsLayout.Description("To keep Settings accessible even if you cannot open the menu, this shortcut can be changed but cannot be removed.", d)),
        };
        return SettingsLayout.Build(sections, d, minTitleWidth: 150);
    }

    FrameworkElement BetaPane()
    {
        bool d = Dark;
        MacPopUp Modifier(int value, string name, Action<AppSettings, int> set, bool enabled)
        {
            var p = new MacPopUp(new[]
            {
                new PopUpOption(0, "Ctrl"), new PopUpOption(1, "Shift"),
                new PopUpOption(2, "Win"), new PopUpOption(3, "Alt"),
            }, value);
            p.SelectionChanged += v => Set(name, s => set(s, (int)v));
            p.IsEnabled = enabled;
            return p;
        }
        var p1 = Modifier(S.PlainTextPasteModifier, nameof(S.PlainTextPasteModifier), (s, v) => s.PlainTextPasteModifier = v, S.PastesPlainTextWithModifier);
        var p2 = Modifier(S.HistoryDeletionModifier, nameof(S.HistoryDeletionModifier), (s, v) => s.HistoryDeletionModifier = v, S.DeletesHistoryWithModifier);
        var p3 = Modifier(S.PasteAndDeleteHistoryModifier, nameof(S.PasteAndDeleteHistoryModifier), (s, v) => s.PasteAndDeleteHistoryModifier = v, S.PastesAndDeletesHistoryWithModifier);
        var c1 = Check("Paste as plain text", S.PastesPlainTextWithModifier, nameof(S.PastesPlainTextWithModifier), (s, v) => s.PastesPlainTextWithModifier = v);
        c1.Changed += v => p1.IsEnabled = v;
        var c2 = Check("Delete from history", S.DeletesHistoryWithModifier, nameof(S.DeletesHistoryWithModifier), (s, v) => s.DeletesHistoryWithModifier = v);
        c2.Changed += v => p2.IsEnabled = v;
        var c3 = Check("Paste and delete from history", S.PastesAndDeletesHistoryWithModifier, nameof(S.PastesAndDeletesHistoryWithModifier), (s, v) => s.PastesAndDeletesHistoryWithModifier = v);
        c3.Changed += v => p3.IsEnabled = v;
        var sections = new List<SettingsSection>
        {
            Section("Modifier Key Actions:", false,
                new SettingsItem(ItemKind.CheckBox, c1), new SettingsItem(ItemKind.PopUp, p1, 28),
                new SettingsItem(ItemKind.CheckBox, c2), new SettingsItem(ItemKind.PopUp, p2, 28),
                new SettingsItem(ItemKind.CheckBox, c3), new SettingsItem(ItemKind.PopUp, p3, 28),
                SettingsLayout.Description("Hold the selected key while choosing a history item to perform the action.", d)),
        };
        var stack = new StackPanel();
        var note = Mac.Text("Beta feature settings may not be preserved after an update.", 13, Mac.Secondary(d));
        note.HorizontalAlignment = HorizontalAlignment.Center;
        note.Margin = new Thickness(0, 20, 0, 0);
        stack.Children.Add(note);
        stack.Children.Add(SettingsLayout.Build(sections, d));
        return stack;
    }

    FrameworkElement AboutPane()
    {
        var stack = new StackPanel { Width = SettingsLayout.ContentWidth, Margin = new Thickness(28, 28, 28, 28) };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new Image { Source = Theme.Image("128"), Width = 64, Height = 64, Margin = new Thickness(0, 0, 16, 0) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        header.Children.Add(icon);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock
        {
            Text = AppInfo.DisplayName,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextPrimary,
        });
        var ver = Mac.Text("Version " + AppInfo.Version, 13, Theme.TextSecondary);
        ver.Margin = new Thickness(0, 2, 0, 0);
        names.Children.Add(ver);
        header.Children.Add(names);
        stack.Children.Add(header);

        var desc = Mac.Text("Clipboard history and snippets for Windows. Everything stays on this PC unless you turn on sync, which encrypts your snippets here before it sends them.", 13, Theme.TextPrimary);
        desc.TextWrapping = TextWrapping.Wrap;
        desc.Margin = new Thickness(0, 20, 0, 0);
        stack.Children.Add(desc);

        var credit = Mac.Text($"{AppInfo.DisplayName} is a Windows port of {AppInfo.UpstreamName} by the Clipy Project and builds on ClipMenu by Naotaka Morimoto. Both are distributed under the MIT License.", 13, Theme.TextSecondary);
        credit.TextWrapping = TextWrapping.Wrap;
        credit.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(credit);

        var buttons = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
        void Add(string title, Action act, bool prominent = false)
        {
            var b = new MacButton(title, prominent) { Margin = new Thickness(0, 0, 8, 8) };
            b.Click += act;
            buttons.Children.Add(b);
        }
        if (AppInfo.HomepageUrl is string home) Add("Website", () => OpenUrl(home), prominent: true);
        Add("Licenses", ShowLicenses);
        Add($"{AppInfo.UpstreamName} on GitHub", () => OpenUrl(AppInfo.UpstreamUrl));
        Add("Open data folder", () => OpenUrl(AppSettings.Directory));
        stack.Children.Add(buttons);
        return stack;
    }

    /// <summary>Show the bundled LICENSE and third-party notices in the default text editor.</summary>
    static void ShowLicenses()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/NOTICES.txt"));
            if (info == null) return;
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), AppInfo.Name + "-Licenses.txt");
            using (var fs = File.Create(path)) info.Stream.CopyTo(fs);
            OpenUrl(path);
        }
        catch { }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
