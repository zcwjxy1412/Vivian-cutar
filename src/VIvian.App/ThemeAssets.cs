using System.IO;
using System.Text.Json;

namespace VIvian.App;

/// <summary>
/// External artwork (default: ..\VIvian-素材) overrides bundled Assets/Furina layers.
/// Legacy names and GIF precedence remain supported; missing files retain the vector fallback.
/// Loading never creates or rewrites the user's directory or metadata.
/// </summary>
public sealed class ThemeAssets
{
    public static string DefaultFolder =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "VIvian-素材"));

    public string Folder { get; }
    public string? Character { get; }
    public string? Assistant { get; }
    public string? Background { get; }
    public string? WaterForeground { get; }
    public string? WaterFrame { get; }
    public string? WaterCorner { get; }
    public string? BannerGenshin { get; }
    public string? BannerStarRail { get; }
    public string? BannerZenless { get; }
    public string AssistantName { get; } = "芙宁娜";
    public string Title { get; } = "furina";

    public ThemeAssets(string? folder = null) : this(folder, Path.Combine(AppContext.BaseDirectory, "Assets", "Furina")) { }

    internal ThemeAssets(string? folder, string bundledFolder)
    {
        Folder = Path.GetFullPath(folder ?? DefaultFolder);
        var externalCharacter = Find(Folder, "character", "gif", "png", "jpg", "jpeg", "webp")
            ?? Find(Folder, "furina-main", "png");
        Character = externalCharacter ?? Find(bundledFolder, "furina-main", "png");
        Assistant = Find(Folder, "assistant", "gif", "png", "jpg", "jpeg", "webp")
            ?? Find(Folder, "furina-avatar", "png") ?? externalCharacter
            ?? Find(bundledFolder, "furina-avatar", "png") ?? Character;
        Background = Layer("background", "scene-background", "png", "jpg", "jpeg");
        WaterForeground = Layer("water-foreground", "water-foreground", "png");
        WaterFrame = Layer("water-frame", "water-frame", "png");
        WaterCorner = Layer("water-corner", "water-corner", "png");
        BannerGenshin = Layer("banner-genshin", "game-genshin", "png", "jpg", "jpeg", "gif");
        BannerStarRail = Layer("banner-starrail", "game-hsr", "png", "jpg", "jpeg", "gif");
        BannerZenless = Layer("banner-zzz", "game-zzz", "png", "jpg", "jpeg", "gif");

        string? Layer(string legacy, string canonical, params string[] extensions) =>
            Find(Folder, legacy, extensions) ?? Find(Folder, canonical, "png") ?? Find(bundledFolder, canonical, "png");
        try
        {
            var json = Path.Combine(Folder, "theme.json");
            if (File.Exists(json) && new FileInfo(json).Length < 16_000)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
                if (doc.RootElement.TryGetProperty("assistantName", out var n) && n.ValueKind == JsonValueKind.String
                    && n.GetString() is { Length: > 0 and <= 16 } name && !string.IsNullOrWhiteSpace(name)) AssistantName = name;
                if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    && t.GetString() is { Length: > 0 and <= 30 } title && !string.IsNullOrWhiteSpace(title))
                    Title = title.Equals("Vivian-cutar", StringComparison.OrdinalIgnoreCase) ? "furina" : title;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    private static string? Find(string folder, string stem, params string[] extensions)
    {
        foreach (var ext in extensions)
        {
            var path = Path.GetFullPath(Path.Combine(folder, stem + "." + ext));
            try { if (File.Exists(path) && new FileInfo(path).Length is > 0 and < 60_000_000) return path; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}

/// <summary>An image slot: animated when the file is a GIF, static otherwise, absent when there is no file.</summary>
public sealed record ArtSlot(string? Path)
{
    public bool HasImage => Path is not null;
    public bool IsGif => Path?.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsStatic => HasImage && !IsGif;
    public Uri? Uri => Path is null ? null : new Uri(Path, UriKind.Absolute);
    public System.Windows.Visibility GifVisibility => IsGif ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility StaticVisibility => IsStatic ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility FallbackVisibility => HasImage ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
}
