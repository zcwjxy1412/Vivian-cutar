using System.Text.RegularExpressions;

namespace VIvian.Core.Accounts;

/// <summary>
/// Reads the in-game UID that Star Rail prints in the HUD. It is optional identity evidence, not a required
/// account field: the product must keep working when OCR cannot read the small text.
/// </summary>
public static partial class GameUidParser
{
    [GeneratedRegex(@"(?i)(?:UID|U\s*I\s*D)\s*[:：]?\s*([0-9]{8,12})", RegexOptions.CultureInvariant)]
    private static partial Regex UidPattern();

    [GeneratedRegex(@"^\s*([0-9]{8,12})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BareUidPattern();

    public static string? Find(IEnumerable<RecognizedText> lines)
    {
        foreach (var line in lines)
        {
            var compact = string.Concat(line.Text.Where(c => !char.IsWhiteSpace(c)));
            var match = UidPattern().Match(compact);
            if (match.Success) return match.Groups[1].Value;
        }
        // A line next to a separate "UID" label is common when OCR splits the two words.
        var linesList = lines.ToArray();
        for (var i = 0; i < linesList.Length; i++)
        {
            var compact = string.Concat(linesList[i].Text.Where(c => !char.IsWhiteSpace(c)));
            if (!compact.Equals("UID", StringComparison.OrdinalIgnoreCase)) continue;
            for (var j = 0; j < linesList.Length; j++)
            {
                if (i == j) continue;
                var other = string.Concat(linesList[j].Text.Where(c => !char.IsWhiteSpace(c)));
                var bare = BareUidPattern().Match(other);
                if (!bare.Success) continue;
                if (Math.Abs(linesList[j].CenterY - linesList[i].CenterY) < 80 &&
                    Math.Abs(linesList[j].CenterX - linesList[i].CenterX) < 400)
                    return bare.Groups[1].Value;
            }
        }
        return null;
    }

    public static bool LooksInGame(IEnumerable<RecognizedText> lines)
    {
        var compact = lines.Select(l => string.Concat(l.Text.Where(c => !char.IsWhiteSpace(c)))).ToArray();
        if (compact.Any(t => t.Contains("点击进入", StringComparison.Ordinal) ||
                             t.Contains("开始游戏", StringComparison.Ordinal) ||
                             t.Contains("进入游戏", StringComparison.Ordinal) ||
                             t.Contains("登录其他账号", StringComparison.Ordinal))) return false;
        return compact.Any(t => t.Contains("开拓等级", StringComparison.Ordinal) ||
                                t.Contains("星琼", StringComparison.Ordinal) ||
                                t.Contains("委托", StringComparison.Ordinal) ||
                                t.Contains("跃迁", StringComparison.Ordinal) ||
                                t.Contains("UID", StringComparison.OrdinalIgnoreCase));
    }
}
