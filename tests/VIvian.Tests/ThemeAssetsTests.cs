using VIvian.App;
using Xunit;

namespace VIvian.Tests;

public sealed class ThemeAssetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "furina-assets-" + Guid.NewGuid().ToString("N"));
    private string External => Path.Combine(_root, "external");
    private string Bundled => Path.Combine(_root, "bundled");

    public ThemeAssetsTests() => Directory.CreateDirectory(External);

    [Fact]
    public void MissingAssetsKeepVectorFallbackAndUseCurrentBrand()
    {
        var theme = new ThemeAssets(External, Bundled);
        Assert.Equal("furina", theme.Title);
        Assert.Equal("芙宁娜", theme.AssistantName);
        Assert.False(new ArtSlot(theme.Character).HasImage);
    }

    [Fact]
    public void LegacyExternalNamesAndAnimatedAssistantFallbackArePreserved()
    {
        File.WriteAllBytes(Path.Combine(External, "character.gif"), [1]);
        File.WriteAllBytes(Path.Combine(External, "character.png"), [1]);
        File.WriteAllBytes(Path.Combine(External, "banner-starrail.jpg"), [1]);
        var theme = new ThemeAssets(External);
        Assert.Equal(Path.Combine(External, "character.gif"), theme.Character);
        Assert.Equal(theme.Character, theme.Assistant);
        Assert.True(new ArtSlot(theme.Assistant).IsGif);
        Assert.Equal(Path.Combine(External, "banner-starrail.jpg"), theme.BannerStarRail);
    }

    [Fact]
    public void CanonicalLayerNamesCanBeUsedAsExternalOverrides()
    {
        foreach (var name in new[] { "furina-main", "furina-avatar", "scene-background", "game-genshin", "game-hsr", "game-zzz" })
            File.WriteAllBytes(Path.Combine(External, name + ".png"), [1]);
        var theme = new ThemeAssets(External);
        Assert.Equal(Path.Combine(External, "furina-main.png"), theme.Character);
        Assert.Equal(Path.Combine(External, "furina-avatar.png"), theme.Assistant);
        Assert.Equal(Path.Combine(External, "scene-background.png"), theme.Background);
        Assert.Equal(Path.Combine(External, "game-genshin.png"), theme.BannerGenshin);
        Assert.Equal(Path.Combine(External, "game-hsr.png"), theme.BannerStarRail);
        Assert.Equal(Path.Combine(External, "game-zzz.png"), theme.BannerZenless);
    }

    [Fact]
    public void BundledLayersFillMissingSlotsButNeverReplaceExternalArtwork()
    {
        Directory.CreateDirectory(Bundled);
        foreach (var name in new[] { "furina-main", "furina-avatar", "scene-background", "water-foreground", "game-genshin", "game-hsr", "game-zzz" })
            File.WriteAllBytes(Path.Combine(Bundled, name + ".png"), [1]);
        var defaults = new ThemeAssets(External, Bundled);
        Assert.Equal(Path.Combine(Bundled, "furina-main.png"), defaults.Character);
        Assert.Equal(Path.Combine(Bundled, "furina-avatar.png"), defaults.Assistant);
        Assert.Equal(Path.Combine(Bundled, "scene-background.png"), defaults.Background);
        Assert.Equal(Path.Combine(Bundled, "game-hsr.png"), defaults.BannerStarRail);
        File.WriteAllBytes(Path.Combine(External, "character.gif"), [1]);
        File.WriteAllBytes(Path.Combine(External, "background.png"), []);
        var overridden = new ThemeAssets(External, Bundled);
        Assert.Equal(Path.Combine(External, "character.gif"), overridden.Character);
        Assert.Equal(overridden.Character, overridden.Assistant);
        Assert.Equal(defaults.Background, overridden.Background);
        Assert.Equal(defaults.BannerStarRail, overridden.BannerStarRail);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"assistantName\":42,\"title\":false}")]
    [InlineData("{")]
    public void MalformedOrWronglyTypedMetadataDoesNotBreakStartup(string json)
    {
        File.WriteAllText(Path.Combine(External, "theme.json"), json);
        var theme = new ThemeAssets(External);
        Assert.Equal("furina", theme.Title);
        Assert.Equal("芙宁娜", theme.AssistantName);
    }

    [Theory]
    [InlineData("Vivian-cutar", "furina")]
    [InlineData("我的控制台", "我的控制台")]
    public void LegacyBrandIsMigratedInMemoryButCustomMetadataIsPreserved(string title, string expected)
    {
        var path = Path.Combine(External, "theme.json");
        var json = System.Text.Json.JsonSerializer.Serialize(new { title, assistantName = "自定义助手" });
        File.WriteAllText(path, json);
        var theme = new ThemeAssets(External);
        Assert.Equal(expected, theme.Title);
        Assert.Equal("自定义助手", theme.AssistantName);
        Assert.Equal(json, File.ReadAllText(path));
    }

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
