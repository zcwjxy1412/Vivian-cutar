namespace VIvian.Infrastructure.Engines.BetterGi;

public enum BetterGiTaskKind
{
    /// <summary><c>BetterGI.exe startOneDragon</c>: runs the one-dragon configuration currently selected in BetterGI.</summary>
    OneDragonSelected,
    /// <summary><c>BetterGI.exe startOneDragon &lt;name&gt;</c>: selects the named configuration first.</summary>
    OneDragonNamed,
    /// <summary><c>BetterGI.exe --startGroups &lt;g1&gt; …</c>: runs script groups. Its completion signals are not known.</summary>
    StartGroups,
}

/// <summary>
/// A validated BetterGI task request. Only built through <see cref="BetterGiArguments"/>, so every instance has passed
/// the allowlist. Construction never touches the installation or BetterGI's configuration.
/// </summary>
public sealed record BetterGiTaskRequest
{
    internal BetterGiTaskRequest(BetterGiTaskKind kind, string? configName, IReadOnlyList<string> groups)
        => (Kind, ConfigName, Groups) = (kind, configName, groups);

    public BetterGiTaskKind Kind { get; }
    public string? ConfigName { get; }
    public IReadOnlyList<string> Groups { get; }

    /// <summary>
    /// True when VIvian knows which log lines prove this kind of run completed. Only the one-dragon flow has a known
    /// completion line in the pinned source; a script-group run can never be reported successful.
    /// </summary>
    public bool HasKnownCompletionContract => Kind != BetterGiTaskKind.StartGroups;

    /// <summary>The argument vector passed to BetterGI.exe (each element is one argv entry; nothing goes through a shell).</summary>
    public IReadOnlyList<string> ToArguments() => Kind switch
    {
        BetterGiTaskKind.OneDragonSelected => ["startOneDragon"],
        BetterGiTaskKind.OneDragonNamed => ["startOneDragon", ConfigName!],
        BetterGiTaskKind.StartGroups => ["--startGroups", .. Groups],
        _ => throw new InvalidOperationException("未知的 BetterGI 任务类型。"),
    };
}

public sealed record BetterGiArgumentResult(BetterGiTaskRequest? Request, string? Error)
{
    public bool IsValid => Request is not null;
}

/// <summary>
/// Builds BetterGI command lines from a fixed allowlist, checked against the pinned parser
/// (BetterGenshinImpact/Helpers/CommandLineOptions.cs @0af68c2, level B):
/// <list type="bullet">
/// <item>The parser trims every argument and drops <c>--instance</c> / <c>--restart-from-pid</c> and their value
/// wherever they appear, so names with surrounding whitespace or starting with '-' could change the meaning.</item>
/// <item>Only the first argument after <c>startOneDragon</c> is used as the configuration name; it is matched
/// ordinally. If no configuration matches, BetterGI logs a warning and runs the currently selected one, which the
/// evidence rules treat as a failure.</item>
/// <item>Configuration and group names become file names (User\OneDragon\*.json, User\ScriptGroup\*.json) and pass
/// through Microsoft.Extensions.Logging message templates, so path, wildcard and brace characters are refused.</item>
/// </list>
/// </summary>
public static class BetterGiArguments
{
    public const int MaxNameLength = 64;
    public const int MaxGroups = 16;

    private static readonly char[] Forbidden = ['<', '>', ':', '"', '/', '\\', '|', '?', '*', '{', '}', '%', '\'', '`', '　'];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static BetterGiArgumentResult OneDragonSelected() =>
        new(new BetterGiTaskRequest(BetterGiTaskKind.OneDragonSelected, null, []), null);

    public static BetterGiArgumentResult OneDragon(string? configName)
    {
        if (configName is null) return OneDragonSelected();
        var error = ValidateName(configName, "一条龙配置名");
        return error is null
            ? new(new BetterGiTaskRequest(BetterGiTaskKind.OneDragonNamed, configName, []), null)
            : new(null, error);
    }

    public static BetterGiArgumentResult StartGroups(IReadOnlyList<string>? groups)
    {
        if (groups is null || groups.Count == 0) return new(null, "至少需要一个配置组名。");
        if (groups.Count > MaxGroups) return new(null, $"配置组最多 {MaxGroups} 个。");
        foreach (var group in groups)
        {
            var error = ValidateName(group, "配置组名");
            if (error is not null) return new(null, error);
        }
        if (groups.Distinct(StringComparer.Ordinal).Count() != groups.Count) return new(null, "配置组名不能重复。");
        return new(new BetterGiTaskRequest(BetterGiTaskKind.StartGroups, null, [.. groups]), null);
    }

    /// <summary>Returns null when the name is acceptable, otherwise a user-facing reason.</summary>
    public static string? ValidateName(string? name, string label = "名称")
    {
        if (string.IsNullOrEmpty(name)) return $"{label}不能为空。";
        if (name.Length > MaxNameLength) return $"{label}不能超过 {MaxNameLength} 个字符。";
        if (name.Trim() != name) return $"{label}首尾不能有空白（BetterGI 会自动去除，导致匹配到其他配置）。";
        if (name.StartsWith('-')) return $"{label}不能以“-”开头（可能被当作 BetterGI 的命令行选项）。";
        if (name.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator))
            return $"{label}不能包含控制字符或不可见格式字符。";
        if (name.IndexOfAny(Forbidden) >= 0) return $"{label}包含不允许的字符（路径、通配符、引号或花括号）。";
        if (name.EndsWith('.') || name == "." || name.Contains("..", StringComparison.Ordinal))
            return $"{label}不能以“.”结尾或包含“..”。";
        var stem = name.Split('.')[0];
        if (ReservedDeviceNames.Contains(stem)) return $"{label}是 Windows 保留设备名。";
        if (name.Contains("startOneDragon", StringComparison.OrdinalIgnoreCase)
            || name.Equals("--startGroups", StringComparison.OrdinalIgnoreCase)
            || name.Equals("--TaskProgress", StringComparison.OrdinalIgnoreCase))
            return $"{label}不能与 BetterGI 的命令关键字相同。";
        return null;
    }
}
