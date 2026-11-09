using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using VIvian.Core.Engines;

namespace VIvian.Infrastructure.Engines.BetterGi;

/// <summary>Parameters that can be saved in VIvian for one Genshin account before desktop acceptance.</summary>
public static class BetterGiAccountPlans
{
    private const string TaskSource = "BetterGI 0.66.0 OneDragonFlowViewModel / AutoDomainParam";
    private const string CurrentParty = "当前队伍";
    private const string NoDomain = "未设置";
    internal const string Daily = "dailyRewards";
    internal const string Craft = "craftCondensedResin";
    internal const string Domain = "autoDomain";
    internal const string Original20 = "autoDomainConfig.originalResin20UseCount";
    internal const string Original40 = "autoDomainConfig.originalResin40UseCount";
    internal const string Condensed = "autoDomainConfig.condensedResinUseCount";

    public static IReadOnlyList<EngineParameterDefinition> Parameters { get; } = Array.AsReadOnly<EngineParameterDefinition>(
    [
        Field(Daily, "领取每日奖励", "boolean", "true|false", "false", "每日奖励"),
        Field("AdventurersGuildCountry", "冒险家协会地区", "enum", "枫丹|蒙德|璃月|稻妻|挪德卡莱", "枫丹", "每日奖励"),
        Field("DailyRewardPartyName", "领奖队伍", "text", "", CurrentParty, "每日奖励"),
        Field(Craft, "合成浓缩树脂", "boolean", "true|false", "false", "合成树脂"),
        Field("CraftingBenchCountry", "合成台地区", "enum", "枫丹|蒙德|璃月|稻妻", "枫丹", "合成树脂"),
        Field("MinResinToKeep", "保留原粹树脂", "integer", "0-999", "0", "合成树脂"),
        Field(Domain, "自动秘境", "boolean", "true|false", "false", "秘境"),
        Field("DomainName", "秘境名称", "text", "", NoDomain, "秘境"),
        Field("PartyName", "秘境队伍", "text", "", CurrentParty, "秘境"),
        Field(Original20, "20 原粹次数", "integer", "0-99", "0", "秘境"),
        Field(Original40, "40 原粹次数", "integer", "0-99", "0", "秘境"),
        Field(Condensed, "浓缩树脂次数", "integer", "0-99", "0", "秘境"),
    ]);

    private static readonly IReadOnlyDictionary<string, EngineParameterDefinition> ByPath =
        Parameters.ToDictionary(parameter => parameter.Path, StringComparer.Ordinal);

    public static BetterGiAccountPlan Compile(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var values = Parameters.ToDictionary(parameter => parameter.Path, parameter => parameter.DefaultValue, StringComparer.Ordinal);
        foreach (var (key, raw) in settings)
        {
            if (!ByPath.TryGetValue(key, out var parameter) || raw is null)
                throw new ArgumentException("原神账号任务包含无法识别的设置。", nameof(settings));
            var value = ParameterRules.Normalize(parameter, raw);
            if (!ParameterRules.IsValid(parameter, value))
                throw new ArgumentException($"“{parameter.Title}”的设置无效。{ParameterRules.Describe(parameter)}。", nameof(settings));
            values[key] = value;
        }

        if (values[Domain] == "true")
        {
            if (values["DomainName"] == NoDomain)
                throw new ArgumentException("启用自动秘境前，请填写 BetterGI 中显示的秘境名称。", nameof(settings));
            if (Count(values, Original20) + Count(values, Original40) + Count(values, Condensed) == 0)
                throw new ArgumentException("启用自动秘境前，请为至少一种树脂设置明确次数。", nameof(settings));
        }

        return new BetterGiAccountPlan(values);
    }

    internal static int Count(IReadOnlyDictionary<string, string> values, string key) =>
        int.Parse(values[key], NumberStyles.None, CultureInfo.InvariantCulture);

    internal static string Party(string value) => value == CurrentParty ? "" : value;

    private static EngineParameterDefinition Field(string path, string title, string type, string allowed, string defaultValue, string group) =>
        new(path, title, "保存到当前 VIvian 原神账号。", type, allowed, defaultValue, "原神账号", "", "", "VIvian 账号设置",
            ParameterAccess.VerifiedReadWrite, TaskSource, group);
}

