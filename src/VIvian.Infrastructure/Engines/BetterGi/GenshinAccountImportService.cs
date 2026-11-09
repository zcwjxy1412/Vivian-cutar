using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;

namespace VIvian.Infrastructure.Engines.BetterGi;

public sealed record GenshinAccountImportResult(bool Success, string Detail, GameAccount? Account = null,
    bool Created = false);

public sealed class GenshinAccountImportService(LocalDatabase database)
{
    public async Task<GenshinAccountImportResult> ImportAndRegisterAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var frame = await WindowsOcrReader.DecodeFileAsync(path, ct).ConfigureAwait(false);
        if (frame is null)
            return new(false, "无法读取原神登录照片，请选择有效的图片。");

        var ocr = await WindowsOcrReader.ReadAsync(frame, ct).ConfigureAwait(false);
        if (!ocr.Available)
            return new(false, "本地 Windows OCR 无法读取这张照片，请检查中文语言包和图片尺寸。");

        var lines = ocr.Lines;
        if (frame.Width < frame.Height && AnalyzeImport(frame.Width, frame.Height, lines).State == GenshinLoginScreenState.Unrecognized)
        {
            var enter = FindUnique(lines.Where(line => Compact(line.Text) == "进入游戏"));
            if (enter is not null && enter.Bottom < frame.Height && enter.Width >= 8 && enter.Height >= 8)
                lines = [.. lines, .. await ReadPhotoCaptionAsync(frame, enter, ct).ConfigureAwait(false)];
        }
        return await RegisterRecognizedAsync(frame.Width, frame.Height, lines, ct).ConfigureAwait(false);
    }

    internal async Task<GenshinAccountImportResult> RegisterRecognizedAsync(int width, int height,
        IReadOnlyList<RecognizedText> lines, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var screen = AnalyzeImport(width, height, lines);
        if (screen.State != GenshinLoginScreenState.SavedAccountCard || screen.Account is not { } account)
            return new(false, screen.State == GenshinLoginScreenState.NeedsUser
                ? "请先在游戏中完成登录验证，再导入显示脱敏账号的照片。"
                : "未识别出唯一的原神脱敏账号，请使用显示完整账号卡片的登录照片。");

        var (saved, created) = await database.RegisterGenshinPhotoAccountAsync(account, ct).ConfigureAwait(false);
        return saved is null
            ? new(false, "无法唯一确定已登记的原神账号，请检查档案。")
            : new(true, created ? "已新建原神档案。已有手工档案在绑定同一脱敏登录标识后才会复用。" : "此原神账号已登记，已返回现有档案。", saved, created);
    }

    private static GenshinLoginScreen AnalyzeImport(int width, int height, IReadOnlyList<RecognizedText> lines)
    {
        var screen = GenshinLoginScreen.Analyze(width, height, lines);
        if (screen.State != GenshinLoginScreenState.Unrecognized || width >= height)
            return screen;

        // shortcut: Windows OCR loses portions of both labels in the photographed portrait card.
        // This correction is confined to file import and still requires the regular layout and one exact mask.
        var enter = FindUnique(lines.Where(line => Compact(line.Text) == "进入游戏"));
        if (enter is null) return screen;

        var brand = FindUnique(lines.Where(line =>
        {
            var text = Compact(line.Text);
            return line.Bottom < enter.Y && (text.Equals("miHoYo", StringComparison.OrdinalIgnoreCase)
                || (text.Length is >= 2 and <= 4 && text.Contains("ho", StringComparison.OrdinalIgnoreCase)));
        }));
        var caption = FindUnique(lines.Where(line =>
        {
            var text = Compact(line.Text);
            return line.Y > enter.Bottom && line.CenterY - enter.CenterY <= 6 * enter.Height
                && Math.Abs(line.CenterX - enter.CenterX) <= enter.Width * .55
                && text.Length is >= 4 and <= 6 && text.Contains("账号", StringComparison.Ordinal)
                && !text.Any(char.IsDigit);
        }));
        if (brand is null || caption is null) return screen;

        return GenshinLoginScreen.Analyze(width, height, lines.Select(line => ReferenceEquals(line, brand)
            ? line with { Text = "miHoYo" }
            : ReferenceEquals(line, caption) ? line with { Text = "登录其他账号" } : line).ToArray());
    }

    private static async Task<IReadOnlyList<RecognizedText>> ReadPhotoCaptionAsync(CapturedFrame frame,
        RecognizedText enter, CancellationToken ct)
    {
        var x = Math.Max(0, (int)(enter.CenterX - enter.Width * 1.6));
        var y = (int)enter.Bottom;
        var crop = frame.Crop(x, y, Math.Min(frame.Width - x, (int)(enter.Width * 3.2)),
            Math.Min(frame.Height - y, (int)(enter.Height * 6)));
        for (var i = 0; i < crop.Bgra.Length; i += 4)
        {
            var gray = (crop.Bgra[i] + 2 * crop.Bgra[i + 1] + crop.Bgra[i + 2]) / 4;
            var value = (byte)Math.Clamp((gray - 50) * 255 / 205, 0, 255);
            crop.Bgra[i] = crop.Bgra[i + 1] = crop.Bgra[i + 2] = value;
        }
        var factor = Math.Clamp(2048 / Math.Max(crop.Width, crop.Height), 1, 3);
        var ocr = await WindowsOcrReader.ReadAsync(crop.Scale(factor), ct).ConfigureAwait(false);
        return ocr.Lines.Select(line => new RecognizedText(line.Text, line.X / factor + x,
            line.Y / factor + y, line.Width / factor, line.Height / factor)).ToArray();
    }

    private static RecognizedText? FindUnique(IEnumerable<RecognizedText> lines)
    {
        var matches = lines.Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string Compact(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
}
