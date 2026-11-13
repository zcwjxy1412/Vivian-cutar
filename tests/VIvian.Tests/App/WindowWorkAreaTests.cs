using System.Windows;
using VIvian.App;
using Xunit;

namespace VIvian.Tests.App;

public sealed class WindowWorkAreaTests
{
    [Theory]
    [InlineData(1)] [InlineData(1.25)] [InlineData(1.5)] [InlineData(2)]
    public void NegativeMonitorOriginRemainsPhysicalAndFittedBoundsAreIdempotent(double scale)
    {
        var work = new Rect(-2560, -240, 2560, 1400);
        var initial = new Rect(-2800, -400, 460 * scale, 720 * scale);
        var fitted = WindowWorkArea.Fit(initial, work, new Size(360, 460), scale);
        Assert.Equal(-2560, fitted.X); Assert.Equal(-240, fitted.Y);
        Assert.True(work.Contains(fitted));
        Assert.Equal(fitted, WindowWorkArea.Fit(fitted, work, new Size(360, 460), scale));
    }

    [Fact]
    public void RemovedMonitorAndSmallerWorkAreaRecoverWithoutPreservingAnOffscreenOrigin()
    {
        var work = new Rect(0, 0, 1920, 1040);
        var fitted = WindowWorkArea.Fit(new Rect(-2400, 100, 3200, 1800), work, new Size(960, 500), 2);
        Assert.Equal(work, fitted);
    }

    [Fact]
    public void TinyWorkAreaWinsOverNominalMinimumAndKeepsTopAndCloseReachable()
    {
        var work = new Rect(1920, 160, 640, 400);
        var fitted = WindowWorkArea.Fit(new Rect(2100, 300, 920, 1440), work, new Size(360, 460), 2);
        Assert.Equal(work, fitted);
    }

    [Fact]
    public void MovingBetweenDpiScalesChangesSizeNotScreenOriginUnits()
    {
        var work = new Rect(1920, -400, 2560, 1400);
        var fitted = WindowWorkArea.Fit(new Rect(2020, -300, 460, 720), work, new Size(360, 460), 1.5);
        Assert.Equal(2020, fitted.Left); Assert.Equal(-300, fitted.Top);
        Assert.Equal(540, fitted.Width); Assert.Equal(720, fitted.Height);
    }

    [Theory]
    [InlineData(-1920)] [InlineData(1920)]
    public void OwnerPlacementFitsSelectedMonitorBeforeNoOpComparison(double origin)
    {
        var work = new Rect(origin, 0, 1920, 1040);
        var owner = new Rect(origin, 0, 500, 700);
        var fromOtherMonitor = new Rect(-origin, 80, 460, 720);
        var fit = WindowWorkArea.FitNearOwner(fromOtherMonitor, owner, work, new Size(360, 460), 1);
        Assert.True(work.Contains(fit));
        Assert.NotEqual(fromOtherMonitor, fit);
        // If the current rect already equals this final result, skipping SetWindowPos is correct.
        Assert.Equal(fit, WindowWorkArea.FitNearOwner(fit, owner, work, new Size(360, 460), 1));
    }
}
