using System.IO;
using System.Text.Json;

namespace VIvian.App;

/// <summary>
/// The user's own artwork, read from a local folder next to the install (default: ..\VIvian-素材). Nothing here is
/// shipped in the repository; when a file is missing VIvian falls back to its original vector guide 小星.
/// Recognised files: character.(gif|png|jpg|webp) · assistant.(gif|png|jpg) · banner-genshin/starrail/zzz.(png|jpg) ·
/// background.(png|jpg) · theme.json { "assistantName": "…", "title": "…" }.
/// </summary>
public sealed class ThemeAssets
{
    public static string DefaultFolder =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "VIvian-素材"));

    public string Folder { get; }
    public string? Character { get; }
    public string? Assistant { get; }
    public string? Background { get; }
    public string? BannerGenshin { get; }
    public string? BannerStarRail { get; }
    public string? BannerZenless { get; }
    public string AssistantName { get; } = "小星";
    public string Title { get; } = "Vivian-cutar";

    public ThemeAssets(string? folder = null)
    {
        Folder = folder ?? DefaultFolder;
        Character = Find("character", "gif", "png", "jpg", "jpeg", "webp");
        Assistant = Find("assistant", "gif", "png", "jpg", "jpeg", "webp") ?? Character;
        Background = Find("background", "png", "jpg", "jpeg");
        BannerGenshin = Find("banner-genshin", "png", "jpg", "jpeg", "gif");
        BannerStarRail = Find("banner-starrail", "png", "jpg", "jpeg", "gif");
        BannerZenless = Find("banner-zzz", "png", "jpg", "jpeg", "gif");
        try
        {
            var json = Path.Combine(Folder, "theme.json");
            if (File.Exists(json) && new FileInfo(json).Length < 16_000)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                if (doc.RootElement.TryGetProperty("assistantName", out var n) && n.GetString() is { Length: > 0 and <= 16 } name) AssistantName = name;
                if (doc.RootElement.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 and <= 30 } title) Title = title;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    private string? Find(string stem, params string[] extensions)
    {
        foreach (var ext in extensions)
        {
            var path = Path.Combine(Folder, stem + "." + ext);
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