/// <summary>A validated snapshot of one account's selections. It can render an isolated BetterGI profile.</summary>
public sealed class BetterGiAccountPlan
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly string[] ClientNames = ["YuanShen.exe", "GenshinImpact.exe"];
    private static readonly (string Setting, string Task)[] Order =
    [
        (BetterGiAccountPlans.Craft, "genshin.craft_condensed_resin"),
        (BetterGiAccountPlans.Domain, "genshin.domain"),
        (BetterGiAccountPlans.Daily, "genshin.daily_rewards"),
    ];

    internal BetterGiAccountPlan(Dictionary<string, string> settings)
    {
        Values = new ReadOnlyDictionary<string, string>(settings);
        Tasks = Order.Where(entry => Enabled(entry.Setting))
            .Select(entry => BetterGiTaskCatalog.Tasks.Single(task => task.Id == entry.Task)).ToList().AsReadOnly();
    }

    public IReadOnlyDictionary<string, string> Values { get; }
    public IReadOnlyList<EngineTaskDefinition> Tasks { get; }

    public BetterGiPlanFiles Render(Guid accountId, string gameExecutable)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("需要原神账号的内部档案编号。", nameof(accountId));
        if (!Path.IsPathFullyQualified(gameExecutable) || !ClientNames.Contains(Path.GetFileName(gameExecutable), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("需要原神客户端的完整路径。", nameof(gameExecutable));
        if (Tasks.Count == 0)
            throw new InvalidOperationException("此账号没有已启用的原神任务。");

        var name = $"VIvian {accountId:N}";
        var domain = Enabled(BetterGiAccountPlans.Domain);
        var profile = new
        {
            Name = name,
            TaskEnabledList = Tasks.ToDictionary(task => task.Id, _ => true),
            TaskOrder = Tasks.Select(task => task.Id).ToArray(),
            TaskDefinitions = Tasks.ToDictionary(task => task.Id, task => task.Title),
            AdventurersGuildCountry = Values["AdventurersGuildCountry"],
            DailyRewardPartyName = BetterGiAccountPlans.Party(Values["DailyRewardPartyName"]),
            CraftingBenchCountry = Values["CraftingBenchCountry"],
            MinResinToKeep = BetterGiAccountPlans.Count(Values, "MinResinToKeep"),
            DomainName = domain ? Values["DomainName"] : "",
            PartyName = BetterGiAccountPlans.Party(Values["PartyName"]),
            WeeklyDomainEnabled = false,
            CompletionAction = "关闭软件",
        };
        var settings = new
        {
            selectedOneDragonFlowConfigName = name,
            commonConfig = new { runForVersion = BetterGiContract.PinnedTag, isFirstRun = false, exitToTray = false },
            genshinStartConfig = new
            {
                installPath = Path.GetFullPath(gameExecutable),
                linkedStartEnabled = false, startGameWithCmd = false, autoEnterGameEnabled = false, autoDisableGenshinHdrEnabled = false,
            },
            scriptConfig = new { autoUpdateSubscribedScripts = false, autoUpdateBeforeCommandLineRun = false },
            notificationConfig = new { dragonEndSummaryEnabled = false },
            autoDomainConfig = new
            {
                specifyResinUse = domain,
                originalResinUseCount = 0,
                originalResin20UseCount = CountWhenDomain(BetterGiAccountPlans.Original20),
                originalResin40UseCount = CountWhenDomain(BetterGiAccountPlans.Original40),
                condensedResinUseCount = CountWhenDomain(BetterGiAccountPlans.Condensed),
                transientResinUseCount = 0, fragileResinUseCount = 0,
                autoArtifactSalvage = false, rewardRecognitionEnabled = domain,
            },
        };
        return new(name, JsonSerializer.Serialize(profile, Json), JsonSerializer.Serialize(settings, Json));

        int CountWhenDomain(string key) => domain ? BetterGiAccountPlans.Count(Values, key) : 0;
    }

    private bool Enabled(string setting) => Values[setting] == "true";
}

public sealed record BetterGiPlanFiles(string ProfileName, string ProfileJson, string SettingsJson);
