using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VIvian.Core.Accounts;

/// <summary>One text line produced by the local OCR engine. Coordinates are physical pixels in the captured client area.</summary>
public sealed record RecognizedText(string Text, double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public double Bottom => Y + Height;
}

/// <summary>A rectangle in client pixels; used to restrict recognition to the login dialog.</summary>
public sealed record ScreenRegion(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool Contains(double x, double y, double margin = 0) =>
        x >= X - margin && x <= Right + margin && y >= Y - margin && y <= Bottom + margin;
}

/// <summary>One saved-account row visible in the miHoYo login dialog.</summary>
public sealed record LoginAccountObservation(string MaskedPhone, string NormalizedPhone, int RowOrder,
    double X, double Y, double Width, double Height, double Confidence, string NearbyText)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

public sealed record LoginScreenObservation(int WindowWidth, int WindowHeight, bool LoginDialogVisible,
    bool AccountListExpanded, IReadOnlyList<LoginAccountObservation> Accounts, double Confidence, string Detail);

public enum LoginScanFailure
{
    None,
    WindowNotFound,
    CaptureFailed,
    OcrUnavailable,
    OcrFailed,
    LoginScreenNotFound,
    NoAccountList,
    AmbiguousAccount,
    WindowMinimized,
    WindowOccluded,
    CapturedWrongWindow,
}

public sealed record LoginScanResult(bool Success, LoginScanFailure Failure, string Detail,
    LoginScreenObservation? Observation, IReadOnlyList<string> OcrLines)
{
    public static LoginScanResult Fail(LoginScanFailure failure, string detail, IReadOnlyList<string>? lines = null) =>
        new(false, failure, detail, null, lines ?? Array.Empty<string>());
}

/// <summary>
/// Parses the masked phone numbers the game shows for saved accounts. The parser deliberately ignores anything
/// that is not an exact masked-number shape; a wrong guess would select another player's account.
/// </summary>
public static partial class MaskedPhoneParser
{
    // 153*****57, 153**** 57, 153＊＊＊＊＊57. Full-width digits and asterisks are accepted because OCR may
    // return them for a Chinese font. At least three mask characters are required.
    [GeneratedRegex(@"(?<![\d０-９])([0-9０-９]{3})\s*[*＊•·]{3,}\s*([0-9０-９]{2,4})(?![\d０-９])",
        RegexOptions.CultureInvariant)]
    private static partial Regex MaskedPhone();

    private static readonly char[] FullWidthDigits = "０１２３４５６７８９".ToCharArray();

