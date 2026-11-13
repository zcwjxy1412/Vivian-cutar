using VIvian.Core;
using VIvian.Core.Accounts;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Infrastructure.Processes;

namespace VIvian.Infrastructure.Engines.OneDragon;

public static class ZenlessAccountImport
{
    private const string ScreenshotHint = "请使用国服绝区零完整游戏截图，画面须显示「进入游戏」账号卡片或展开的保存账号列表。";

    public static async Task<IReadOnlyList<string>> ReadScreenshotAsync(string root, string path, CancellationToken ct = default)
    {
        var frame = await WindowsOcrReader.DecodeFileAsync(path, ct).ConfigureAwait(false);
        if (frame is null || !IsGameFrame(frame.Width, frame.Height))
            throw new InvalidOperationException(ScreenshotHint);
        var dropdown = ScaleBox(frame.Width, frame.Height, 1197, 470, 65, 65);
        var closed = await ZenlessLoginDriver.LoadTemplateAsync(root, "account_dropdown_expand", ct).ConfigureAwait(false);
        var expanded = await ZenlessLoginDriver.LoadTemplateAsync(root, "account_dropdown_collapse", ct).ConfigureAwait(false);
        var hasClosed = GenshinLoginTemplates.Match(frame, closed, dropdown, ct) is not null;
        var hasExpanded = GenshinLoginTemplates.Match(frame, expanded, dropdown, ct) is not null;
        if (hasClosed == hasExpanded) throw new InvalidOperationException(ScreenshotHint);
        var ocr = await WindowsOcrReader.ReadAsync(frame, ct).ConfigureAwait(false);
        if (!ocr.Available)
            throw new InvalidOperationException("Windows 无法识别此截图，请确认已安装简体中文 OCR，并使用清晰的游戏截图。");
        var rows = hasExpanded
            ? await ReadExpandedRowsAsync(frame, ocr.Lines, ct).ConfigureAwait(false)
            : null;
        var header = await ReadHeaderAsync(frame, ocr.Lines, ct).ConfigureAwait(false);
        var confirmed = hasClosed && await ConfirmButtonAsync(frame, ocr.Lines, ct).ConfigureAwait(false);
        var accounts = Recognize(frame.Width, frame.Height, ocr.Lines, hasClosed, hasExpanded, rows, header, confirmed);
        return accounts.Count > 0 ? accounts : throw new InvalidOperationException(
            "截图中没有可唯一识别的脱敏账号，请收起列表后逐个截图，或重新截取清晰的完整列表。");
    }

    public static async Task<IReadOnlyList<Guid>> RegisterAsync(LocalDatabase database, string root, string profileDirectory,
        string gamePath, IReadOnlyList<string> masks, CancellationToken ct = default)
    {
        using var desktop = DesktopOperationLease.Acquire(profileDirectory);
        await ValidateSetupAsync(root, gamePath, ct).ConfigureAwait(false);
        var ids = await database.RegisterZenlessScreenshotAsync(masks, ct).ConfigureAwait(false);
        var accounts = await database.LoadAccountsAsync(GameId.ZenlessZoneZero, ct).ConfigureAwait(false);
        for (var index = 0; index < ids.Length; index++)
            await PrepareRegisteredAsync(database, accounts.Single(account => account.Id == ids[index]),
                root, gamePath, masks[index], ct).ConfigureAwait(false);
        return ids;
    }

    public static async Task PrepareAsync(LocalDatabase database, GameAccount account, string root,
        string profileDirectory, string gamePath, string masked, CancellationToken ct = default)
    {
        using var desktop = DesktopOperationLease.Acquire(profileDirectory);
        await ValidateSetupAsync(root, gamePath, ct).ConfigureAwait(false);
        await PrepareRegisteredAsync(database, account, root, gamePath, masked, ct).ConfigureAwait(false);
    }

