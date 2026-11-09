using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kauri.Core;

namespace Kauri.UI;

public enum MenuType { Main, History, Snippet }

/// <summary>Port of Clipy's MenuManager: builds the status/hot-key menus from history and snippets.</summary>
public sealed class MenuManager
{
    public static MenuManager Shared { get; } = new();
    const int MaxKeyEquivalents = 10;
    MacMenuWindow? _open;

    public Action? ClearHistory { get; set; }
    public Action? EditSnippets { get; set; }
    public Action? OpenSettings { get; set; }
    public Action? Quit { get; set; }

    static AppSettings S => AppSettings.Shared;
    int FirstIndex => S.StartsMenuItemTitlesAtZero ? 0 : 1;

    // ---------------- Popup ----------------

    public void PopUp(MenuType type, Point? at = null, bool anchorBottom = false)
    {
        var folders = Database.Shared.FetchFolders();
        var items = type switch
        {
            MenuType.History => HistoryItems(),
            MenuType.Snippet => SnippetItems(folders),
            _ => MainMenuItems(folders),
        };
        Show(items, at, anchorBottom);
    }

    public void PopUpSnippetFolder(Guid folderId)
    {
        var folder = Database.Shared.FetchFolders().FirstOrDefault(f => f.Id == folderId);
        if (folder == null) return;
        var items = new List<MacMenuItem> { MacMenuItem.Header(folder.Title) };
        int index = FirstIndex;
        foreach (var s in folder.Snippets.Where(s => s.IsEnabled))
            items.Add(SnippetItem(s, index++));
        Show(items, null, false);
    }

    void Show(List<MacMenuItem> items, Point? at, bool anchorBottom)
    {
        _open?.CloseAll();
        // NSMenu with no items never appears (e.g. the snippet hot key with no snippet folders).
        if (items.Count == 0 || items.All(i => i.IsSeparator)) return;
        var fg = Native.GetForegroundWindow();
        PasteService.Shared.TargetWindow = fg;
        var p = at ?? CursorDip();
        var w = new MacMenuWindow(items);
        w.MenuClosed += () => { if (_open == w) _open = null; };
        _open = w;
        // NSMenu.popUpHighlightingFirstItem is used for hot-key menus.
        w.ShowAt(p, anchorBottom, highlightFirst: at == null);
    }

    public static Point CursorDip()
    {
        Native.GetCursorPos(out var pt);
        double scale;
        using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96.0;
        return new Point(pt.X / scale, pt.Y / scale);
    }

    // ---------------- Builders ----------------

    public List<MacMenuItem> MainMenuItems(List<SnippetFolder> folders)
    {
        var menu = new List<MacMenuItem>();
        if (S.ShowsSnippetsAboveHistory)
        {
            var before = menu.Count;
            AddSnippetItems(menu, folders);
            menu.Add(MacMenuItem.Separator());
        }
        AddHistoryItems(menu);
        if (!S.ShowsSnippetsAboveHistory)
        {
            menu.Add(MacMenuItem.Separator());
            AddSnippetItems(menu, folders);
        }
        menu.Add(MacMenuItem.Separator());
        if (S.ShowsClearHistoryMenuItem)
            menu.Add(new MacMenuItem { Title = "Clear History", Glyph = "\uE74D", Action = () => ClearHistory?.Invoke() });
        menu.Add(new MacMenuItem { Title = "Edit Snippets…", Glyph = "\uE70F", Action = () => EditSnippets?.Invoke() });
        menu.Add(new MacMenuItem { Title = "Settings…", Glyph = "\uE713", KeyEquivalent = ",", Action = () => OpenSettings?.Invoke() });
        menu.Add(MacMenuItem.Separator());
        menu.Add(new MacMenuItem { Title = "Quit " + AppInfo.Name, Glyph = "\uE7E8", Action = () => Quit?.Invoke() });
        return menu;
    }

    public List<MacMenuItem> HistoryItems()
    {
        var menu = new List<MacMenuItem>();
        AddHistoryItems(menu);
        return menu;
    }

    public List<MacMenuItem> SnippetItems(List<SnippetFolder> folders)
    {
        var menu = new List<MacMenuItem>();
        AddSnippetItems(menu, folders);
        return menu;
    }

    static MacMenuItem FolderItem(string title)
    {
        var item = new MacMenuItem { Title = title, Submenu = new List<MacMenuItem>() };
        if (S.ShowsIconsInMenu)
        {
            item.Glyph = "\uE8B7"; // Folder
        }
        return item;
    }

