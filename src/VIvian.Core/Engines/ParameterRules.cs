using System.Globalization;

namespace VIvian.Core.Engines;

/// <summary>
/// Type, range and cross-field rules for registered parameters. One implementation shared by the WPF client,
/// configuration validation and (later) the AI tools, so all three accept and reject exactly the same values.
/// DataType: boolean · integer (AllowedValues "min-max") · enum (AllowedValues "a|b|c") · text (free single-line).
/// </summary>
public sealed record ParameterOption(string Value, string Label);

public static class ParameterRules
{
    public static bool IsScalar(EngineParameterDefinition p) => p.DataType is "boolean" or "integer" or "enum" or "text";

    public static IReadOnlyList<string> Options(EngineParameterDefinition p) => p.DataType switch
    {
        "boolean" => ["true", "false"],
        "enum" => p.AllowedValues.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(o => o.Split('=', 2)[0]).ToArray(),
        _ => [],
    };

    /// <summary>Value plus a readable label ("回忆之蕾（角色经验）") for choice lists. Option encoding: "value=purpose".</summary>
    public static IReadOnlyList<ParameterOption> LabelledOptions(EngineParameterDefinition p) => p.DataType switch
    {
        "boolean" => [new("true", "开启"), new("false", "关闭")],
        "enum" => p.AllowedValues.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(o => o.Split('=', 2)).Select(x => new ParameterOption(x[0],
                x.Length == 2 && x[1].Length > 0 ? $"{x[0]}（{x[1]}）" : x[0])).ToArray(),
        _ => [],
    };

    public static string Normalize(EngineParameterDefinition p, string raw)
    {
        var value = raw.Trim();
        return p.DataType == "boolean" ? value.ToLowerInvariant() : value;
    }

    public static bool IsValid(EngineParameterDefinition p, string value)
    {
        switch (p.DataType)
        {
            case "boolean": return value is "true" or "false";
            case "enum": return Options(p).Contains(value, StringComparer.Ordinal);
            case "integer":
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return false;
                var range = p.AllowedValues.Split('-');
                return range.Length == 2 && int.TryParse(range[0], out var min) && int.TryParse(range[1], out var max)
                    && n >= min && n <= max;
            case "text":
                return value.Length is > 0 and <= 40 && value == value.Trim() && !value.Any(c => char.IsControl(c) ||
                    c is '"' or '\'' or '#' or ':' or '\\' or '[' or ']' or '{' or '}' or '|' or ',' or '&' or '*' or '!' or '%' or '@' or '`');
            default: return false;
        }
    }

    public static string Describe(EngineParameterDefinition p) => p.DataType switch
    {
        "boolean" => "只能是 true 或 false",
        "integer" => "整数，范围 " + p.AllowedValues.Replace("-", " 至 ", StringComparison.Ordinal),
        "enum" => "只能是：" + string.Join("、", Options(p)),
        "text" => "1–40 个字的单行名称，不能含引号、#、冒号等符号",
        _ => "该参数不支持在 VIvian 中修改",
    };

    /// <summary>Value after the proposal is applied (or the current value when unchanged).</summary>
    public static string? ValueAfter(ConfigurationProposal proposal, string path) =>
        proposal.Changes.FirstOrDefault(c => c.Path == path)?.NewValue
        ?? proposal.Base.Values.FirstOrDefault(v => v.Schema.Path == path)?.Value;

    /// <summary>Changes that spend resources or destroy items; always confirmed by the user even with auto-save.</summary>
    public static bool RequiresConfirmation(ConfigurationChange change) =>
        change.NewValue == "true" && change.Path is "use_fuel" or "use_reserved_trailblaze_power" or "break_down_level_four_relicset";

    /// <summary>Contradictions that would make the saved configuration fail at run time. They block saving.</summary>
    public static IReadOnlyList<string> Conflicts(ConfigurationProposal proposal)
    {
        var result = new List<string>();
        if (proposal.Base.Game != GameId.StarRail) return result;
        var type = ValueAfter(proposal, "instance_type");
        if (type is not null && proposal.Base.Values.Any(v => v.Schema.Path == "instance_names." + type))
        {
            var name = ValueAfter(proposal, "instance_names." + type);
            if (name is null or "无")
                result.Add($"清体力副本类型选为“{type}”，但该类型的副本名称为“{name ?? "未设置"}”，上游会跳过清体力。请先填写副本名称");
        }
        return result;
    }

    /// <summary>Settings that are valid but will not have the effect a user probably expects. Shown, never blocking.</summary>
    public static IReadOnlyList<string> Warnings(ConfigurationProposal proposal)
    {
        var result = new List<string>();
        if (proposal.Base.Game != GameId.StarRail) return result;
        bool On(string path) => ValueAfter(proposal, path) == "true";
        bool Known(string path) => ValueAfter(proposal, path) is not null;
        if (Known("power_enable") && !On("power_enable"))
        {
            if (On("echo_of_war_enable")) result.Add("清体力总开关关闭时，日常中的历战余响也会跳过（单独执行“清体力”不受影响）");
            result.Add("清体力关闭后，“日常”不会消耗开拓力，每日实训通常无法达到 500 分");
        }
        if (Known("reward_enable") && !On("reward_enable")) result.Add("领取奖励关闭后，VIvian 无法核验每日实训是否完成");
        if (Known("daily_enable") && !On("daily_enable")) result.Add("每日实训关闭后，“日常”只清体力，不做实训");
        if (On("use_fuel") || On("use_reserved_trailblaze_power")) result.Add("将消耗燃料或后备开拓力，属于不可退回的资源消耗");
        return result;
    }
}