    internal static async Task PrepareRegisteredAsync(LocalDatabase database, GameAccount account, string root,
        string gamePath, string masked, CancellationToken ct)
    {
        if (account.Game != GameId.ZenlessZoneZero || account.IsLegacyPlaceholder)
            throw new InvalidOperationException("请选择一个绝区零账号档案。");
        var normalized = MaskedPhoneParser.Normalize(masked.Trim());
        if (normalized.Length == 0 || !masked.Trim().All(ch => char.IsAsciiDigit(ch) || ch is '*' or '＊'))
            throw new InvalidOperationException("请填写登录卡片上的脱敏手机号，或先导入登录截图。");
        var profiles = await database.LoadLoginProfilesAsync(GameId.ZenlessZoneZero, ct).ConfigureAwait(false);
        var profile = profiles.SingleOrDefault(row => row.AccountId == account.Id);
        if (profiles.Any(row => row.AccountId != account.Id && row.NormalizedPhone == normalized))
            throw new InvalidOperationException("该脱敏手机号已登记。请选中已有账号。");

        var existing = OneDragonProfileBinding.FromSettings(
            (await database.LoadAccountSettingsAsync(account.Id, ct).ConfigureAwait(false)).Values);
        if (existing is not null && !string.Equals(existing.Root, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("该账号已绑定另一处 OneDragon 安装，请在账号管理核对原绑定。");

        var binding = existing ?? await OneDragonImportedProfile.EnsureAsync(root, account.Id, gamePath, ct).ConfigureAwait(false);
        if (existing is null || profile?.NormalizedPhone != normalized)
            await database.SaveOneDragonBindingAsync(account, binding, normalized, ct).ConfigureAwait(false);
        await OneDragonSourceProfile.PrepareMissingAsync(binding, gamePath, ct).ConfigureAwait(false);
    }

    private static async Task ValidateSetupAsync(string root, string gamePath, CancellationToken ct)
    {
        if (ZenlessLoginDriver.EngineBusy())
            throw new InvalidOperationException("请关闭 OneDragon 窗口后再准备运行配置。");
        await OneDragonAccountBatch.ValidateInstallationAsync(root, ct).ConfigureAwait(false);
        if (!Path.IsPathFullyQualified(gamePath) ||
            !string.Equals(Path.GetFileName(gamePath), "ZenlessZoneZero.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择国服绝区零的 ZenlessZoneZero.exe。");
        try
        {
            using var image = GenshinGameExecutable.OpenForSignatureVerification(gamePath);
            GenshinGameExecutable.Verify(gamePath, image);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new InvalidOperationException("无法核对绝区零客户端的文件路径或米哈游签名。", ex);
        }
    }

    internal static IReadOnlyList<string> Recognize(int width, int height, IReadOnlyList<RecognizedText> lines,
        bool closed, bool expanded, IReadOnlyList<LoginAccountObservation>? expandedRows = null,
        IReadOnlyList<LoginAccountObservation>? confirmedHeader = null, bool? confirmedButton = null)
    {
        if (!IsGameFrame(width, height) || closed == expanded) return [];
        RecognizedText[] InBox(double x, double y, double w, double h)
        {
            var box = ScaleBox(width, height, x, y, w, h);
            return lines.Where(line => box.Contains(line.CenterX, line.CenterY)).ToArray();
        }

        var header = confirmedHeader ?? MaskedPhoneParser.FindAll(InBox(610, 450, 590, 82));
        if (header.Count != 1) return [];
        if (closed)
            return (confirmedButton ?? (LoginStateClassifier.FindExact(InBox(880, 580, 170, 85), "进入游戏") is not null))
                ? [header[0].NormalizedPhone] : [];

        var rows = expandedRows ?? MaskedPhoneParser.FindAll(InBox(620, 532, 610, 293));
        var masks = rows.Select(row => row.NormalizedPhone).ToArray();
        if (masks.Distinct(StringComparer.Ordinal).Count() != masks.Length) return [];
        return masks.Contains(header[0].NormalizedPhone, StringComparer.Ordinal)
            ? masks : [header[0].NormalizedPhone, .. masks];
    }

    internal static async Task<bool> ConfirmButtonAsync(CapturedFrame frame, IReadOnlyList<RecognizedText> lines,
        CancellationToken ct, Func<CapturedFrame, CancellationToken, Task<OcrReadResult>>? readCrop = null)
    {
        var box = ScaleBox(frame.Width, frame.Height, 870, 580, 180, 87);
        if (LoginStateClassifier.FindExact(lines.Where(line => box.Contains(line.CenterX, line.CenterY)).ToArray(),
                "进入游戏") is not null)
            return true;

        var read = await ReadHighContrastAsync(frame, box, ct, readCrop).ConfigureAwait(false);
        // Used only with a verified closed dropdown and the account card. The stylized leading 进 can disappear.
        return read.Available && read.Lines is [{ } label]
            && LoginStateClassifier.Compact(label.Text) is "进入游戏" or "入游戏";
    }

    internal static Task<OcrReadResult> ReadHighContrastAsync(CapturedFrame frame, ScreenRegion box,
        CancellationToken ct, Func<CapturedFrame, CancellationToken, Task<OcrReadResult>>? readCrop = null)
    {
        var crop = frame.Crop((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height);
        for (var i = 0; i < crop.Bgra.Length; i += 4)
        {
            var light = crop.Bgra[i] >= 190 && crop.Bgra[i + 1] >= 190 && crop.Bgra[i + 2] >= 190;
            crop.Bgra[i] = crop.Bgra[i + 1] = crop.Bgra[i + 2] = light ? (byte)0 : (byte)255;
        }
        return (readCrop ?? WindowsOcrReader.ReadAsync)(crop, ct);
    }

    internal static async Task<IReadOnlyList<LoginAccountObservation>> ReadHeaderAsync(CapturedFrame frame,
        IReadOnlyList<RecognizedText> lines, CancellationToken ct,
        Func<CapturedFrame, CancellationToken, Task<OcrReadResult>>? readCrop = null)
    {
        var header = ScaleBox(frame.Width, frame.Height, 610, 450, 590, 82);
        var readPhone = readCrop ?? WindowsOcrReader.ReadAsync;
        var accounts = await ReadMaskedRowsAsync(frame, lines, header, ct, readPhone).ConfigureAwait(false);
        if (accounts.Count > 0) return accounts;

        // An entire phone line can be missing from full-window OCR. Reread the isolated phone on the verified card.
        var phone = ScaleBox(frame.Width, frame.Height, 721, 477, 294, 46);
        var x = (int)phone.X;
        var y = (int)phone.Y;
        var crop = frame.Crop(x, y, (int)phone.Width, (int)phone.Height);
        var read = await readPhone(crop, ct).ConfigureAwait(false);
        return read.Available && MaskedPhoneParser.FindAll(read.Lines) is [{ } found]
            ? [found with { X = found.X + x, Y = found.Y + y }]
            : [];
    }

    internal static Task<IReadOnlyList<LoginAccountObservation>> ReadExpandedRowsAsync(CapturedFrame frame,
        IReadOnlyList<RecognizedText> lines, CancellationToken ct,
        Func<CapturedFrame, CancellationToken, Task<OcrReadResult>>? readCrop = null) =>
        ReadMaskedRowsAsync(frame, lines, ScaleBox(frame.Width, frame.Height, 620, 532, 610, 293), ct,
            readCrop ?? WindowsOcrReader.ReadAsync);

    private static async Task<IReadOnlyList<LoginAccountObservation>> ReadMaskedRowsAsync(CapturedFrame frame,
        IReadOnlyList<RecognizedText> lines, ScreenRegion box, CancellationToken ct,
        Func<CapturedFrame, CancellationToken, Task<OcrReadResult>> readCrop)
    {
        var visible = lines.Where(line => box.Contains(line.CenterX, line.CenterY)).ToArray();
        var accounts = MaskedPhoneParser.FindAll(visible).ToList();
        foreach (var line in visible)
        {
            var prefix = MaskedPhoneParser.ToAsciiDigits(line.Text.Trim());
            if (prefix.Length != 3 || !prefix.All(char.IsAsciiDigit) || line.Height < 8
                || line.X * 1920 / frame.Width is < 700 or > 810
                || accounts.Any(account => Math.Abs(account.CenterY - line.CenterY) < line.Height))
                continue;

            // Windows OCR can read the three leading digits and drop the tail in a full frame.
            // Reread just that row at double scale, and require agreement on its prefix and position.
            const int scale = 2;
            var x = Math.Max(0, (int)Math.Round(line.X - line.Height * .4));
            var y = Math.Max(0, (int)Math.Round(line.Y - line.Height * .6));
            var w = Math.Min(frame.Width - x, (int)Math.Round(line.Height * 8.6));
            var h = Math.Min(frame.Height - y, (int)Math.Round(line.Height * 2.1));
            var retry = await readCrop(frame.Crop(x, y, w, h).Scale(scale), ct).ConfigureAwait(false);
            if (!retry.Available || MaskedPhoneParser.FindAll(retry.Lines) is not [{ } recovered]
                || !recovered.NormalizedPhone.StartsWith(prefix + "****", StringComparison.Ordinal)
                || Math.Abs(recovered.X / scale + x - line.X) > line.Height * .5
                || Math.Abs(recovered.CenterY / scale + y - line.CenterY) > line.Height * .5)
                continue;

            accounts.Add(recovered with
            {
                X = recovered.X / scale + x,
                Y = recovered.Y / scale + y,
                Width = recovered.Width / scale,
                Height = recovered.Height / scale,
            });
        }
        return accounts.OrderBy(account => account.CenterY).ThenBy(account => account.X).ToArray();
    }

    internal static ScreenRegion ScaleBox(int width, int height, double x, double y, double w, double h) =>
        new(x * width / 1920, y * height / 1080, w * width / 1920, h * height / 1080);

    private static bool IsGameFrame(int width, int height) =>
        height >= 480 && Math.Abs((double)width / height - 16d / 9) <= .06;
}
