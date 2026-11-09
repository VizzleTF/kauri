using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Kauri.UI;

public enum ItemKind { CheckBox, PopUp, NumberField, Description, Text, Recorder, Custom }

public sealed record SettingsItem(ItemKind Kind, FrameworkElement Element, double Indent = 0, double? VisualHeight = null);

public sealed class SettingsSection
{
    public string? Title { get; init; }
    public bool BottomDivider { get; init; }
    public bool CenterTitle { get; init; }
    public List<SettingsItem> Items { get; } = new();
}

/// <summary>
/// Clipy's settings grid (titles on the left, controls on the right, dividers between groups),
/// spaced for Windows 11 control sizes.
/// </summary>
public static class SettingsLayout
{
    public const double ContentWidth = 560;
    const double DividerGap = 16;
    const double SectionGap = 10;
    const double ColumnGap = 16;
    const double TitleLine = 20;

    public static double VisualHeight(SettingsItem i) => i.VisualHeight ?? i.Kind switch
    {
        ItemKind.CheckBox => 20,
        ItemKind.PopUp => 32,
        ItemKind.NumberField => 32,
        ItemKind.Description => 16,
        ItemKind.Text => 20,
        ItemKind.Recorder => 36,
        _ => i.Element.DesiredSize.Height,
    };

    static double Between(ItemKind a, ItemKind b) => (a, b) switch
    {
        (_, ItemKind.Description) => 4,
        (ItemKind.Description, _) => 10,
        (ItemKind.CheckBox, ItemKind.CheckBox) => 8,
        _ => 8,
    };

    /// <summary>Align the title's text line with the first control's text.</summary>
    static double TitleOffset(SettingsItem i) => (VisualHeight(i) is var h && i.Kind is ItemKind.PopUp or ItemKind.NumberField or ItemKind.Recorder)
        ? (h - TitleLine) / 2
        : 0;

    public static FrameworkElement Build(IReadOnlyList<SettingsSection> sections, bool dark, double contentWidth = ContentWidth, double minTitleWidth = 0)
    {
        double titleW = minTitleWidth;
        foreach (var s in sections)
        {
            if (s.Title == null) continue;
            var t = Mac.Text(s.Title);
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            titleW = Math.Max(titleW, Math.Min(Math.Ceiling(t.DesiredSize.Width) + 1, contentWidth * 0.45));
        }
        double contentX = titleW + ColumnGap;
        double avail = contentWidth - contentX;

        var canvas = new Canvas { Width = contentWidth };
        double y = 0;
        ItemKind? prevLast = null;
        bool prevDivider = false;
        foreach (var s in sections)
        {
            if (s.Items.Count == 0) continue;
            var first = s.Items[0];
            if (prevLast != null)
            {
                if (prevDivider)
                {
                    y += DividerGap;
                    var d = new MacDivider(dark) { Width = contentWidth };
                    Canvas.SetTop(d, y);
                    canvas.Children.Add(d);
                    y += 1 + DividerGap;
                }
                else y += SectionGap;
            }

            if (s.Title != null)
            {
                var t = Mac.Text(s.Title, 13, Theme.TextPrimary);
                t.TextWrapping = TextWrapping.Wrap;
                t.MaxWidth = titleW;
                t.TextAlignment = TextAlignment.Right;
                t.LineHeight = TitleLine;
                t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                t.Measure(new Size(titleW, double.PositiveInfinity));
                Canvas.SetLeft(t, titleW - t.DesiredSize.Width);
                Canvas.SetTop(t, y + TitleOffset(first));
                canvas.Children.Add(t);
            }

            ItemKind? prev = null;
            foreach (var item in s.Items)
            {
                if (prev != null) y += Between(prev.Value, item.Kind);
                double w = avail - item.Indent;
                Canvas.SetLeft(item.Element, contentX + item.Indent);
                Canvas.SetTop(item.Element, y);
                if (item.Element is MacCheckBox cb) cb.SetWrapWidth(w);
                if (item.Kind == ItemKind.Description) item.Element.Width = Math.Max(10, w);
                canvas.Children.Add(item.Element);
                item.Element.Measure(new Size(w, double.PositiveInfinity));
                y += Math.Max(VisualHeight(item), item.Element.DesiredSize.Height);
                prev = item.Kind;
            }
            prevLast = prev;
            prevDivider = s.BottomDivider;
        }
        canvas.Height = y;
        return new Border { Child = canvas, Padding = new Thickness(28, 24, 28, 28) };
    }

    public static SettingsItem Description(string text, bool dark) =>
        new(ItemKind.Description, new TextBlock
        {
            Text = text,
            FontFamily = Theme.Font,
            FontSize = 12,
            Foreground = Theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        });
}
