using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Kauri.Core;

namespace Kauri.UI;

/// <summary>Port of ExcludedApplicationsSettingsView: inset list + [+|−] control group.</summary>
public sealed class ExcludedAppsView : StackPanel
{
    readonly bool _dark;
    readonly StackPanel _rows = new();
    readonly Grid _listHost = new() { Height = 320 };
    string? _selected;
    readonly Border _minus;
    readonly Path _minusGlyph;

    public ExcludedAppsView(bool dark)
    {
        _dark = dark;
        Width = SettingsLayout.ContentWidth;
        Margin = new Thickness(28, 24, 28, 28);
        var list = new Border
        {
            Height = 320,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BorderBrush = Theme.Window.CardBorder,
            Background = Theme.Window.Card,
            Child = _listHost,
        };
        _listHost.Children.Add(new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10, 5, 10, 5) });
        Children.Add(list);

        var group = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = Mac.ControlFill(dark),
            BorderBrush = Mac.ElevationBorder(),
            BorderThickness = new Thickness(1),
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var g = new StackPanel { Orientation = Orientation.Horizontal };
        var plus = Segment("M5,0.5 V9.5 M0.5,5 H9.5", out _);
        plus.MouseLeftButtonUp += (_, e) => { e.Handled = true; AddApps(); };
        _minus = Segment("M0.5,5 H9.5", out _minusGlyph);
        _minus.MouseLeftButtonUp += (_, e) => { e.Handled = true; RemoveSelection(); };
        g.Children.Add(plus);
        g.Children.Add(new Border { Width = 0.5, Height = 13, Background = dark ? Theme.Gray(0xFF, 0.2) : Theme.Gray(0x00, 0.15), VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(_minus);
        group.Child = g;
        Children.Add(group);
        Focusable = true;
        KeyDown += (_, e) => { if (e.Key is Key.Delete or Key.Back) RemoveSelection(); };
        Refresh();
    }

    Border Segment(string glyph, out Path path)
    {
        path = new Path
        {
            Data = Geometry.Parse(glyph),
            Stroke = Mac.Label(_dark),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border { Width = 36, Child = path, Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
    }

    void Refresh()
    {
        _rows.Children.Clear();
        while (_listHost.Children.Count > 1) _listHost.Children.RemoveAt(1);
        var apps = AppSettings.Shared.ExcludedApplications;
        foreach (var a in apps) _rows.Children.Add(Row(a));
        _minusGlyph.Stroke = _selected != null && apps.Any(a => a.Identifier == _selected) ? Mac.Label(_dark) : Mac.DisabledText(_dark);
        if (apps.Count == 0)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            var dashed = new Rectangle
            {
                Width = 22, Height = 22, RadiusX = 5.5, RadiusY = 5.5,
                Stroke = Mac.Secondary(_dark), StrokeThickness = 1.6,
                StrokeDashArray = new DoubleCollection { 2.2, 1.9 }, StrokeDashCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            empty.Children.Add(dashed);
            var h = Mac.Text("No Excluded Apps", 13, Mac.Secondary(_dark), FontWeights.Bold);
            h.HorizontalAlignment = HorizontalAlignment.Center;
            h.Margin = new Thickness(0, 9, 0, 0);
            empty.Children.Add(h);
            var d = Mac.Text("Add an app to stop saving its clipboard history.", 11, Mac.Secondary(_dark));
            d.HorizontalAlignment = HorizontalAlignment.Center;
            d.Margin = new Thickness(0, 8, 0, 0);
            empty.Children.Add(d);
            _listHost.Children.Add(empty);
        }
    }

    FrameworkElement Row(ExcludedApplication app)
    {
        bool sel = app.Identifier == _selected;
        var row = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = sel ? Theme.Menu.Hover : Brushes.Transparent,
            Padding = new Thickness(6, 4, 6, 4),
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new Image { Width = 28, Height = 28, Source = IconFor(app.Identifier) };
        sp.Children.Add(icon);
        var texts = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Mac.Text(app.Name, 13, Mac.Label(_dark)));
        var id = Mac.Text(app.Identifier, 11, Mac.Secondary(_dark));
        id.Margin = new Thickness(0, 4, 0, 0);
        texts.Children.Add(id);
        sp.Children.Add(texts);
        row.Child = sp;
        row.MouseLeftButtonDown += (_, e) => { _selected = app.Identifier; Focus(); Refresh(); e.Handled = true; };
        return row;
    }

    static ImageSource? IconFor(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            return Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        catch { return null; }
    }

    void AddApps()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Applications (*.exe)|*.exe",
            Multiselect = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        AppSettings.Update(nameof(AppSettings.ExcludedApplications), s =>
        {
            foreach (var f in dlg.FileNames)
            {
                if (s.ExcludedApplications.Any(a => string.Equals(a.Identifier, f, StringComparison.OrdinalIgnoreCase))) continue;
                string name;
                try { name = FileVersionInfo.GetVersionInfo(f).FileDescription is { Length: > 0 } d ? d : System.IO.Path.GetFileNameWithoutExtension(f); }
                catch { name = System.IO.Path.GetFileNameWithoutExtension(f); }
                s.ExcludedApplications.Add(new ExcludedApplication(f, name));
            }
        });
        _selected = null;
        Refresh();
    }

    void RemoveSelection()
    {
        if (_selected == null) return;
        AppSettings.Update(nameof(AppSettings.ExcludedApplications), s => s.ExcludedApplications.RemoveAll(a => a.Identifier == _selected));
        _selected = null;
        Refresh();
    }
}
