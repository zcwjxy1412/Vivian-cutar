using System.Globalization;

namespace VIvian.Infrastructure.Engines.OneDragon;

/// <summary>
/// A validated OneDragon one-dragon request. Built only through <see cref="OneDragonArguments"/>.
/// Allowed (pinned launcher_base.py / exe_launcher.py, level B): <c>-o</c> (required), <c>-c</c> (close the game
/// afterwards) and <c>-i &lt;n[,n…]&gt;</c> (account instances). Power actions (<c>-s</c>, <c>--hibernate</c>,
/// <c>--sleep</c>), GUI mode (no <c>-o</c>) and <c>-v</c>/<c>-h</c> are never produced.
/// </summary>
public sealed record OneDragonTaskRequest
{
    internal OneDragonTaskRequest(bool closeGame, IReadOnlyList<int> instances) => (CloseGame, Instances) = (closeGame, instances);

    public bool CloseGame { get; }
    /// <summary>Account instance indices, in the order given. Empty means OneDragon's own default.</summary>
    public IReadOnlyList<int> Instances { get; }

    public IReadOnlyList<string> ToArguments()
    {
        var args = new List<string> { "-o" };
        if (CloseGame) args.Add("-c");
        if (Instances.Count > 0)
        {
            args.Add("-i");
            args.Add(string.Join(',', Instances.Select(i => i.ToString(CultureInfo.InvariantCulture))));
        }
        return args;
    }

    /// <summary>The list OneDragon logs back as "指定运行实例: [1, 2]" (Python list repr), when instances were requested.</summary>
    internal string? ExpectedInstanceEcho =>
        Instances.Count == 0 ? null : "[" + string.Join(", ", Instances.Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]";
}

public sealed record OneDragonArgumentResult(OneDragonTaskRequest? Request, string? Error)
{
    public bool IsValid => Request is not null;
}

public static class OneDragonArguments
{
    public const int MaxInstances = 8;
    public const int MaxInstanceIndex = 99;

    public static OneDragonArgumentResult OneDragon(bool closeGame = false, IReadOnlyList<int>? instances = null)
    {
        instances ??= [];
        if (instances.Count > MaxInstances) return new(null, $"最多指定 {MaxInstances} 个账号实例。");
        if (instances.Any(i => i is < 0 or > MaxInstanceIndex)) return new(null, $"账号实例编号必须在 0–{MaxInstanceIndex} 之间。");
        if (instances.Distinct().Count() != instances.Count) return new(null, "账号实例编号不能重复。");
        return new(new OneDragonTaskRequest(closeGame, [.. instances]), null);
    }

    /// <summary>Parses user text such as "1,2" with the same strictness: digits and commas only, no spaces or signs.</summary>
    public static OneDragonArgumentResult Parse(bool closeGame, string? instanceText)
    {
        if (string.IsNullOrEmpty(instanceText)) return OneDragon(closeGame);
        if (instanceText.Length > 64) return new(null, "账号实例列表过长。");
        var parts = instanceText.Split(',');
        var values = new List<int>();
        foreach (var part in parts)
        {
            if (part.Length is 0 or > 2 || !part.All(char.IsAsciiDigit)) return new(null, $"无效的账号实例编号：“{part}”。只允许以英文逗号分隔的数字。");
            values.Add(int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture));
        }
        return OneDragon(closeGame, values);
    }
}
