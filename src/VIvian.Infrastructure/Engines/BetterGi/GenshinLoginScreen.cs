using System.Text.RegularExpressions;
using VIvian.Core.Accounts;

namespace VIvian.Infrastructure.Engines.BetterGi;

public enum GenshinLoginScreenState
{
    Unrecognized,
    SavedAccountCard,
    AmbiguousAccount,
    NeedsUser,
}

/// <summary>Classifies OCR from a verified Genshin client frame. Retains only a masked row and the entry label bounds.</summary>
public sealed partial class GenshinLoginScreen
{
    private static readonly string[] UserOnlyText =
    [
        "输入密码", "登录密码", "密码登录", "验证码", "安全验证", "身份验证", "请完成验证", "二次验证",
        "短信验证", "手机验证", "人机验证", "滑块", "扫码登录", "实名认证", "用户协议", "隐私政策", "隐私协议",
    ];
    private static readonly string[] UnsupportedOverlayText =
        ["退出登录", "注销登录", "确定退出", "登录失败", "登录异常", "网络错误", "网络异常", "账号异常"];

    private GenshinLoginScreen(GenshinLoginScreenState state, LoginAccountObservation? account = null,
        ScreenRegion? enterLabel = null)
    {
        State = state;
        Account = account;
        EnterLabel = enterLabel;
    }

    public GenshinLoginScreenState State { get; }
    public LoginAccountObservation? Account { get; }
    internal ScreenRegion? EnterLabel { get; }

    public static GenshinLoginScreen Analyze(int width, int height, IReadOnlyList<RecognizedText> lines) =>
        Analyze(width, height, lines, null);

    internal static GenshinLoginScreen Analyze(int width, int height, IReadOnlyList<RecognizedText> lines,
        ScreenRegion? closedCaret)
    {
        var compact = lines.Select(line => line with { Text = Compact(line.Text) }).ToArray();
        if (compact.Any(line => IsUserOnly(line.Text)))
            return new(GenshinLoginScreenState.NeedsUser);

        if (width <= 0 || height <= 0 || compact.Any(line => !WithinFrame(line, width, height) || IsBlockingOverlay(line.Text)))
            return new(GenshinLoginScreenState.Unrecognized);

        var brand = Unique(compact.Where(line => IsBrand(line.Text, allowPartial: closedCaret is not null)));
        var enter = UniqueLabel(compact, "进入游戏");
        var other = UniqueLabel(compact, "登录其他账号");
        if (brand is null || enter is null || other is null || !MatchesReferenceLayout(brand, enter, other, width, height))
            return new(GenshinLoginScreenState.Unrecognized);

        var possibleRows = compact.Where(line => line.CenterY > brand.Bottom && line.CenterY < other.Y &&
                Math.Abs(line.CenterX - enter.CenterX) <= enter.Width * 3.5 &&
                line.Text.Any(IsPossibleAccountCharacter))
            .Take(2).ToArray();
        if (possibleRows.Length > 1)
            return new(GenshinLoginScreenState.AmbiguousAccount);
        if (possibleRows.Length == 0)
            return new(GenshinLoginScreenState.Unrecognized);

        var row = possibleRows[0];
        var relativeY = (row.CenterY - brand.CenterY) / (enter.CenterY - brand.CenterY);
        var relativeX = (row.CenterX - enter.CenterX) / enter.Width;
        if (!ExactMask().IsMatch(row.Text) || relativeY is < .26 or > .66 || relativeX is < -2.8 or > -.25 ||
            row.Y < brand.Bottom || row.Bottom > enter.Y ||
            (closedCaret is not null && !IsCaretAligned(closedCaret, row)))
            return new(GenshinLoginScreenState.Unrecognized);

        var accounts = MaskedPhoneParser.FindAll([row]);
        return accounts.Count == 1
            ? new(GenshinLoginScreenState.SavedAccountCard, accounts[0], new(enter.X, enter.Y, enter.Width, enter.Height))
            : new(GenshinLoginScreenState.Unrecognized);
    }

