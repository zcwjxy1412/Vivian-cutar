using System.Reflection;
using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class GenshinMenuRecognitionTests
{
    [Theory]
    [InlineData(720)]
    [InlineData(1080)]
    [InlineData(1440)]
    [InlineData(2160)]
    public void MultipleMenuEntriesAreSupportingEvidenceRatherThanAmbiguousButtons(int height)
    {
        var scale = height / 1080d;
        var area = new ScreenRegion(0, 190 * scale, 650 * scale, 790 * scale);
        var lines = new[] { "背包", "图鉴", "好友" }.Select((text, index) =>
            new RecognizedText(text, 100 * scale, (250 + index * 200) * scale, 60 * scale, 30 * scale)).ToArray();
        Assert.True(GenshinWindowsLoginDesktop.HasMenuContent(lines, area));
        Assert.False(GenshinWindowsLoginDesktop.HasMenuContent([new("其他", 100, 250, 60, 30)], area));
        Assert.False(GenshinWindowsLoginDesktop.HasMenuContent([new("背包", 1600 * scale, 250 * scale, 60, 30)], area));
    }

    [Fact]
    public async Task SubmittedMenuScreenshotIsRecognizedWithoutDesktopInput()
    {
        var screenshot = Environment.GetEnvironmentVariable("VIVIAN_MENU_SCREENSHOT");
        if (screenshot is null) return; // Private user image stays outside the repository.
        var root = Environment.GetEnvironmentVariable("VIVIAN_BETTERGI_ROOT")!;
        var frame = (await WindowsOcrReader.DecodeFileAsync(screenshot, default))!;
        var lines = await WindowsOcrReader.ReadAsync(frame, default);
        Assert.True(lines.Available);
        var templates = await GenshinLoginTemplates.LoadAsync(root, default);
        var desktop = new GenshinWindowsLoginDesktop("", "", templates, false);
        var window = new StarRailWindowInfo((IntPtr)1, 42, "", frame.Width, frame.Height, 0, 0, DateTimeOffset.UtcNow);
        var method = typeof(GenshinWindowsLoginDesktop).GetMethod("ClassifyAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = await (Task<GenshinDesktopFrame>)method.Invoke(desktop, [window, frame, lines.Lines, CancellationToken.None])!;
        Assert.Equal(GenshinView.Menu, result.View);
        Assert.NotNull(result.Main);
    }
}
