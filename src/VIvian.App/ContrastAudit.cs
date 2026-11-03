using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VIvian.App;

/// <summary>
/// Smoke-test check: every visible, enabled text element must meet WCAG 2.1 AA contrast (4.5:1) against the nearest
/// opaque solid background behind it. Disabled controls are exempt, as in WCAG. Text over non-solid backgrounds is
/// not checked and is counted separately.
/// </summary>
public static class ContrastAudit
{
    public const double MinimumRatio = 4.5;

    public sealed record Finding(string Text, double Ratio, Color Foreground, Color Background);

    public static (IReadOnlyList<Finding> Failures, int Checked, int Skipped) Check(DependencyObject root)
    {
        var failures = new List<Finding>();
        int checkedCount = 0, skipped = 0;
        foreach (var block in Descendants(root).OfType<TextBlock>())
        {
            if (string.IsNullOrWhiteSpace(block.Text) || !block.IsVisible || !block.IsEnabled || block.ActualWidth < 1) continue;
            if (block.Foreground is not SolidColorBrush { Color.A: > 200 } fg || BackgroundBehind(block) is not { } bg)
            {
                skipped++;
                continue;
            }
            checkedCount++;
            var ratio = Ratio(fg.Color, bg);
            if (ratio < MinimumRatio) failures.Add(new Finding(block.Text, ratio, fg.Color, bg));
        }
        return (failures, checkedCount, skipped);
    }

    public static double Ratio(Color a, Color b)
    {
        var (l1, l2) = (Luminance(a), Luminance(b));
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>Nearest opaque background in the visual tree; null when it is a gradient or image (unknown).</summary>
    private static Color? BackgroundBehind(DependencyObject node)
    {
        for (var current = VisualTreeHelper.GetParent(node); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            var brush = current switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                Control control => control.Background,
                _ => null,
            };
            switch (brush)
            {
                case null:
                    continue;
                case SolidColorBrush { Color.A: > 200 } solid when solid.Opacity > 0.8:
                    return solid.Color;
                case SolidColorBrush:
                    continue; // (semi-)transparent: look further back
                default:
                    return null;
            }
        }
        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }
}
