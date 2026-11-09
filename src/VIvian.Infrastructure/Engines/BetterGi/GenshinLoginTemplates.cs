using System.Security.Cryptography;
using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines;

namespace VIvian.Infrastructure.Engines.BetterGi;

internal sealed class GenshinLoginTemplates
{
    // Only visual templates from the already installed BetterGI scripts repository are used.
    // Reference: bettergi-scripts-list@16737383d1e770e3f0f7c2b4186ac875c1770c70, 切换账号Dropdown 1.1.
    private static readonly (string File, string Sha256)[] Assets =
    [
        ("caret.png", "4346667F129315CCC10036D11F88E21EB1E0210F930A4A981AEB425DBAA17C47"),
        ("logout.png", "C207C48A8FA9C3A9CA19EDC00BEDF589EAF86F554FD0CD3D6541BB8CC72CFCC7"),
        ("paimon_cancel.png", "13EE76939D386A1CF8A55553807B68301947B74279F3AD819433F89AFC148B1E"),
        ("paimon_menu.png", "D58EBCFE3D0B27ADBA3285FE3DAAC5FEA0D15E1AB220EB81015F8E83D8424773"),
        ("quit.png", "CA67DC40CA0D3893135B42F1AB1016293EE6CE642F90139FFFF64EE1B6EA4DD8"),
    ];

    private readonly Dictionary<string, CapturedFrame> _images = new(StringComparer.Ordinal);

    public static async Task<GenshinLoginTemplates> LoadAsync(string root, CancellationToken ct)
    {
        var templates = new GenshinLoginTemplates();
        foreach (var (file, hash) in Assets)
        {
            var path = Path.Combine(root, "Repos", "bettergi-scripts-list-git", "repo", "js", "切换账号Dropdown", "assets", file);
            if (ReadOnlyFileAccess.FindReparsePoint(path) is not null)
                throw new InvalidDataException("原神登录识别素材路径包含链接。");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 128 * 1024 || Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) != hash)
                throw new InvalidDataException("原神登录识别素材与已核对的版本不同。");
            if (!ReadOnlyFileAccess.HandlePathMatches(stream.SafeFileHandle, path))
                throw new InvalidDataException("原神登录识别素材实际路径异常。");
            templates._images[file] = await WindowsOcrReader.DecodeFileAsync(path, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException("原神登录识别素材无法解码。");
        }
        return templates;
    }

    public ScreenRegion? Find(CapturedFrame frame, string file, ScreenRegion region, CancellationToken ct) =>
        Match(frame, _images[file], region, ct);

    internal static ScreenRegion? Match(CapturedFrame frame, CapturedFrame image, ScreenRegion region, CancellationToken ct)
    {
        var scale = frame.Height / 1080d;
        var w = (int)Math.Round(image.Width * scale);
        var h = (int)Math.Round(image.Height * scale);
        var samples = new List<(int X, int Y, double Value)>();
        for (var y = 0; y < h; y += 3)
        for (var x = 0; x < w; x += 3)
        {
            var ix = Math.Min((int)(x / scale), image.Width - 1);
            var iy = Math.Min((int)(y / scale), image.Height - 1);
            if (image.Bgra[(iy * image.Width + ix) * 4 + 3] >= 240)
                samples.Add((x, y, Gray(image, ix, iy)));
        }
        if (samples.Count < 8) return null;
        var mean = samples.Average(s => s.Value);
        var centered = samples.Select(s => (s.X, s.Y, Value: s.Value - mean)).ToArray();
        var templatePower = centered.Sum(s => s.Value * s.Value);
        if (templatePower < samples.Count * 36) return null;

        const double threshold = .92;
        var matches = new List<(int X, int Y, double Score)>();
        var maxX = Math.Min(frame.Width, (int)region.Right) - w;
        var maxY = Math.Min(frame.Height, (int)region.Bottom) - h;
        for (var y = Math.Max(0, (int)region.Y); y <= maxY; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = Math.Max(0, (int)region.X); x <= maxX; x++)
            {
                double sum = 0, sumSquare = 0, product = 0;
                foreach (var s in centered)
                {
                    var gray = Gray(frame, x + s.X, y + s.Y);
                    sum += gray;
                    sumSquare += gray * gray;
                    product += gray * s.Value;
                }
                var power = sumSquare - sum * sum / centered.Length;
                if (power < centered.Length * 36 || product <= 0) continue;
                var score = product / Math.Sqrt(power * templatePower);
                if (score >= threshold) matches.Add((x, y, score));
            }
        }
        if (matches.Count == 0) return null;
        var best = matches.MaxBy(m => m.Score);
        if (matches.Any(m => Math.Abs(m.X - best.X) >= w / 2 || Math.Abs(m.Y - best.Y) >= h / 2)) return null;
        return new(best.X, best.Y, w, h);
    }

    private static int Gray(CapturedFrame frame, int x, int y)
    {
        var i = (y * frame.Width + x) * 4;
        return (frame.Bgra[i] * 29 + frame.Bgra[i + 1] * 150 + frame.Bgra[i + 2] * 77) >> 8;
    }
}
