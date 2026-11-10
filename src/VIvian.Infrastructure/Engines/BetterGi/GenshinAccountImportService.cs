using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;

namespace VIvian.Infrastructure.Engines.BetterGi;

public sealed record GenshinAccountImportResult(bool Success, string Detail, GameAccount? Account = null,
    bool Created = false);

public sealed class GenshinAccountImportService(LocalDatabase database)
{
    public Task<GenshinAccountImportResult> ImportAndRegisterAsync(string path, CancellationToken ct = default) =>
        ImportAsync(path, null, ct);

    public Task<GenshinAccountImportResult> BindExistingAsync(string path, Guid accountId, CancellationToken ct = default) =>
        ImportAsync(path, accountId, ct);

    private async Task<GenshinAccountImportResult> ImportAsync(string path, Guid? accountId, CancellationToken ct)
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
        return await RegisterRecognizedAsync(frame.Width, frame.Height, lines, ct, accountId).ConfigureAwait(false);
    }

    internal async Task<GenshinAccountImportResult> RegisterRecognizedAsync(int width, int height,
        IReadOnlyList<RecognizedText> lines, CancellationToken ct, Guid? accountId = null)
    {
        ct.ThrowIfCancellationRequested();
        var screen = AnalyzeImport(width, height, lines);
        if (screen.State == GenshinLoginScreenState.NeedsUser)
            return new(false, "请先在游戏中完成登录验证，再导入显示脱敏账号的截图。");

        var accounts = screen.Account is { } account ? [account] : GenshinLoginScreen.AnalyzeExpandedList(width, height, lines);
        if (accounts.Count == 0)
            return new(false, "未能核对原神脱敏账号，请使用清晰的账号卡片或完整的保存账号列表。");
        if (accountId.HasValue && accounts.Count != 1)
            return new(false, "绑定已有档案时，请收起列表并截取要绑定的单个账号卡片。");

        var registered = await database.RegisterGenshinPhotoAccountsAsync(accounts, accountId, ct).ConfigureAwait(false);
        var saved = registered.FirstOrDefault().Account;
        if (accountId.HasValue)
            return saved is null
                ? new(false, "无法绑定所选档案。请确认它是已启用的独立原神档案，且双方的登录标识没有冲突。")
                : new(true, "已核对所选原神档案的登录绑定，原有任务和设置已保留。", saved);
        if (saved is null) return new(false, "无法唯一确定已登记的原神账号，请检查档案。");
        var created = registered.Count(row => row.Created);
        return new(true, registered.Count > 1
            ? $"已识别 {registered.Count} 个原神账号，新建 {created} 个，复用 {registered.Count - created} 个。"
            : created == 1 ? "已新建原神档案。" : "此原神账号已登记，已返回现有档案。", saved, created > 0);
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
