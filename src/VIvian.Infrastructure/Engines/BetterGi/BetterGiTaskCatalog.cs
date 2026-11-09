using VIvian.Core.Engines;

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>Source-backed BetterGI 0.66.0 tasks for capability queries. Each task still requires desktop acceptance.</summary>
public static class BetterGiTaskCatalog
{
    private const string Profile = "User/OneDragon/<profile>.json";
    private const string Global = "User/config.json";
    private const string TaskSource = "BetterGI 0.66.0 Model/OneDragonTaskItem.cs";
    private const string DomainSource = "BetterGI 0.66.0 GameTask/AutoDomain/AutoDomainConfig.cs";

    public static IReadOnlyList<EngineTaskDefinition> Tasks { get; } = Array.AsReadOnly<EngineTaskDefinition>(
    [
        new("genshin.daily_rewards", "领取每日奖励", CapabilityStatus.Pending, EvidenceLevel.B, TaskSource,
            "前往冒险家协会领奖并尝试领取纪行奖励。核查日志只能证明每日委托奖励状态。",
            TaskFrequency.Daily, "startOneDragon <配置名>", "归属本次运行的每日奖励核查、流程结束与清理",
            ParameterPaths: ["AdventurersGuildCountry", "DailyRewardPartyName", "CompletionAction"]),
        new("genshin.craft_condensed_resin", "合成树脂", CapabilityStatus.Pending, EvidenceLevel.B, TaskSource,
            "按一条龙的合成地点及保留量处理原粹树脂。",
            Invocation: "startOneDragon <配置名>", CompletionSignal: "合成结果待实机校准",
            ParameterPaths: ["CraftingBenchCountry", "MinResinToKeep", "CompletionAction"]),
        new("genshin.domain", "自动秘境", CapabilityStatus.Pending, EvidenceLevel.B, TaskSource,
            "一条龙提供秘境和队伍，全局秘境设置提供树脂策略。",
            Invocation: "startOneDragon <配置名>", CompletionSignal: "秘境轮次与领奖证据待实机校准",
            ParameterPaths: ["DomainName", "PartyName", "WeeklyDomainEnabled", "autoDomainConfig.specifyResinUse",
                "autoDomainConfig.resinPriorityList", "autoDomainConfig.originalResinUseCount", "autoDomainConfig.originalResin20UseCount", "autoDomainConfig.originalResin40UseCount",
                "autoDomainConfig.condensedResinUseCount", "autoDomainConfig.transientResinUseCount",
                "autoDomainConfig.fragileResinUseCount", "autoDomainConfig.autoArtifactSalvage", "CompletionAction"]),
    ]);

    public static IReadOnlyList<EngineParameterDefinition> Parameters { get; } = Array.AsReadOnly<EngineParameterDefinition>(
    [
        Field("AdventurersGuildCountry", "冒险家协会国家", "string", "上游配置界面可选", "枫丹"),
        Field("DailyRewardPartyName", "每日奖励队伍", "string", "空或游戏内队伍名", ""),
        Field("CraftingBenchCountry", "合成台国家", "string", "上游配置界面可选", "枫丹"),
        Field("MinResinToKeep", "保留原粹树脂", "integer", "非负整数", "0"),
        Field("DomainName", "秘境", "string", "上游配置界面可选", ""),
        Field("PartyName", "秘境队伍", "string", "空或游戏内队伍名", ""),
        Field("WeeklyDomainEnabled", "按星期配置秘境", "boolean", "true|false", "false"),
        Field("CompletionAction", "结束后动作", "enum", "无|关闭游戏|关闭软件|关闭游戏和软件", "无"),
        Field("autoDomainConfig.specifyResinUse", "指定树脂次数", "boolean", "true|false", "false", Global),
        Field("autoDomainConfig.resinPriorityList", "树脂优先序", "list", "上游秘境设置可选", "浓缩树脂,原粹树脂", Global),
        Field("autoDomainConfig.originalResinUseCount", "原粹树脂次数（旧字段）", "integer", "非负整数", "0", Global),
        Field("autoDomainConfig.originalResin20UseCount", "20 原粹次数", "integer", "非负整数", "0", Global),
        Field("autoDomainConfig.originalResin40UseCount", "40 原粹次数", "integer", "非负整数", "0", Global),
        Field("autoDomainConfig.condensedResinUseCount", "浓缩树脂次数", "integer", "非负整数", "0", Global),
        Field("autoDomainConfig.transientResinUseCount", "须臾树脂次数", "integer", "非负整数", "0", Global),
        Field("autoDomainConfig.autoArtifactSalvage", "秘境后分解圣遗物", "boolean", "true|false", "false", Global),
        Field("autoDomainConfig.fragileResinUseCount", "脆弱树脂次数", "integer", "非负整数", "0", Global),
    ]);

    private static EngineParameterDefinition Field(string path, string title, string type, string values, string defaultValue,
        string config = Profile) =>
        new(path, title, "固定版本定义，写入待验收", type, values, defaultValue, "原神", "", "", config,
            ParameterAccess.UpstreamManaged, config == Profile ? TaskSource : DomainSource);
}
