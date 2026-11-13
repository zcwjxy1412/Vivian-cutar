using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VIvian.App;

// Vector-only decorative chrome. The declared solid background is the darkest glass stop,
// so text contrast auditing stays conservative even though the painted surface is lighter.
public class FurinaPanel : Border
{
    public static readonly DependencyProperty CornerArtProperty = DependencyProperty.Register(nameof(CornerArt), typeof(ImageSource), typeof(FurinaPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public ImageSource? CornerArt { get => (ImageSource?)GetValue(CornerArtProperty); set => SetValue(CornerArtProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 20 || ActualHeight < 20) return;
        var r = new Rect(3, 3, ActualWidth - 6, ActualHeight - 6);
        var glass = new LinearGradientBrush();
        glass.StartPoint = new Point(0, 0); glass.EndPoint = new Point(.25, 1);
        glass.GradientStops.Add(new(Colors.White, 0));
        glass.GradientStops.Add(new(Color.FromRgb(231, 238, 255), .25));
        glass.GradientStops.Add(new(Color.FromRgb(215, 233, 255), 1));
        dc.DrawRoundedRectangle(glass, new Pen(Brushes.White, 2), r, 16, 16);
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(130, 168, 235)), 1.5),
            new Rect(6, 6, ActualWidth - 12, ActualHeight - 12), 13, 13);
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 2),
            new Rect(8, 8, ActualWidth - 16, ActualHeight - 16), 12, 12);
        var gold = new Pen(new SolidColorBrush(Color.FromRgb(215, 188, 121)), 1.2);
        dc.DrawLine(gold, new Point(18, 2), new Point(48, 2));
        dc.DrawLine(gold, new Point(2, 18), new Point(2, 45));
        dc.DrawLine(gold, new Point(ActualWidth - 18, ActualHeight - 2), new Point(ActualWidth - 48, ActualHeight - 2));
        // Gold fleuron stays in the top bevel, clear of reading content.
        dc.DrawEllipse(Brushes.White, gold, new Point(ActualWidth - 23, 5), 3, 2);
        dc.DrawLine(gold, new Point(ActualWidth - 36, 5), new Point(ActualWidth - 30, 5));
        if (CornerArt is not null && ActualWidth >= 140)
            dc.DrawImage(CornerArt, new Rect(ActualWidth - 74, -16, 68, 68));
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
    private static readonly Geometry LeftWave = FreezeShape("M 43,94 C 13,95 3,79 9,65 C 12,58 6,56 4,62 C 1,48 18,48 14,63 C 10,75 21,80 27,78 C 5,58 12,42 19,35 C 26,26 19,19 15,24 C 22,9 35,20 27,32 C 17,48 23,66 34,72 C 19,53 26,42 29,41 C 26,59 35,71 43,76 Z");
    private static readonly Geometry RightWave = FreezeShape("M 57,95 C 82,94 99,83 96,72 C 94,64 85,67 88,73 C 91,69 94,75 88,79 C 93,62 89,52 83,47 C 74,38 82,32 87,37 C 82,25 71,31 74,42 C 79,59 77,70 67,77 C 87,59 92,26 78,16 C 72,12 69,16 73,20 C 75,15 82,20 80,26 C 77,44 65,54 57,66 Z");
    private static readonly Geometry Foam = FreezeShape("M 41,91 C 15,89 9,77 13,66 M 25,75 C 13,56 17,41 23,33 M 59,91 C 78,88 89,84 92,76 M 68,76 C 85,62 87,44 82,33 M 27,27 C 29,20 24,17 21,21");
    private static readonly Geometry WaterDrop = FreezeShape("M 0,-4 C -1,-1 -3,1 -2,3 Q 0,6 2,3 C 3,1 1,-1 0,-4 Z");
    private static readonly Geometry Surf = FreezeShape("M 0,82 C 13,100 19,35 35,49 C 43,57 36,73 31,66 C 37,70 38,60 33,59 C 25,58 28,84 41,83 C 59,84 61,22 78,32 C 86,38 80,58 75,49 C 81,53 81,41 76,42 C 68,45 72,79 87,73 Q 94,70 100,79 L 100,100 L 0,100 Z");
    private static readonly Geometry Shape1 = FreezeShape("M 37,16 L 34,6 L 44,11 L 50,1 L 56,11 L 66,6 L 63,16 Z");
    private static readonly Geometry Shape2 = FreezeShape("M 18,76 C 12,61 6,39 9,31 C 15,28 28,41 30,66 M 30,66 C 21,36 33,17 42,24 C 49,28 43,51 46,66 M 46,66 L 50,11 L 54,66 M 54,66 C 57,51 51,28 58,24 C 67,17 79,36 70,66 M 70,66 C 72,41 85,28 91,31 C 94,39 88,61 82,76 M 18,76 Q 50,66 82,76 L 78,86 Q 50,78 22,86 Z M 29,91 Q 50,87 71,91");
    private static readonly Geometry Shape3 = FreezeShape("M 50,35 L 56,47 L 50,59 L 44,47 Z");
    private static Geometry FreezeShape(string data) { var shape = Geometry.Parse(data); shape.Freeze(); return shape; }

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(FurinaOrnament),
        new FrameworkPropertyMetadata("Crown", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public static readonly DependencyProperty WaterImageProperty = DependencyProperty.Register(nameof(WaterImage), typeof(ImageSource), typeof(FurinaOrnament),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public ImageSource? WaterImage { get => (ImageSource?)GetValue(WaterImageProperty); set => SetValue(WaterImageProperty, value); }
    public FurinaOrnament() { IsHitTestVisible = false; Focusable = false; }
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (Kind == "Halo" && WaterImage is not null)
        {
            dc.DrawImage(WaterImage, new Rect(0, 0, ActualWidth, ActualHeight));
            return;
        }
        dc.PushTransform(new ScaleTransform(ActualWidth / 100, ActualHeight / 100));
        var gold = new Pen(new SolidColorBrush(Color.FromRgb(242, 212, 142)), 2);
        var white = new Pen(new SolidColorBrush(Color.FromRgb(226, 248, 255)), 1.1);
        if (Kind == "Halo")
        {
            var water = new LinearGradientBrush(Color.FromRgb(216, 251, 255), Color.FromRgb(52, 137, 230), 45);
            dc.DrawEllipse(null, gold, new(50, 53), 40, 41);
            dc.DrawGeometry(water, white, LeftWave);
            dc.DrawGeometry(water, white, RightWave);
            dc.DrawGeometry(null, new Pen(Brushes.White, 1.4), Foam);
            foreach (var p in new[] { new Point(8, 39), new Point(12, 26), new Point(91, 53), new Point(94, 34) })
            {
                dc.PushTransform(new TranslateTransform(p.X, p.Y));
                dc.DrawGeometry(water, white, WaterDrop);
                dc.Pop();
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(41, 83, 195)), gold,
                Shape1);
        }
        else if (Kind == "Surf")
        {
            dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(160, 217, 249, 255), Color.FromArgb(60, 67, 149, 240), 90),
                new Pen(new SolidColorBrush(Color.FromArgb(200, 230, 250, 255)), .8), Surf);
        }
        else
        {
            dc.DrawGeometry(null, gold, Shape2);
            foreach (var p in new[] { new Point(9, 28), new Point(35, 20), new Point(50, 8), new Point(65, 20), new Point(91, 28) })
                dc.DrawEllipse(Brushes.White, gold, p, 2.8, 2.8);
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(126, 217, 255)), gold, Shape3);
        }
        dc.Pop();
    }
}