    internal static IReadOnlyList<LoginAccountObservation> AnalyzeExpandedList(int width, int height,
        IReadOnlyList<RecognizedText> lines, ScreenRegion? openCaret = null)
    {
        var compact = lines.Select(line => line with { Text = Compact(line.Text) }).ToArray();
        if (compact.Any(line => !WithinFrame(line, width, height) || line.Text == "进入游戏" ||
                IsBlockingOverlay(line.Text) || IsUserOnly(line.Text)))
            return [];

        var footer = UniqueLabel(compact, "登录其他账号");
        if (footer is null || Math.Abs(footer.CenterX - width / 2d) > width * .12) return [];
        var brand = Unique(compact.Where(line => IsBrand(line.Text, allowPartial: true) &&
            Math.Abs(line.CenterX - footer.CenterX) <= footer.Width * .6 &&
            (footer.CenterY - line.CenterY) / footer.Height is >= 11 and <= 28));
        if (brand is null) return [];

        var rows = compact.Where(line => line.Y > brand.Bottom && line.Bottom < footer.Y &&
            (line.CenterX - footer.CenterX) / footer.Width is >= -2.5 and <= -.25 &&
            line.Text.Any(IsPossibleAccountCharacter) && !IsLastUsed(line.Text)).OrderBy(line => line.Y).ToArray();
        if (rows.Length < 2) return [];
        var header = rows[0];
        var headerPosition = (header.CenterY - brand.CenterY) / (footer.CenterY - brand.CenterY);
        var masks = rows.Select(line => NormalizeExactMask(line.Text)).ToArray();
        if (headerPosition is < .17 or > .39 || masks.Any(string.IsNullOrEmpty) ||
            !masks.Skip(1).Contains(masks[0]) || masks.Skip(1).Distinct(StringComparer.Ordinal).Count() != rows.Length - 1 ||
            (openCaret is not null && !IsCaretAligned(openCaret, header)))
            return [];

        var captions = compact.Where(line => line.Y > header.Bottom && line.Bottom < footer.Y &&
            Math.Abs(line.X - header.X) <= header.Width * .6 && IsLastUsed(line.Text)).ToArray();
        if (captions.Length != rows.Length - 1) return [];

        var accounts = new List<LoginAccountObservation>(rows.Length - 1);
        // The selected header is repeated in the list. Only saved rows have a last-used caption.
        for (var i = 1; i < rows.Length; i++)
        {
            var row = rows[i];
            var next = i + 1 < rows.Length ? rows[i + 1] : footer;
            if (row.Y <= rows[i - 1].Bottom || Math.Abs(row.X - header.X) > header.Width * .3 ||
                !captions.Any(line => line.Y >= row.Bottom && line.Bottom < next.Y &&
                    Math.Abs(line.X - row.X) <= row.Width * .55))
                return [];
            accounts.Add(new(row.Text, masks[i], i - 1, row.X, row.Y, row.Width, row.Height, .9, ""));
        }
        return accounts;
    }

    internal static string NormalizeExactMask(string? value)
    {
        var compact = Compact(value ?? "");
        return ExactMask().IsMatch(compact) ? MaskedPhoneParser.Normalize(compact) : "";
    }

    [GeneratedRegex(@"\A[0-9０-９]{3}[*＊•·]{3,}[0-9０-９]{2,4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExactMask();

    [GeneratedRegex(@"\A[0-9]{1,3}(?:秒|分钟|小时|天|周|个月|年)前\z", RegexOptions.CultureInvariant)]
    private static partial Regex LastUsed();

    private static string Compact(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

    private static bool IsPossibleAccountCharacter(char c) => char.IsDigit(c) || c is '*' or '＊' or '•' or '·';

    private static bool IsLastUsed(string text) => text is "刚刚" or "昨天" ||
        LastUsed().IsMatch(MaskedPhoneParser.ToAsciiDigits(text));

    private static bool IsBrand(string text, bool allowPartial) => text.Equals("miHoYo", StringComparison.OrdinalIgnoreCase) ||
        (allowPartial && text.Equals("HO", StringComparison.OrdinalIgnoreCase));

    private static bool IsCaretAligned(ScreenRegion caret, RecognizedText header) =>
        caret.Width > 0 && caret.Height > 0 &&
        Math.Abs(caret.Y + caret.Height / 2 - header.CenterY) <= header.Height &&
        (caret.X + caret.Width / 2 - header.CenterX) / header.Width is >= 1.5 and <= 4.5;

    private static bool IsUserOnly(string compactText) => compactText == "密码" ||
        UserOnlyText.Any(marker => compactText.Contains(marker, StringComparison.Ordinal));

    internal static bool IsBlockingOverlay(string compactText) => compactText is "确定" or "确认" or "取消" ||
        UnsupportedOverlayText.Any(marker => compactText.Contains(marker, StringComparison.Ordinal));

    internal static bool WithinFrame(RecognizedText line, int width, int height) =>
        line.X >= 0 && line.Y >= 0 && line.Width > 0 && line.Height > 0 &&
        line.X + line.Width <= width && line.Bottom <= height;

    private static RecognizedText? UniqueLabel(IReadOnlyList<RecognizedText> lines, string label) =>
        Unique(lines.Where(line => line.Text.Equals(label, StringComparison.OrdinalIgnoreCase)));

    private static RecognizedText? Unique(IEnumerable<RecognizedText> lines)
    {
        var matches = lines.Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    // shortcut: alignment comes from one photographed card; recalibrate using verified client captures before enabling input.
    private static bool MatchesReferenceLayout(RecognizedText brand, RecognizedText enter, RecognizedText other,
        int width, int height) =>
        Math.Abs(enter.CenterX - width / 2d) <= width * .15 &&
        enter.CenterY / height is >= .35 and <= .75 &&
        Math.Abs(enter.CenterX - brand.CenterX) <= enter.Width * .55 &&
        Math.Abs(enter.CenterX - other.CenterX) <= enter.Width * .55 &&
        (enter.CenterY - brand.CenterY) / enter.Height is >= 4 and <= 15 &&
        (other.CenterY - enter.CenterY) / enter.Height is >= 1.8 and <= 6 &&
        brand.Bottom < enter.Y && enter.Bottom < other.Y;
}