    public static string ToAsciiDigits(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            var index = Array.IndexOf(FullWidthDigits, c);
            builder.Append(index >= 0 ? (char)('0' + index) : c);
        }
        return builder.ToString();
    }

    /// <summary>Identity key that ignores the number of mask characters and surrounding whitespace.</summary>
    public static string Normalize(string maskedPhone)
    {
        var match = MaskedPhone().Match(ToAsciiDigits(maskedPhone));
        return match.Success ? $"{match.Groups[1].Value}****{match.Groups[2].Value}" : "";
    }

    public static IReadOnlyList<LoginAccountObservation> FindAll(IReadOnlyList<RecognizedText> lines,
        ScreenRegion? region = null)
    {
        var scoped = region is null
            ? lines
            : lines.Where(l => region.Contains(l.CenterX, l.CenterY, Math.Max(12, l.Height))).ToArray();
        var rows = MergeRows(scoped);
        var found = new List<LoginAccountObservation>();
        foreach (var row in rows)
        {
            var matches = MaskedPhone().Matches(row.Text);
            foreach (Match match in matches)
            {
                var display = Regex.Replace(match.Value, @"\s+", "");
                var normalized = Normalize(display);
                if (normalized.Length == 0) continue;
                // Distribute the row box over the matching characters so two accounts on one OCR line keep
                // separate click targets instead of collapsing onto the same centre.
                var start = row.Text.Length == 0 ? 0d : (double)match.Index / row.Text.Length;
                var width = row.Text.Length == 0 ? row.Width : row.Width * match.Length / row.Text.Length;
                var x = row.X + row.Width * start;
                var confidence = match.Groups[2].Value.Length switch
                {
                    4 => 0.95,
                    3 => 0.9,
                    _ => 0.82,
                };
                found.Add(new LoginAccountObservation(display, normalized, 0, x, row.Y, width, row.Height,
                    confidence, NearbyText(rows, row)));
            }
        }

        found.AddRange(Reconstruct(scoped, region));

        // OCR frequently returns the same row twice (for example a logo line and a button line). Keep one
        // observation per identity and vertical position.
        var deduped = new List<LoginAccountObservation>();
        foreach (var candidate in found.OrderBy(a => a.CenterY).ThenBy(a => a.CenterX))
        {
            if (deduped.Any(existing => existing.NormalizedPhone == candidate.NormalizedPhone &&
                    Math.Abs(existing.CenterY - candidate.CenterY) < Math.Max(8, candidate.Height * 0.8)))
                continue;
            deduped.Add(candidate);
        }

        return deduped.Select((a, index) => a with { RowOrder = index }).ToArray();
    }

    /// <summary>
    /// Windows OCR often drops the asterisks in the masked phone, leaving the prefix and suffix as separate
    /// runs. Rebuild the identity only when a three-digit run and a two-to-four-digit run sit on the same
    /// visual row with a gap where the mask belongs.
    /// </summary>
    private static IEnumerable<LoginAccountObservation> Reconstruct(IReadOnlyList<RecognizedText> lines,
        ScreenRegion? region)
    {
        var groups = new List<List<(RecognizedText Line, string Digits)>>();
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).OrderBy(l => l.CenterY))
        {
            var digits = Regex.Replace(ToAsciiDigits(line.Text), @"\s+", "");
            if (digits.Length is < 1 or > 4 || !digits.All(char.IsAsciiDigit)) continue;
            var group = groups.FirstOrDefault(g =>
                Math.Abs(g.Average(x => x.Line.CenterY) - line.CenterY) <= Math.Max(10, line.Height));
            if (group is null) groups.Add([(line, digits)]);
            else group.Add((line, digits));
        }

        foreach (var group in groups)
        {
            var runs = group.OrderBy(x => x.Line.X).ToArray();
            for (var i = 0; i < runs.Length; i++)
            {
                if (runs[i].Digits.Length != 3) continue;
                for (var j = i + 1; j < runs.Length; j++)
                {
                    if (runs[j].Digits.Length is < 2 or > 4) continue;
                    var gap = runs[j].Line.X - (runs[i].Line.X + runs[i].Line.Width);
                    if (gap < 12 || gap > 600) continue;
                    if (region is not null &&
                        (!region.Contains(runs[i].Line.CenterX, runs[i].Line.CenterY, 12) ||
                         !region.Contains(runs[j].Line.CenterX, runs[j].Line.CenterY, 12))) continue;
                    var left = runs[i].Line.X;
                    var right = runs[j].Line.X + runs[j].Line.Width;
                    var top = Math.Min(runs[i].Line.Y, runs[j].Line.Y);
                    var bottom = Math.Max(runs[i].Line.Bottom, runs[j].Line.Bottom);
                    yield return new LoginAccountObservation($"{runs[i].Digits}*****{runs[j].Digits}",
                        $"{runs[i].Digits}****{runs[j].Digits}", 0, left, top, right - left, bottom - top, 0.8,
                        string.Join(" ", group.Select(x => x.Line.Text)));
                }
            }
        }
    }

    private static string NearbyText(IReadOnlyList<RecognizedText> rows, RecognizedText row) =>
        string.Join(" ", rows.Where(r => r != row && Math.Abs(r.CenterY - row.CenterY) <= Math.Max(18, row.Height) &&
            Math.Abs(r.CenterX - row.CenterX) <= Math.Max(420, row.Width * 2)).Select(r => r.Text));

    /// <summary>
    /// Joins OCR lines that sit on the same visual row. Windows OCR sometimes splits a masked number into
    /// "153" and "*****57"; matching the joined row recovers it without inventing characters.
    /// </summary>
    private static IReadOnlyList<RecognizedText> MergeRows(IReadOnlyList<RecognizedText> lines)
    {
        var result = new List<RecognizedText>();
        foreach (var group in lines.Where(l => !string.IsNullOrWhiteSpace(l.Text))
                     .GroupBy(l => Math.Round(l.CenterY / Math.Max(10, l.Height * 0.7)))
                     .OrderBy(g => g.Key))
        {
            var ordered = group.OrderBy(l => l.X).ToArray();
            var left = ordered.Min(l => l.X);
            var top = ordered.Min(l => l.Y);
            var right = ordered.Max(l => l.X + l.Width);
            var bottom = ordered.Max(l => l.Bottom);
            result.Add(new RecognizedText(string.Join(" ", ordered.Select(l => l.Text.Trim())), left, top,
                right - left, bottom - top));
        }
        return result;
    }
}

