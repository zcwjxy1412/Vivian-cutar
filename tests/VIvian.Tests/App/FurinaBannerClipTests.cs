using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VIvian.App;
using Xunit;

namespace VIvian.Tests.App;

public sealed class FurinaBannerClipTests
{
    [Fact]
    public void ImageAndGradientAreClippedAtAllCornersAfterResizeAndDpiChange()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var invalidIcon = Path.GetTempFileName();
                try
                {
                    File.WriteAllText(invalidIcon, "not an image");
                    Assert.Null(FurinaAppIcon.Load(invalidIcon));
                    File.Delete(invalidIcon);
                    Assert.Null(FurinaAppIcon.Load(invalidIcon));
                }
                finally { File.Delete(invalidIcon); }
                var layers = new Grid();
                layers.Children.Add(new Image { Source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 255, 0, 0, 255 }, 4), Stretch = Stretch.Fill });
                layers.Children.Add(new Border { Background = new LinearGradientBrush(Colors.Red, Colors.Blue, 0) });
                var banner = new FurinaRoundedClipBorder { CornerRadius = new CornerRadius(12), Child = layers };
                foreach (var size in new[] { new Size(156, 68), new Size(240, 90), new Size(156, 68) })
                foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                {
                    VisualTreeHelper.SetRootDpi(banner, new DpiScale(scale, scale));
                    banner.Measure(size); banner.Arrange(new Rect(size)); banner.UpdateLayout();
                    var w = (int)(size.Width * scale); var h = (int)(size.Height * scale);
                    var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(banner);
                    var pixels = new byte[w * h * 4]; bitmap.CopyPixels(pixels, w * 4, 0);
                    foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) })
                        Assert.Equal(0, pixels[(y * w + x) * 4 + 3]);
                    Assert.Equal(255, pixels[((h / 2) * w + w / 2) * 4 + 3]);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
