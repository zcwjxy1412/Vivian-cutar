using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VIvian.App;

// Vector-only decorative chrome. Reading surfaces remain opaque and independently auditable.
public class FurinaPanel : Border
{
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 20 || ActualHeight < 20) return;
        var r = new Rect(3, 3, ActualWidth - 6, ActualHeight - 6);
        dc.DrawRoundedRectangle(null, new Pen(Brushes.White, 2), r, 16, 16);
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(137, 170, 245)), 1),
            new Rect(6, 6, ActualWidth - 12, ActualHeight - 12), 13, 13);
        var shine = new LinearGradientBrush(Color.FromArgb(170, 255, 255, 255), Colors.Transparent, 90);
        dc.DrawRoundedRectangle(shine, null, new Rect(8, 7, ActualWidth - 16, Math.Min(14, ActualHeight - 14)), 10, 10);
        var gold = new Pen(new SolidColorBrush(Color.FromRgb(215, 188, 121)), 1.2);
        dc.DrawLine(gold, new Point(18, 2), new Point(48, 2));
        dc.DrawLine(gold, new Point(2, 18), new Point(2, 45));
        dc.DrawLine(gold, new Point(ActualWidth - 18, ActualHeight - 2), new Point(ActualWidth - 48, ActualHeight - 2));
    }
}

public class FurinaPlaque : Border
{
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 24 || ActualHeight < 8) return;
        double w = ActualWidth, h = ActualHeight;
        var outline = new StreamGeometry();
        using (var g = outline.Open())
        {
            g.BeginFigure(new Point(12, 1), true, true);
            g.PolyLineTo([new(w - 14, 1), new(w - 7, h * .25), new(w - 1, h * .5),
                new(w - 7, h * .75), new(w - 14, h - 1), new(12, h - 1), new(1, h * .5)], true, false);
        }
        dc.DrawGeometry(Background, new Pen(BorderBrush, 1.4), outline);
        var ice = new Pen(new SolidColorBrush(Color.FromRgb(151, 181, 255)), .7);
        dc.DrawLine(ice, new Point(16, 4), new Point(w - 18, 4));
        dc.DrawLine(ice, new Point(16, h - 4), new Point(w - 18, h - 4));
        foreach (double x in new[] { 9.0, w - 10 })
        {
            dc.DrawLine(new Pen(BorderBrush, 1), new Point(x - 2, h / 2), new Point(x + 2, h / 2));
            dc.DrawLine(new Pen(BorderBrush, 1), new Point(x, h / 2 - 3), new Point(x, h / 2 + 3));
        }
    }
}

public sealed class FurinaOrnament : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(FurinaOrnament),
        new FrameworkPropertyMetadata("Crown", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public FurinaOrnament() { IsHitTestVisible = false; Focusable = false; }
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        dc.PushTransform(new ScaleTransform(ActualWidth / 100, ActualHeight / 100));
        var gold = new Pen(new SolidColorBrush(Color.FromRgb(242, 212, 142)), 2);
        var white = new Pen(new SolidColorBrush(Color.FromRgb(226, 248, 255)), 1.1);
        if (Kind == "Halo")
        {
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(156, 218, 255)), 4), new(50, 53), 43, 42);
            dc.DrawEllipse(null, gold, new(50, 53), 39, 40);
            for (int i = 0; i < 8; i++)
            {
                dc.PushTransform(new RotateTransform(i * 45, 50, 53));
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(170, 100, 194, 255)), white,
                    Geometry.Parse("M 50,8 C 72,0 77,17 68,23 C 78,8 59,12 61,19 C 55,16 52,12 50,8 Z"));
                dc.Pop();
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(41, 83, 195)), gold,
                Geometry.Parse("M 37,16 L 34,6 L 44,11 L 50,1 L 56,11 L 66,6 L 63,16 Z"));
        }
        else
        {
            dc.DrawGeometry(null, gold, Geometry.Parse("M 18,76 C 12,61 6,39 9,31 C 15,28 28,41 30,66 M 30,66 C 21,36 33,17 42,24 C 49,28 43,51 46,66 M 46,66 L 50,11 L 54,66 M 54,66 C 57,51 51,28 58,24 C 67,17 79,36 70,66 M 70,66 C 72,41 85,28 91,31 C 94,39 88,61 82,76 M 18,76 Q 50,66 82,76 L 78,86 Q 50,78 22,86 Z M 29,91 Q 50,87 71,91"));
            foreach (var p in new[] { new Point(9, 28), new Point(35, 20), new Point(50, 8), new Point(65, 20), new Point(91, 28) })
                dc.DrawEllipse(Brushes.White, gold, p, 2.8, 2.8);
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(126, 217, 255)), gold, Geometry.Parse("M 50,35 L 56,47 L 50,59 L 44,47 Z"));
        }
        dc.Pop();
    }
}