/// <summary>Interprets OCR output as a login dialog. Absence of the dialogue is a normal, reportable outcome.</summary>
public static class LoginScreenAnalyzer
{
    public static LoginScanResult Analyze(int windowWidth, int windowHeight, IReadOnlyList<RecognizedText> lines,
        ScreenRegion? region = null)
    {
        var ocrLines = lines.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
        if (lines.Count == 0)
            return LoginScanResult.Fail(LoginScanFailure.OcrFailed, "本地 OCR 没有返回任何文字。", ocrLines);

        var accounts = MaskedPhoneParser.FindAll(lines, region);
        var compact = lines.Select(l => string.Concat(l.Text.Where(c => !char.IsWhiteSpace(c)))).ToArray();
        var hasEnter = compact.Any(t => t.Contains("进入游戏", StringComparison.Ordinal));
        var hasOther = compact.Any(t => t.Contains("登录其他账号", StringComparison.Ordinal));
        var hasBrand = compact.Any(t => t.Contains("miHoYo", StringComparison.OrdinalIgnoreCase) ||
                                       t.Contains("米哈游", StringComparison.Ordinal));
        var hasPassword = compact.Any(t => t.Contains("密码", StringComparison.Ordinal) ||
                                          t.Contains("手机号", StringComparison.Ordinal));
        var dialog = accounts.Count > 0 && (hasEnter || hasOther || hasBrand || hasPassword);

        if (!dialog)
        {
            var detail = accounts.Count == 0
                ? "没有找到米哈游登录对话框（未识别到脱敏手机号）。"
                : "找到脱敏手机号，但没有找到“进入游戏”或“登录其他账号”，无法确认这是登录界面。";
            return LoginScanResult.Fail(LoginScanFailure.LoginScreenNotFound, detail, ocrLines);
        }
        if (accounts.Count == 0)
            return LoginScanResult.Fail(LoginScanFailure.NoAccountList, "登录对话框可见，但没有读到已保存账号。", ocrLines);

        var confidence = accounts.Average(a => a.Confidence);
        if (hasEnter && hasOther) confidence = Math.Min(1, confidence + 0.05);
        var expanded = accounts.Count > 1 || compact.Any(t => t.Contains("最近登录", StringComparison.Ordinal));
        var observation = new LoginScreenObservation(windowWidth, windowHeight, true, expanded, accounts,
            Math.Round(confidence, 3),
            expanded ? $"识别到 {accounts.Count} 个已保存账号。" : $"识别到 {accounts.Count} 个已保存账号（列表可能尚未展开）。");
        return new LoginScanResult(true, LoginScanFailure.None, observation.Detail, observation, ocrLines);
    }
}

public enum AccountSelectionFailure { None, NotFound, Ambiguous }

public sealed record AccountSelection(bool Success, int Index, LoginAccountObservation? Account,
    AccountSelectionFailure Failure, string Detail);

/// <summary>Maps a stored profile to exactly one visible row; ties are reported, never broken by a guess.</summary>
public static class AccountSelectionResolver
{
    public static AccountSelection Resolve(IReadOnlyList<LoginAccountObservation> accounts, string identifier, int rowOrder)
    {
        var normalized = MaskedPhoneParser.Normalize(identifier);
        var candidates = accounts.Where(a => normalized.Length > 0
            ? a.NormalizedPhone == normalized
            : string.Equals(a.MaskedPhone, identifier, StringComparison.Ordinal)).ToArray();
        if (candidates.Length == 0)
            return new AccountSelection(false, -1, null, AccountSelectionFailure.NotFound,
                $"登录界面中没有找到账号“{identifier}”。");
        if (candidates.Length == 1)
            return new AccountSelection(true, candidates[0].RowOrder, candidates[0], AccountSelectionFailure.None,
                $"已锁定账号“{candidates[0].MaskedPhone}”。");

        var byOrder = candidates.Where(a => a.RowOrder == rowOrder).ToArray();
        if (byOrder.Length == 1)
            return new AccountSelection(true, byOrder[0].RowOrder, byOrder[0], AccountSelectionFailure.None,
                $"脱敏手机号重复，按登记顺序锁定第 {rowOrder + 1} 项。");
        return new AccountSelection(false, -1, null, AccountSelectionFailure.Ambiguous,
            $"登录界面中有 {candidates.Length} 个账号显示为“{identifier}”，无法唯一确定目标；请重新扫描或使用备注区分。");
    }
}

