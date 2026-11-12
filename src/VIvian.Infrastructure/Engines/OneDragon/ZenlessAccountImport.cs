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
        var accounts = Recognize(frame.Width, frame.Height, ocr.Lines, hasClosed, hasExpanded);
        return accounts.Count > 0 ? accounts : throw new InvalidOperationException(
            "截图中没有可唯一识别的脱敏账号，请收起列表后逐个截图，或重新截取清晰的完整列表。");
    }

    public static async Task<IReadOnlyList<Guid>> RegisterAsync(LocalDatabase database, string root, string profileDirectory,
        string gamePath, IReadOnlyList<string> masks, CancellationToken ct = default)
    {
        using var desktop = DesktopOperationLease.Acquire(profileDirectory);
        if (ZenlessLoginDriver.EngineBusy())
            throw new InvalidOperationException("请关闭 OneDragon 窗口后再导入，以免覆盖正在编辑的档案。");
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

        var ids = await database.RegisterZenlessScreenshotAsync(masks, ct).ConfigureAwait(false);
        var accounts = await database.LoadAccountsAsync(GameId.ZenlessZoneZero, ct).ConfigureAwait(false);
        for (var index = 0; index < ids.Length; index++)
        {
            var id = ids[index];
            var existing = OneDragonProfileBinding.FromSettings(
                (await database.LoadAccountSettingsAsync(id, ct).ConfigureAwait(false)).Values);
            if (existing is not null)
            {
                if (!string.Equals(existing.Root, root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("该账号已绑定另一处 OneDragon 安装，请在账号管理核对原绑定。");
                await OneDragonSourceProfile.ReadAsync(existing, ct).ConfigureAwait(false);
                continue;
            }
            var binding = await OneDragonImportedProfile.EnsureAsync(root, id, gamePath, ct).ConfigureAwait(false);
            await database.SaveOneDragonBindingAsync(accounts.Single(account => account.Id == id),
                binding, masks[index], ct).ConfigureAwait(false);
        }
        return ids;
    }

    internal static IReadOnlyList<string> Recognize(int width, int height, IReadOnlyList<RecognizedText> lines,
        bool closed, bool expanded)
    {
        if (!IsGameFrame(width, height) || closed == expanded) return [];
        RecognizedText[] InBox(double x, double y, double w, double h)
        {
            var box = ScaleBox(width, height, x, y, w, h);
            return lines.Where(line => box.Contains(line.CenterX, line.CenterY)).ToArray();
        }

        var header = MaskedPhoneParser.FindAll(InBox(610, 450, 590, 82));
        if (header.Count != 1) return [];
        if (closed)
            return LoginStateClassifier.FindExact(InBox(880, 580, 170, 85), "进入游戏") is not null
                ? [header[0].NormalizedPhone] : [];

        var rows = MaskedPhoneParser.FindAll(InBox(620, 532, 610, 293));
        var masks = rows.Select(row => row.NormalizedPhone).ToArray();
        if (masks.Distinct(StringComparer.Ordinal).Count() != masks.Length) return [];
        return masks.Contains(header[0].NormalizedPhone, StringComparer.Ordinal)
            ? masks : [header[0].NormalizedPhone, .. masks];
    }

    internal static ScreenRegion ScaleBox(int width, int height, double x, double y, double w, double h) =>
        new(x * width / 1920, y * height / 1080, w * width / 1920, h * height / 1080);

    private static bool IsGameFrame(int width, int height) =>
        height >= 480 && Math.Abs((double)width / height - 16d / 9) <= .06;
}
