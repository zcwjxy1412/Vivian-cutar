using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines.BetterGi;
using Xunit;

namespace VIvian.Tests.Engines.BetterGi;

public sealed class GenshinResinInventoryReaderTests
{
    [Fact]
    public async Task LocalCapturedResourceBarReads143OriginalAndZeroCondensed()
    {
        var path = Environment.GetEnvironmentVariable("VIVIAN_RESIN_FIXTURE");
        if (string.IsNullOrEmpty(path)) return;
        var bar = (await WindowsOcrReader.DecodeFileAsync(path, default))!;
        var frame = new CapturedFrame(new byte[bar.Width * (bar.Width * 9 / 16) * 4], bar.Width,
            bar.Width * 9 / 16, "resource-bar replay");
        Buffer.BlockCopy(bar.Bgra, 0, frame.Bgra, 0, bar.Bgra.Length);
        var root = @"C:\Program Files\BetterGI";
        var icon = (await WindowsOcrReader.DecodeFileAsync(Path.Combine(root,
            "GameTask", "QuickTeleport", "Assets", "1920x1080", "MapSettingsButton.png"), default))!.Scale(2);
        for (var row = 0; row < icon.Height; row++)
            Buffer.BlockCopy(icon.Bgra, row * icon.Stride, frame.Bgra, (2004 + row) * frame.Stride + 80 * 4, icon.Stride);
        var reader = await GenshinResinInventoryReader.LoadAsync(root, default);
        Assert.True(reader.IsMap(frame, default));
        Assert.Equal((143, 0), await reader.ReadAsync(frame, default));
    }

    private static readonly CapturedFrame MapSettings = Icon(33, 32, 1);
    private static readonly CapturedFrame Original = Icon(10, 10, 2);
    private static readonly CapturedFrame Condensed = Icon(13, 24, 3);
    private static readonly GenshinResinInventoryReader Reader = new(MapSettings, Original, Condensed);

    [Theory]
    [InlineData("000", 0)]
    [InlineData("2 2 2", 2)]
    [InlineData("001", null)]
    [InlineData("00", null)]
    [InlineData("multi", null)]
    [InlineData("unavailable", null)]
    [InlineData("OOO", null)]
    [InlineData("666", null)]
    [InlineData("", null)]
    public async Task RepeatedDigitFallbackRequiresThreeIdenticalValidDigits(string repeated, int? expected)
    {
        var reader = new GenshinResinInventoryReader(MapSettings, Original, Icon(13, 24, 99), Condensed);
        var calls = 0;
        var result = await reader.ReadAsync(MapFrame(), (_, _) => Task.FromResult(++calls switch
        {
            1 => new OcrReadResult(true, null, [Line("143/200")]),
            2 => new OcrReadResult(true, null, []),
            _ when repeated == "multi" => new OcrReadResult(true, null, [Line("000"), Line("000")]),
            _ when repeated == "unavailable" => new OcrReadResult(false, "offline", [Line("000")]),
            _ => new OcrReadResult(true, null, [Line(repeated)]),
        }), default);
        Assert.Equal(3, calls);
        Assert.Equal(expected is { } value ? (143, value) : ((int, int)?)null, result);
    }

    [Theory]
    [InlineData(1, "215 / 200", "4", 215, 4)]
    [InlineData(2, "0/200", "5", 0, 5)]
    [InlineData(1, "199/200", "0", 199, 0)]
    public async Task ReadsBothCountersNextToTheirIconsInAMapFrame(int scale, string originalText, string condensedText,
        int expectedOriginal, int expectedCondensed)
    {
        var counts = await Reader.ReadAsync(MapFrame(scale), ReadCounts(originalText, condensedText), default);

        Assert.Equal((expectedOriginal, expectedCondensed), counts);
    }

    [Theory]
    [InlineData("162/180", "2")]
    [InlineData("1627200", "2")]
    [InlineData("ID 162/200", "2")]
    [InlineData("16 2/200", "2")]
    [InlineData("162/200", "12")]
    [InlineData("162/200", "6")]
    [InlineData("162/200", "")]
    [InlineData("162/200", "x2")]
    public async Task RejectsAmbiguousCounterText(string originalText, string condensedText)
    {
        Assert.Null(await Reader.ReadAsync(MapFrame(), ReadCounts(originalText, condensedText), default));
    }

    [Theory]
    [InlineData(0, 1480, false)]
    [InlineData(40, null, false)]
    [InlineData(40, 1320, false)]
    [InlineData(40, 1480, true)]
    public async Task RequiresMapContextAndOneCorrectlyPlacedMatchForEachCounter(int mapX, int? condensedX,
        bool duplicateOriginal)
    {
        var frame = MapFrame(mapX: mapX, condensedX: condensedX, duplicateOriginal: duplicateOriginal);

        Assert.Null(await Reader.ReadAsync(frame, ReadCounts("162/200", "2"), default));
    }

    [Fact]
    public async Task RejectsMoreThanOneOcrLineForACounter()
    {
        var lines = new[] { Line("162/200"), Line("161/200") };

        Assert.Null(await Reader.ReadAsync(MapFrame(), (_, _) => Task.FromResult(new OcrReadResult(true, null, lines)), default));
    }

    [Fact]
    public async Task RejectsUnavailableOcrEvenIfItReturnsText()
    {
        var result = new OcrReadResult(false, "offline", [Line("162/200")]);

        Assert.Null(await Reader.ReadAsync(MapFrame(), (_, _) => Task.FromResult(result), default));
    }

    private static Func<CapturedFrame, CancellationToken, Task<OcrReadResult>> ReadCounts(string original, string condensed) =>
        (crop, _) => Task.FromResult(new OcrReadResult(true, null, [Line(crop.Width > 150 ? original : condensed)]));

    private static RecognizedText Line(string text) => new(text, 0, 0, 20, 10);

    private static CapturedFrame MapFrame(int scale = 1, int mapX = 40, int? condensedX = 1480,
        bool duplicateOriginal = false)
    {
        var width = 1920 * scale;
        var height = 1080 * scale;
        var frame = new CapturedFrame(new byte[width * height * 4], width, height, "synthetic map");
        Place(MapSettings, mapX, 1002);
        Place(Original, 1610, 40);
        if (duplicateOriginal) Place(Original, 1700, 40);
        if (condensedX.HasValue) Place(Condensed, condensedX.Value, 38);
        return frame;

        void Place(CapturedFrame icon, int x, int y)
        {
            var scaled = icon.Scale(scale);
            for (var row = 0; row < scaled.Height; row++)
                Buffer.BlockCopy(scaled.Bgra, row * scaled.Stride, frame.Bgra,
                    (y * scale + row) * frame.Stride + x * scale * 4, scaled.Stride);
        }
    }

    private static CapturedFrame Icon(int width, int height, int seed)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)random.Next(30, 256);
            pixels[i + 3] = 255;
        }
        return new(pixels, width, height, "synthetic icon");
    }
}