public enum AccountConfigurationMode { Unified, Independent }

/// <summary>
/// The first selected account owns the task plan and settings snapshot for the whole batch. Other accounts reuse
/// that snapshot; their own stored profiles are never overwritten.
/// </summary>
public static class BatchConfigurationResolver
{
    public static IReadOnlyDictionary<string, string> Resolve(IReadOnlyDictionary<string, string> firstSnapshot,
        IReadOnlyDictionary<string, string> accountSettings, AccountConfigurationMode mode) =>
        new Dictionary<string, string>(mode == AccountConfigurationMode.Unified ? firstSnapshot : accountSettings,
            StringComparer.Ordinal);
}

public sealed record SwitchPolicy(
    int MaxAttempts = 2,
    TimeSpan? LoginScreenTimeout = null,
    TimeSpan? StepTimeout = null,
    TimeSpan? GameStartTimeout = null,
    TimeSpan? GameExitTimeout = null,
    TimeSpan? InGameTimeout = null)
{
    public TimeSpan LoginScreen { get; } = Positive(LoginScreenTimeout, TimeSpan.FromSeconds(90));
    public TimeSpan Step { get; } = Positive(StepTimeout, TimeSpan.FromSeconds(35));
    public TimeSpan GameStart { get; } = Positive(GameStartTimeout, TimeSpan.FromSeconds(150));
    public TimeSpan GameExit { get; } = Positive(GameExitTimeout, TimeSpan.FromSeconds(45));
    public TimeSpan InGame { get; } = Positive(InGameTimeout, TimeSpan.FromSeconds(90));

    public int Attempts => MaxAttempts is >= 1 and <= 5
        ? MaxAttempts : throw new ArgumentOutOfRangeException(nameof(MaxAttempts), MaxAttempts, "重试次数必须在 1 到 5 之间。");

    private static TimeSpan Positive(TimeSpan? value, TimeSpan fallback)
    {
        var result = value ?? fallback;
        return result > TimeSpan.Zero && result <= TimeSpan.FromMinutes(15)
            ? result : throw new ArgumentOutOfRangeException(nameof(value), result, "超时必须大于 0 且不超过 15 分钟。");
    }
}

public enum BatchRunState
{
    Waiting,
    RecognizingLogin,
    SelectingAccount,
    LoggingIn,
    VerifyingLogin,
    LoadingConfiguration,
    RunningTasks,
    StoppingTasks,
    SwitchingAccount,
    Completed,
    Failed,
    ReauthorizationRequired,
}

public sealed record BatchAccountProgress(Guid AccountId, string AccountName, BatchRunState State, string Detail,
    int Attempt, DateTimeOffset At);

public static class BatchProgressText
{
    public static string Describe(BatchRunState state) => state switch
    {
        BatchRunState.Waiting => "等待启动",
        BatchRunState.RecognizingLogin => "正在识别登录界面",
        BatchRunState.SelectingAccount => "正在选择账号",
        BatchRunState.LoggingIn => "正在登录",
        BatchRunState.VerifyingLogin => "正在确认登录状态",
        BatchRunState.LoadingConfiguration => "正在加载配置",
        BatchRunState.RunningTasks => "正在执行任务",
        BatchRunState.StoppingTasks => "正在停止任务",
        BatchRunState.SwitchingAccount => "正在切换下一个账号",
        BatchRunState.Completed => "已完成",
        BatchRunState.Failed => "失败",
        BatchRunState.ReauthorizationRequired => "需要重新授权",
        _ => state.ToString(),
    };
}

/// <summary>Formats a duration for progress text without relying on the current culture.</summary>
public static class DurationText
{
    public static string Seconds(TimeSpan value) =>
        Math.Round(value.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " 秒";
}
