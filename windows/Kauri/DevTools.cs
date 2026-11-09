using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kauri.Core;
using Kauri.UI;

namespace Kauri;

/// <summary>
/// Developer tooling: renders screens to 2x PNGs so they can be diffed against captures of
/// Clipy for Mac. Usage: Kauri.exe --render &lt;target&gt; --theme dark|light --active true|false --out file.png
/// Data comes from KAURI_DATA_DIR (seeded with the same fixtures used on the Mac).
/// </summary>
public static class DevTools
{
    public static void Run(string[] args)
    {
        string Arg(string name, string def)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
        }
        var target = args.Length > 1 ? args[1] : "menu";
        Theme.ForcedDark = Arg("--theme", "dark") == "dark";
        KeyWindowState.ForceActive = Arg("--active", "true") == "true";
        var outPath = Arg("--out", "render.png");

        AppSettings.Load();
        Database.Open();
        Seed();

        if (target == "selftest") { SelfTest(outPath); return; }
        if (target == "appicon") { AppIcon(outPath); return; }

        FrameworkElement visual;
        Size size;
        Window? host = null;
        switch (target)
        {
            case "menu":
            case "menu-hl":
            case "sub-history":
            case "sub-snippet":
                (visual, size) = MenuScene(target);
                break;
            case var t when t.StartsWith("settings-"):
            {
                var pane = Enum.Parse<SettingsPane>(t["settings-".Length..], true);
                var w = new SettingsWindow(pane) { ShowActivated = false, Left = -20000, Top = -20000 };
                host = w;
                w.Show();
                w.UpdateLayout();
                visual = (FrameworkElement)w.Content;
                size = new Size(visual.ActualWidth, visual.ActualHeight);
                break;
            }
            case "editor-folder":
            case "editor-snippet":
            {
                Theme.ForcedDark = false;
                var w = new SnippetsEditorWindow { ShowActivated = false, Left = -20000, Top = -20000 };
                host = w;
                w.Show();
                w.UpdateLayout();
                if (target == "editor-snippet") w.DevSelectFirstSnippet();
                w.UpdateLayout();
                visual = (FrameworkElement)w.Content;
                size = new Size(visual.ActualWidth, visual.ActualHeight);
                break;
            }
            case "alert-login":
            {
                var a = new MacAlert($"Launch {AppInfo.Name} on system startup?", "You can change this later in Settings.",
                    new[] { "Launch on system startup", "Don't Launch" }, true) { ShowActivated = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
                host = a;
                a.Show();
                a.UpdateLayout();
                visual = a.Body;
                size = new Size(visual.ActualWidth, visual.ActualHeight);
                break;
            }
            default:
                throw new ArgumentException("unknown target " + target);
        }
        Save(visual, size, outPath);
        host?.Close();
    }

