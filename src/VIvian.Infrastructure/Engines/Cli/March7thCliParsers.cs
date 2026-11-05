using System.Text.Json;
using System.Text.RegularExpressions;

namespace VIvian.Infrastructure.Engines.Cli;

public enum ParseState
{
    /// <summary>The output matches the documented shape. The shape itself is still unverified on the real release.</summary>
    Parsed,
    /// <summary>The output does not match any shape VIvian knows; nothing is inferred from it.</summary>
    Unsupported,
    Empty,
}

public sealed record ParsedVersion(ParseState State, string? Version, string Detail);

/// <summary>Fields named by the upstream website (level C). Unknown fields are listed, never interpreted.</summary>
public sealed record ParsedStatus(ParseState State, bool? FirstRunCompleted, bool? GamePathExists, bool? ActiveRunPresent,
    IReadOnlyList<string> UnknownFields, string Detail);

public sealed record ParsedTaskList(ParseState State, string? TopLevelKind, int? ItemCount, string Detail);

/// <summary>
/// Parsers for March7thAssistant v2026.10.31 diagnostic output. Schemas come from pinned source and documentation, not
/// from the real executable: every result is labelled unverified, and anything unexpected is Unsupported.
/// </summary>
public static partial class March7thCliParsers
{
    public const string SchemaNote = "结构来自源码/文档（级别 B/C），尚未用真实 v2026.10.31 程序确认。";

    [GeneratedRegex(@"^v?(\d{4}\.\d{1,2}\.\d{1,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseTag();

    public static ParsedVersion ParseVersion(IReadOnlyList<string> lines)
    {
        var meaningful = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (meaningful.Count == 0) return new(ParseState.Empty, null, "没有输出。");
        var tags = meaningful.Select(l => ReleaseTag().Match(l)).Where(m => m.Success).Select(m => "v" + m.Groups[1].Value).Distinct().ToList();
        return tags.Count == 1
            ? new(ParseState.Parsed, tags[0], $"版本行：{tags[0]}。{SchemaNote}")
            : new(ParseState.Unsupported, null, tags.Count == 0 ? "输出中没有可识别的版本行。" : "输出中有多个不同的版本行，结果不确定。");
    }

    private static readonly string[] StatusFields = ["first_run_completed", "game_path_exists", "active_run"];

    public static ParsedStatus ParseStatus(IReadOnlyList<string> lines)
    {
        var text = string.Join('\n', lines).Trim();
        if (text.Length == 0) return new(ParseState.Empty, null, null, null, [], "没有输出。");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Unsupported("顶层不是 JSON 对象。");
            if (!root.TryGetProperty("first_run_completed", out var first) || first.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("game_path_exists", out var game) || game.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("active_run", out var active) || active.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
                return Unsupported("缺少文档中的字段或字段类型不符。");
            var unknown = root.EnumerateObject().Select(p => p.Name).Where(n => !StatusFields.Contains(n)).Order(StringComparer.Ordinal).ToList();
            return new(ParseState.Parsed, first.GetBoolean(), game.GetBoolean(), active.ValueKind == JsonValueKind.Object, unknown,
                $"已识别文档字段；另有 {unknown.Count} 个未解释的字段。{SchemaNote}");
        }
        catch (JsonException)
        {
            return Unsupported("输出不是有效的 JSON。");
        }

        static ParsedStatus Unsupported(string why) => new(ParseState.Unsupported, null, null, null, [], why);
    }

    /// <summary>The task list JSON shape is not substantiated: only its top-level kind and size are recorded.</summary>
    public static ParsedTaskList ParseTaskList(IReadOnlyList<string> lines)
    {
        var text = string.Join('\n', lines).Trim();
        if (text.Length == 0) return new(ParseState.Empty, null, null, "没有输出。");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            var (kind, count) = root.ValueKind switch
            {
                JsonValueKind.Array => ("array", root.GetArrayLength()),
                JsonValueKind.Object => ("object", root.EnumerateObject().Count()),
                _ => (root.ValueKind.ToString().ToLowerInvariant(), 0),
            };
            return new(ParseState.Unsupported, kind, count,
                $"是有效 JSON（顶层 {kind}，{count} 项），但任务列表结构未经证实，VIvian 不从中提取任务标识。");
        }
        catch (JsonException)
        {
            return new(ParseState.Unsupported, null, null, "输出不是有效的 JSON。");
        }
    }
}
