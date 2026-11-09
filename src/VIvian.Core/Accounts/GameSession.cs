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
        var hud = lines.Select(l => l with { Text = LoginStateClassifier.Compact(l.Text) }).ToArray();
        var compact = hud.Select(l => l.Text).ToArray();
        if (compact.Any(t => t.Contains("点击进入", StringComparison.Ordinal) ||
                             t.Contains("开始游戏", StringComparison.Ordinal) ||
                             t.Contains("进入游戏", StringComparison.Ordinal) ||
                             t.Contains("登录其他账号", StringComparison.Ordinal))) return false;
        // The version footer can contain a UID before the world has loaded.
        return compact.Any(t => t.StartsWith("开拓等级", StringComparison.Ordinal) ||
                                t is "查看短信" or "阅读短信") ||
               compact.Count(t => t is "委托" or "跃迁" or "编队" or "背包" or "任务") >= 2 ||
               HasWorldHud(hud);
    }

    private static bool HasWorldHud(IReadOnlyList<RecognizedText> lines)
    {
        // Normal exploration shows status effects by the party and the chat shortcut across the HUD.
        // The loading footer shows neither pair, even when it already displays a UID.
        var effects = lines.FirstOrDefault(l => l.Text is "状态效果" or "状态效果U");
        return effects is not null && lines.Any(chat => chat.Text.EndsWith("Enter", StringComparison.OrdinalIgnoreCase) &&
            chat.CenterX < effects.CenterX - 4 * effects.Width && chat.CenterY > effects.CenterY + 4 * effects.Height);
    }
}