    /// <summary>Drives the real windows (menus, submenu, settings, editor) and snapshots each one.</summary>
    static void SelfTest(string outPrefix)
    {
        var log = new List<string>();
        void Snap(Window w, string name)
        {
            w.UpdateLayout();
            var c = (FrameworkElement)w.Content;
            Save(c, new Size(c.ActualWidth, c.ActualHeight), $"{outPrefix}-{name}.png");
            log.Add($"{name}: {w.GetType().Name} {w.ActualWidth:0}x{w.ActualHeight:0} at {w.Left:0},{w.Top:0} active={w.IsActive}");
        }
        void Pump(int ms)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
            t.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        KeyWindowState.ForceActive = null;
        MenuManager.Shared.PopUp(MenuType.Main, new Point(200, 200));
        Pump(400);
        var menus = Application.Current.Windows.OfType<MacMenuWindow>().ToList();
        log.Add($"menus open: {menus.Count}");
        if (menus.Count > 0)
        {
            var m = menus[0];
            Snap(m, "menu");
            // Keyboard: Down, Down, Right opens the "11 - 20" submenu.
            foreach (var k in new[] { System.Windows.Input.Key.Down, System.Windows.Input.Key.Down, System.Windows.Input.Key.Right })
            {
                var src = PresentationSource.FromVisual(m);
                m.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, src!, 0, k) { RoutedEvent = UIElement.PreviewKeyDownEvent });
                Pump(80);
            }
            Pump(300);
            var all = Application.Current.Windows.OfType<MacMenuWindow>().ToList();
            log.Add($"menus after Right: {all.Count}");
            for (int i = 0; i < all.Count; i++) Snap(all[i], "menu" + i);
            m.CloseAll();
            Pump(200);
            log.Add($"menus after close: {Application.Current.Windows.OfType<MacMenuWindow>().Count()}");
        }
        SettingsWindow.ShowShared(SettingsPane.Menu);
        Pump(500);
        var sw = Application.Current.Windows.OfType<SettingsWindow>().First();
        Snap(sw, "settings-menu");
        sw.Show(SettingsPane.Shortcuts);
        Pump(300);
        Snap(sw, "settings-shortcuts");
        sw.Close();
        SnippetsEditorWindow.ShowShared();
        Pump(500);
        var ed = Application.Current.Windows.OfType<SnippetsEditorWindow>().First();
        ed.DevSelectFirstSnippet();
        Pump(200);
        Snap(ed, "editor-snippet");
        ed.Close();
        File.WriteAllLines(outPrefix + "-log.txt", log);
    }

    /// <summary>Kauri app icon: a clipboard whose board carries a kauri tree silhouette (vector, 256×256 design grid).</summary>
    public static Canvas AppIconArt()
    {
        var c = new Canvas { Width = 256, Height = 256 };
        Brush G(string a, string b) => new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(b), 90);
        var shadow = new Border
        {
            Width = 160, Height = 196, CornerRadius = new CornerRadius(26),
            Background = Brushes.Black, Opacity = 0.22,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 14 },
        };
        Canvas.SetLeft(shadow, 48); Canvas.SetTop(shadow, 46);
        c.Children.Add(shadow);
        var board = new Border
        {
            Width = 160, Height = 196, CornerRadius = new CornerRadius(26),
            Background = G("#FFFFFF", "#E7F2EC"),
            BorderBrush = G("#FFFFFF", "#C9DDD2"), BorderThickness = new Thickness(2),
        };
        Canvas.SetLeft(board, 48); Canvas.SetTop(board, 38);
        c.Children.Add(board);
        // Clip
        var clip = new Border { Width = 84, Height = 40, CornerRadius = new CornerRadius(14), Background = G("#2F8F6B", "#145C45") };
        Canvas.SetLeft(clip, 86); Canvas.SetTop(clip, 20);
        c.Children.Add(clip);
        var hole = new Border { Width = 26, Height = 10, CornerRadius = new CornerRadius(5), Background = G("#0F4A37", "#1C6B51") };
        Canvas.SetLeft(hole, 115); Canvas.SetTop(hole, 30);
        c.Children.Add(hole);
        // Kauri: tall straight trunk with a broad, flat-topped crown
        var trunk = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M121,206 L123,132 L133,132 L135,206 Z"),
            Fill = G("#7A5A3C", "#5B4029"),
        };
        c.Children.Add(trunk);
        var crown = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M70,118 C70,96 92,86 106,92 C112,74 144,74 150,92 C164,86 186,96 186,118 C186,136 168,142 156,138 C148,148 108,148 100,138 C88,142 70,136 70,118 Z"),
            Fill = G("#3FAE7F", "#1E7454"),
        };
        c.Children.Add(crown);
        var branches = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M128,150 L108,132 M128,158 L150,134"),
            Stroke = G("#7A5A3C", "#5B4029"), StrokeThickness = 6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        c.Children.Add(branches);
        var ground = new Border { Width = 76, Height = 8, CornerRadius = new CornerRadius(4), Background = G("#9CCFB6", "#7DB89C") };
        Canvas.SetLeft(ground, 90); Canvas.SetTop(ground, 204);
        c.Children.Add(ground);
        return c;
    }

    static void AppIcon(string outDir)
    {
        Directory.CreateDirectory(outDir);
        foreach (var px in new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 })
        {
            var art = AppIconArt();
            var box = new Viewbox { Child = art, Width = px, Height = px };
            box.Measure(new Size(px, px));
            box.Arrange(new Rect(0, 0, px, px));
            box.UpdateLayout();
            var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(box);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(outDir, $"icon_{px}.png"));
            enc.Save(fs);
        }
    }

    static void Save(FrameworkElement visual, Size size, string path)
    {
        if (visual.Parent == null)
        {
            visual.Measure(size);
            visual.Arrange(new Rect(size));
            visual.UpdateLayout();
        }
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var vb = new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, size.Width, size.Height) };
            dc.DrawRectangle(vb, null, new Rect(size));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Lay out menus exactly as the macOS capture (menu window = body + 74.5/76pt shadow margins).</summary>
    static (FrameworkElement, Size) MenuScene(string target)
    {
        var folders = Database.Shared.FetchFolders();
        var main = MenuManager.Shared.MainMenuItems(folders);
        var canvas = new Canvas();
        const double ox = 74.5, oy = 76;
        var mainView = new MacMenuView(main);
        var mainHost = MacMenuWindow.BuildHost(mainView);
        Canvas.SetLeft(mainHost, ox - MacMenuWindow.ShadowPad);
        Canvas.SetTop(mainHost, oy - MacMenuWindow.ShadowPad);
        canvas.Children.Add(mainHost);
        double w = mainView.MenuWidth + ox * 2, h = mainView.ContentHeight + oy * 2;
        int openIndex = target switch { "sub-history" => 2, "sub-snippet" => 6, _ => -1 };
        if (target == "menu-hl") mainView.SetSelected(main.FindIndex(i => i.Title == "Clear History"));
        if (openIndex >= 0)
        {
            mainView.SetSelected(openIndex, true);
            var sub = new MacMenuView(main[openIndex].Submenu!, isSubmenu: true);
            var subHost = MacMenuWindow.BuildHost(sub);
            double sx = ox + mainView.MenuWidth - 4, sy = oy + mainView.RowTop(openIndex) - MacMenuView.Padding;
            Canvas.SetLeft(subHost, sx - MacMenuWindow.ShadowPad);
            Canvas.SetTop(subHost, sy - MacMenuWindow.ShadowPad);
            canvas.Children.Add(subHost);
            w = Math.Max(w, sx + sub.MenuWidth + ox);
            h = Math.Max(h, sy + sub.ContentHeight + oy);
        }
        canvas.Width = w;
        canvas.Height = h;
        return (canvas, new Size(w, h));
    }

    /// <summary>Same fixtures as the reference instance on the Mac (scratch seed.sql).</summary>
    static void Seed()
    {
        if (Database.Shared.FetchHistoryDetails(true, false, 1).Count > 0) return;
        string[] items =
        {
            "Hello, world!", "The quick brown fox jumps over the lazy dog", "https://example.com/docs/getting-started", "#FF6600",
            "Lorem ipsum dolor sit amet, consectetur", "git status", "123456", "Meeting notes for Monday",
            "npm install --save-dev typescript", "Sample text 10", "Short", "A longer line that goes beyond twenty characters for sure",
            "foo bar baz", "rgb(0, 122, 255)", "Line one\nLine two\nLine three", "   leading spaces", "SELECT * FROM users;",
            "Пример текста на русском", "日本語のテキスト", "Sample text 20", "Sample text 21", "Sample text 22", "Sample text 23",
            "Sample text 24", "Sample text 25",
        };
        long t = 1762600000;
        for (int i = 0; i < items.Length; i++)
            Database.Shared.SaveHistory(Guid.NewGuid().ToString().ToUpperInvariant(), items[i], new[] { ClipTypes.String },
                new[] { new ClipAsset(ClipTypes.String, System.Text.Encoding.UTF8.GetBytes(items[i])) }, null, t - i * 60);
        var g = Database.Shared.InsertFolder("Greetings");
        Database.Shared.InsertSnippet(g.Id, "Hello", "Hello! How are you?");
        Database.Shared.InsertSnippet(g.Id, "Thanks", "Thank you very much!");
        var sig = Database.Shared.InsertSnippet(g.Id, "Signature", "Best regards,\nJohn Appleseed");
        sig.IsEnabled = false;
        Database.Shared.UpdateSnippet(sig);
        var c = Database.Shared.InsertFolder("Code");
        Database.Shared.InsertSnippet(c.Id, "console.log", "console.log();");
        Database.Shared.InsertSnippet(c.Id, "for loop", "for (let i = 0; i < n; i++) {\n}");
        var d = Database.Shared.InsertFolder("Disabled folder");
        d.IsEnabled = false;
        Database.Shared.UpdateFolder(d);
        Database.Shared.InsertSnippet(d.Id, "Hidden", "hidden");
        AppSettings.Update("seed", s =>
        {
            foreach (var k in new[] { ClipTypes.String, ClipTypes.Rtf, ClipTypes.Rtfd, ClipTypes.Html, ClipTypes.Pdf, ClipTypes.Filenames, ClipTypes.Url, ClipTypes.Image })
                s.PasteboardTypes[k] = false;
            s.SuppressLoginItemAlert = true;
        });
    }
}