    void AddHistoryItems(List<MacMenuItem> menu)
    {
        int placeInLine = S.InlineMenuItemLimit;
        int placeInsideFolder = Math.Max(1, S.FolderMenuItemLimit);
        menu.Add(MacMenuItem.Header("History"));

        int firstIndex = FirstIndex;
        int listNumber = firstIndex;
        int subMenuCount = placeInLine;
        var details = Database.Shared.FetchHistoryDetails(!S.ReordersClipsAfterPasting, S.ShowsImagesInMenu || S.ShowsColorPreviewInMenu, S.MaximumHistoryCount);
        int total = details.Count;
        MacMenuItem? currentFolder = null;
        for (int i = 0; i < total; i++)
        {
            var d = details[i];
            if (placeInLine < 1 || placeInLine - 1 < i)
            {
                if (i == subMenuCount)
                {
                    int first = firstIndex + subMenuCount;
                    int count = Math.Min(placeInsideFolder, total - subMenuCount);
                    currentFolder = FolderItem($"{first} - {first + count - 1}");
                    currentFolder.Range = (first, first + count - 1);
                    menu.Add(currentFolder);
                    listNumber = firstIndex;
                }
                currentFolder?.Submenu!.Add(ClipItem(d, i, listNumber));
                listNumber++;
            }
            else
            {
                menu.Add(ClipItem(d, i, listNumber));
                listNumber++;
            }
            if (i + 1 == subMenuCount + placeInsideFolder) subMenuCount += placeInsideFolder;
        }
    }

    MacMenuItem ClipItem(HistoryDetail detail, int index, int listNumber)
    {
        var h = detail.History;
        string? key = null;
        if (S.AddsNumericKeyEquivalents && index < MaxKeyEquivalents)
        {
            int n = S.StartsMenuItemTitlesAtZero ? index : index + 1;
            if (n == MaxKeyEquivalents) n = 0;
            key = n.ToString();
        }
        var item = new MacMenuItem
        {
            Title = h.TypedTitle,
            Number = S.MarksMenuItemsWithNumbers ? listNumber : null,
            KeyEquivalent = key,
            ToolTip = S.ShowsToolTipsOnMenuItems ? TextUtil.Prefix(h.Title, S.MaximumToolTipLength) : null,
            Action = () => PasteService.Shared.PasteHistory(h.Id),
        };
        if (detail.Thumbnail is { } t)
        {
            if (t.Kind == "image" && S.ShowsImagesInMenu)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = new MemoryStream(t.Data);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    item.Image = bmp;
                    item.ImageSize = AspectFit(bmp.PixelWidth, bmp.PixelHeight, S.ThumbnailWidth, S.ThumbnailHeight);
                }
                catch { }
            }
            else if (t.Kind == "colorCode" && S.ShowsColorPreviewInMenu && ColorCode.TryParse(Encoding.UTF8.GetString(t.Data), out var c))
            {
                item.Swatch = c;
                item.ImageSize = AspectFit(20, 20, S.ThumbnailWidth, S.ThumbnailHeight);
            }
        }
        return item;
    }

    /// <summary>Port of NSImage.aspectFitImage: scales to fit inside the box, preserving aspect ratio.</summary>
    static Size AspectFit(double w, double h, double maxW, double maxH)
    {
        if (w <= 0 || h <= 0) return new Size(maxW, maxH);
        double r = Math.Min(maxW / w, maxH / h);
        return new Size(Math.Round(w * r), Math.Round(h * r));
    }

    void AddSnippetItems(List<MacMenuItem> menu, List<SnippetFolder> folders)
    {
        if (folders.Count == 0) return;
        menu.Add(MacMenuItem.Header("Snippet"));
        int firstIndex = FirstIndex;
        foreach (var f in folders.Where(f => f.IsEnabled))
        {
            var folderItem = FolderItem(f.Title);
            menu.Add(folderItem);
            int i = firstIndex;
            foreach (var s in f.Snippets.Where(s => s.IsEnabled))
                folderItem.Submenu!.Add(SnippetItem(s, i++));
        }
    }

    MacMenuItem SnippetItem(Snippet s, int listNumber)
    {
        var item = new MacMenuItem
        {
            Title = TextUtil.TrimmedMenuTitle(s.Title),
            Number = S.MarksMenuItemsWithNumbers ? listNumber : null,
            ToolTip = S.ShowsToolTipsOnMenuItems ? TextUtil.Prefix(s.Content, S.MaximumToolTipLength) : null,
            Action = () => PasteService.Shared.PasteText(s.Content),
        };
        if (S.ShowsIconsInMenu)
        {
            item.Glyph = "\uE8A5"; // Document
        }
        return item;
    }
}
