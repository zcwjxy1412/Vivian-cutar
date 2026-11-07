using VIvian.Core.Accounts;

namespace VIvian.Infrastructure.Accounts;

public sealed record LoginScreenView(LoginScanResult Result, CapturedFrame? Frame, StarRailWindowInfo? Window,
    LoginPanelRect? Panel)
{
    /// <summary>All OCR lines with their client-area boxes, so the controller can locate buttons by text.</summary>
    public IReadOnlyList<RecognizedText> Lines { get; init; } = Array.Empty<RecognizedText>();

    public bool LoginDialogVisible => Panel is not null ||
        (Result.Success && Result.Observation is { LoginDialogVisible: true });
}

/// <summary>
/// Captures the game client, runs local OCR, and turns the result into a login-screen observation. This type is
/// read-only: it never clicks, starts, or stops the game.
/// </summary>
public interface ILoginScreenSource
{
    Task<LoginScreenView> ScanAsync(CancellationToken cancellationToken);
    Task<LoginScreenView> CaptureAsync(CancellationToken cancellationToken);
}

public sealed class LoginScreenReader : ILoginScreenSource
{
    public async Task<LoginScreenView> ScanAsync(CancellationToken cancellationToken)
    {
        var window = StarRailWindow.Find();
        if (window is null)
            return new LoginScreenView(LoginScanResult.Fail(LoginScanFailure.WindowNotFound,
                "没有找到星铁游戏窗口。"), null, null, null);
        StarRailWindow.BringToFront(window.Handle);
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        var frame = ScreenCapture.CaptureClient(window.Handle);
        if (frame is null)
            return new LoginScreenView(LoginScanResult.Fail(LoginScanFailure.CaptureFailed,
                "游戏窗口存在，但屏幕捕获失败。"), null, window, null);
        return await AnalyzeAsync(frame, window, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LoginScreenView> ImportAsync(string path, CancellationToken cancellationToken)
    {
        var frame = await WindowsOcrReader.DecodeFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (frame is null)
            return new LoginScreenView(LoginScanResult.Fail(LoginScanFailure.CaptureFailed,
                $"无法读取截图：{path}"), null, null, null);
        return await AnalyzeAsync(frame, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LoginScreenView> CaptureAsync(CancellationToken cancellationToken)
    {
        var window = StarRailWindow.Find();
        if (window is null)
            return new LoginScreenView(LoginScanResult.Fail(LoginScanFailure.WindowNotFound,
                "没有找到星铁游戏窗口。"), null, null, null);
        StarRailWindow.BringToFront(window.Handle);
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        var frame = ScreenCapture.CaptureClient(window.Handle);
        return frame is null
            ? new LoginScreenView(LoginScanResult.Fail(LoginScanFailure.CaptureFailed,
                "游戏窗口存在，但屏幕捕获失败。"), null, window, null)
            : new LoginScreenView(new LoginScanResult(true, LoginScanFailure.None, "", null, Array.Empty<string>()),
                frame, window, LoginPanelDetector.Detect(frame));
    }

    public async Task<LoginScreenView> AnalyzeAsync(CapturedFrame frame, StarRailWindowInfo? window,
        CancellationToken cancellationToken)
    {
        var ocr = await WindowsOcrReader.ReadAsync(frame, cancellationToken).ConfigureAwait(false);
        if (!ocr.Available)
            return new LoginScreenView(LoginScanResult.Fail(LoginScanFailure.OcrUnavailable,
                ocr.Error ?? "本地 OCR 不可用。", ocr.Lines.Select(l => l.Text).ToArray()), frame, window,
                LoginPanelDetector.Detect(frame));

        var panel = LoginPanelDetector.Detect(frame);
        var lines = ocr.Lines;
        if (panel is not null && panel.Width >= 240 && panel.Height >= 140 && LooksLikeLogin(ocr.Lines))
        {
            var panelLines = await ReadPanelAsync(frame, panel, cancellationToken).ConfigureAwait(false);
            lines = MergeLines(ocr.Lines, panelLines);
        }
        var region = panel is null
            ? null
            : new ScreenRegion(panel.X - 8, panel.Y - 8, panel.Width + 16, panel.Height + 16);
        var result = LoginScreenAnalyzer.Analyze(frame.Width, frame.Height, lines, region);
        if (!result.Success && panel is not null && panel.Width >= 240 && panel.Height >= 140)
        {
            result = LoginScanResult.Fail(LoginScanFailure.NoAccountList,
                "登录对话框可见，但没有识别到可用的脱敏手机号。", result.OcrLines);
        }
        return new LoginScreenView(result, frame, window, panel) { Lines = lines };
    }

    private static bool LooksLikeLogin(IReadOnlyList<RecognizedText> lines)
    {
        var compact = lines.Select(l => string.Concat(l.Text.Where(c => !char.IsWhiteSpace(c)))).ToArray();
        return compact.Any(t => t.Contains("进入游戏", StringComparison.Ordinal) ||
                                t.Contains("登录其他账号", StringComparison.Ordinal) ||
                                t.Contains("miHoYo", StringComparison.OrdinalIgnoreCase) ||
                                t.Contains("米哈游", StringComparison.Ordinal) ||
                                t.Contains("密码", StringComparison.Ordinal) ||
                                t.Contains("手机号", StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<RecognizedText>> ReadPanelAsync(CapturedFrame frame,
        LoginPanelRect panel, CancellationToken cancellationToken)
    {
        const int margin = 12;
        var x = Math.Max(0, (int)Math.Floor(panel.X) - margin);
        var y = Math.Max(0, (int)Math.Floor(panel.Y) - margin);
        var width = Math.Min(frame.Width - x, (int)Math.Ceiling(panel.Width) + margin * 2);
        var height = Math.Min(frame.Height - y, (int)Math.Ceiling(panel.Height) + margin * 2);
        var crop = frame.Crop(x, y, width, height);
        var factor = Math.Clamp(2200 / Math.Max(crop.Width, crop.Height), 2, 4);
        var ocr = await WindowsOcrReader.ReadAsync(crop.Scale(factor), cancellationToken).ConfigureAwait(false);
        if (!ocr.Available) return Array.Empty<RecognizedText>();
        return ocr.Lines.Select(l => new RecognizedText(l.Text, l.X / factor + x, l.Y / factor + y,
            l.Width / factor, l.Height / factor)).ToArray();
    }

    private static IReadOnlyList<RecognizedText> MergeLines(IReadOnlyList<RecognizedText> full,
        IReadOnlyList<RecognizedText> panel)
    {
        var merged = new List<RecognizedText>(panel);
        foreach (var line in full)
        {
            var compact = Compact(line.Text);
            if (compact.Length == 0) continue;
            if (merged.Any(m => Compact(m.Text) == compact && Math.Abs(m.CenterY - line.CenterY) < 12)) continue;
            merged.Add(line);
        }
        return merged;
    }

    private static string Compact(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

    public static async Task<string?> SaveEvidenceAsync(LoginScreenView view, string directory, string name,
        CancellationToken cancellationToken)
    {
        if (view.Frame is null) return null;
        try
        {
            var path = Path.Combine(directory, name + ".png");
            await WindowsOcrReader.SavePngAsync(view.Frame, path, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
