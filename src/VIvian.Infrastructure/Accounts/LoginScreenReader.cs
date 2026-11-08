using VIvian.Core.Accounts;

namespace VIvian.Infrastructure.Accounts;

public sealed record LoginScreenView(LoginScanResult Result, CapturedFrame? Frame, StarRailWindowInfo? Window,
    LoginPanelRect? Panel)
{
    /// <summary>All OCR lines with their client-area boxes, so the controller can locate buttons by text.</summary>
    public IReadOnlyList<RecognizedText> Lines { get; init; } = Array.Empty<RecognizedText>();

    /// <summary>Window identity, capture method, ownership check and OCR boxes for this frame (masked).</summary>
    public ScanEvidence? Evidence { get; init; }

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

public sealed class LoginScreenReader(IGameWindowLocator? locator = null) : ILoginScreenSource
{
    private readonly IGameWindowLocator _locator = locator ?? StarRailWindowLocator.Instance;

    /// <summary>
    /// Reads the game exactly as it is. This never raises, restores or makes the game topmost; a frame whose
    /// pixels cannot be proven to belong to the game window is rejected with an actionable reason.
    /// </summary>
    public async Task<LoginScreenView> ScanAsync(CancellationToken cancellationToken)
    {
        var (failure, frame, window, check) = Capture();
        if (failure is not null) return failure;
        var view = await AnalyzeAsync(frame!, window, cancellationToken).ConfigureAwait(false);
        return view.Evidence is null ? view : view with { Evidence = view.Evidence with { Check = check! } };
    }

    private (LoginScreenView? Failure, CapturedFrame? Frame, StarRailWindowInfo? Window, CaptureCheck? Check) Capture()
    {
        var window = _locator.Find();
        if (window is null)
        {
            var detail = _locator.IsProcessRunning()
                ? "星铁进程存在，但还没有可用的游戏窗口（可能仍在启动）。"
                : "没有找到星铁游戏窗口。请先启动游戏并停在登录界面。";
            return (new LoginScreenView(LoginScanResult.Fail(LoginScanFailure.WindowNotFound, detail), null, null, null),
                null, null, null);
        }
        var (frame, check) = ScreenCapture.CaptureChecked(window);
        var verdict = CaptureVerdict.Evaluate(check);
        var evidence = new ScanEvidence(window.Handle.ToInt64(), window.ProcessId, StarRailWindow.ProcessName,
            window.ClientWidth, window.ClientHeight, DateTimeOffset.UtcNow, check, []);
        if (!verdict.Accept || frame is null)
            return (new LoginScreenView(LoginScanResult.Fail(verdict.Failure == LoginScanFailure.None
                    ? LoginScanFailure.CaptureFailed : verdict.Failure, verdict.Detail), null, window, null)
                { Evidence = evidence }, null, window, check);
        return (null, frame, window, check);
    }

    public async Task<LoginScreenView> ImportAsync(string path, CancellationToken cancellationToken)
    {
        var frame = await WindowsOcrReader.DecodeFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (frame is null)
            return new LoginScreenView(LoginScanResult.Fail(LoginScanFailure.CaptureFailed,
                $"无法读取截图：{path}"), null, null, null);
        return await AnalyzeAsync(frame, null, cancellationToken).ConfigureAwait(false);
    }

    public Task<LoginScreenView> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (failure, frame, window, _) = Capture();
        if (failure is not null) return Task.FromResult(failure);
        return Task.FromResult(new LoginScreenView(
            new LoginScanResult(true, LoginScanFailure.None, "", null, Array.Empty<string>()),
            frame, window, LoginPanelDetector.Detect(frame!)));
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
        if (NeedsTitleCornerScan(lines))
            lines = MergeLines(lines, await ReadCornersAsync(frame, cancellationToken).ConfigureAwait(false));
        var region = panel is null
            ? null
            : new ScreenRegion(panel.X - 8, panel.Y - 8, panel.Width + 16, panel.Height + 16);
        var result = LoginScreenAnalyzer.Analyze(frame.Width, frame.Height, lines, region);
        if (!result.Success && panel is not null && panel.Width >= 240 && panel.Height >= 140)
        {
            result = LoginScanResult.Fail(LoginScanFailure.NoAccountList,
                "登录对话框可见，但没有识别到可用的脱敏手机号。", result.OcrLines);
        }
        var evidence = window is null ? null : new ScanEvidence(window.Handle.ToInt64(), window.ProcessId,
            StarRailWindow.ProcessName, frame.Width, frame.Height, DateTimeOffset.UtcNow,
            new CaptureCheck(true, true, false, frame.Source == "window-print" ? "print" : "bitblt", 0, 0, false),
            result.Observation?.Accounts ?? []);
        return new LoginScreenView(result, frame, window, panel) { Lines = lines, Evidence = evidence };
    }

    /// <summary>
    /// The title screen's account controls are small icons with tiny captions that full-frame OCR often misses.
    /// When "点击进入" is visible but no logout caption was read, the four corners are read again at 3x.
    /// </summary>
    private static bool NeedsTitleCornerScan(IReadOnlyList<RecognizedText> lines)
    {
        var compact = lines.Select(l => LoginStateClassifier.Compact(l.Text)).ToArray();
        return compact.Any(t => t.Contains("点击进入", StringComparison.Ordinal)) &&
               !compact.Any(t => LoginStateClassifier.LogoutControls.Any(w => t.Contains(w, StringComparison.Ordinal)));
    }

    private static async Task<IReadOnlyList<RecognizedText>> ReadCornersAsync(CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        var result = new List<RecognizedText>();
        var w = frame.Width * 32 / 100;
        var h = frame.Height * 30 / 100;
        foreach (var (x, y) in new[] { (frame.Width - w, 0), (0, 0), (frame.Width - w, frame.Height - h), (0, frame.Height - h) })
        {
            cancellationToken.ThrowIfCancellationRequested();
            const int factor = 3;
            var crop = frame.Crop(x, y, w, h);
            var ocr = await WindowsOcrReader.ReadAsync(crop.Scale(factor), cancellationToken).ConfigureAwait(false);
            if (!ocr.Available) continue;
            result.AddRange(ocr.Lines.Select(l => new RecognizedText(l.Text, l.X / factor + x, l.Y / factor + y,
                l.Width / factor, l.Height / factor)));
        }
        return result;
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
