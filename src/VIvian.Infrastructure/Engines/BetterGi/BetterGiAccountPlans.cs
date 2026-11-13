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
    internal const string NoDomain = "未设置";
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
            if (values["DomainName"] is (NoDomain or "根据提升指南选择秘境"))
                throw new ArgumentException("启用自动秘境前，请填写 BetterGI 中显示的秘境名称。", nameof(settings));
            if (Count(values, Original20) + Count(values, Original40) + Count(values, Condensed) == 0)
                throw new ArgumentException("启用自动秘境前，请为至少一种树脂设置明确次数。", nameof(settings));
        }

        return new BetterGiAccountPlan(values);
    }

    public static BetterGiPlanFiles RenderDailyAcceptance(Guid accountId, BetterGiOneDragonPlan source, string gameExecutable)
        => RenderDaily(accountId, source, gameExecutable, craft: false);

    internal static BetterGiPlanFiles RenderSavedRun(Guid accountId, BetterGiOneDragonPlan source, string gameExecutable)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.EnabledTasks.SequenceEqual([BetterGiMarkers.DailyRewardTask]))
            return RenderDaily(accountId, source, gameExecutable, craft: false);
        if (!source.EnabledTasks.SequenceEqual([BetterGiMarkers.CraftResinTask, BetterGiMarkers.DailyRewardTask]))
            throw new InvalidOperationException("源档案仅支持领取每日奖励，或按顺序执行合成浓缩树脂后领取每日奖励。");
        if (source.MinResinToKeep != 0 || !ParameterRules.IsValid(ByPath["CraftingBenchCountry"], source.CraftingBenchCountry))
            throw new InvalidOperationException("当前合成核验只支持保留原粹树脂为 0 且有已知合成地区的档案。");
        return RenderDaily(accountId, source, gameExecutable, craft: true);
    }

    internal static BetterGiPlanFiles RebuildSavedRun(Guid accountId, string gameExecutable, JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object || !profile.TryGetProperty("TaskEnabledList", out var enabled) ||
            enabled.ValueKind != JsonValueKind.Object || !profile.TryGetProperty("MinResinToKeep", out var reserve) ||
            !reserve.TryGetInt32(out var minimum))
            throw new InvalidDataException("隔离任务设置缺少任务列表或树脂保留值。");
        var craft = enabled.TryGetProperty("genshin.craft_condensed_resin", out var crafting) && crafting.GetBoolean();
        var source = new BetterGiOneDragonPlan(BetterGiAccountPlan.SourceProfileName(accountId), craft
            ? [BetterGiMarkers.CraftResinTask, BetterGiMarkers.DailyRewardTask] : [BetterGiMarkers.DailyRewardTask],
            "关闭软件", BetterGiConfiguration.ReadString(profile, "CraftingBenchCountry") ?? "",
            minimum, "", "", false,
            BetterGiConfiguration.ReadString(profile, "AdventurersGuildCountry") ?? "",
            BetterGiConfiguration.ReadString(profile, "DailyRewardPartyName") ?? "");
        return RenderSavedRun(accountId, source, gameExecutable);
    }

    private static BetterGiPlanFiles RenderDaily(Guid accountId, BetterGiOneDragonPlan source, string gameExecutable, bool craft)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Name != BetterGiAccountPlan.SourceProfileName(accountId))
            throw new InvalidOperationException("日常验收需要当前账号已绑定的 BetterGI 源档案。");
        if (!source.EnabledTasks.Contains(BetterGiMarkers.DailyRewardTask, StringComparer.Ordinal))
            throw new InvalidOperationException("当前 BetterGI 源档案未启用领取每日奖励，无法运行日常验收。");
        if (!ParameterRules.IsValid(ByPath["AdventurersGuildCountry"], source.AdventurersGuildCountry)
            || (source.DailyRewardPartyName.Length != 0
                && !ParameterRules.IsValid(ByPath["DailyRewardPartyName"], source.DailyRewardPartyName)))
            throw new InvalidOperationException("当前 BetterGI 源档案的领奖地区或队伍设置无效。");

        var values = new Dictionary<string, string>
        {
            [Daily] = "true",
            ["AdventurersGuildCountry"] = source.AdventurersGuildCountry,
        };
        if (craft)
        {
            values[Craft] = "true";
            values["CraftingBenchCountry"] = source.CraftingBenchCountry;
        }
        // The parsed file's party name is literal. Only a saved VIvian setting interprets “当前队伍” as empty.
        return Compile(values).Render(accountId, gameExecutable, source.DailyRewardPartyName);
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

    public BetterGiPlanFiles Render(Guid accountId, string gameExecutable) => Render(accountId, gameExecutable, null);

    internal BetterGiPlanFiles Render(Guid accountId, string gameExecutable, string? dailyRewardPartyName)
    {
        var name = SourceProfileName(accountId);
        if (!Path.IsPathFullyQualified(gameExecutable) || !ClientNames.Contains(Path.GetFileName(gameExecutable), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("需要原神客户端的完整路径。", nameof(gameExecutable));
        if (Tasks.Count == 0)
            throw new InvalidOperationException("此账号没有已启用的原神任务。");

        var domain = Enabled(BetterGiAccountPlans.Domain);
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
        return new(name, RenderProfile(name, includeDisabled: false, dailyRewardPartyName), JsonSerializer.Serialize(settings, Json))
        {
            RequiresStockCraft = Enabled(BetterGiAccountPlans.Craft),
        };

        int CountWhenDomain(string key) => domain ? BetterGiAccountPlans.Count(Values, key) : 0;
    }

    internal static string SourceProfileName(Guid accountId)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("需要原神账号的内部档案编号。", nameof(accountId));
        return $"VIvian {accountId:N}";
    }

    internal string RenderSourceProfile(Guid accountId) => RenderProfile(SourceProfileName(accountId), includeDisabled: true);

    private string RenderProfile(string name, bool includeDisabled, string? dailyRewardPartyName = null)
    {
        var tasks = Order.Where(entry => includeDisabled || Enabled(entry.Setting)).ToArray();
        var domainName = Values["DomainName"];
        return JsonSerializer.Serialize(new
        {
            Name = name,
            TaskEnabledList = tasks.ToDictionary(entry => entry.Task, entry => Enabled(entry.Setting)),
            TaskOrder = tasks.Select(entry => entry.Task).ToArray(),
            TaskDefinitions = tasks.ToDictionary(entry => entry.Task,
                entry => BetterGiTaskCatalog.Tasks.Single(task => task.Id == entry.Task).Title),
            AdventurersGuildCountry = Values["AdventurersGuildCountry"],
            DailyRewardPartyName = dailyRewardPartyName ?? BetterGiAccountPlans.Party(Values["DailyRewardPartyName"]),
            CraftingBenchCountry = Values["CraftingBenchCountry"],
            MinResinToKeep = BetterGiAccountPlans.Count(Values, "MinResinToKeep"),
            DomainName = (includeDisabled || Enabled(BetterGiAccountPlans.Domain)) &&
                domainName != BetterGiAccountPlans.NoDomain ? domainName : "",
            PartyName = BetterGiAccountPlans.Party(Values["PartyName"]),
            WeeklyDomainEnabled = false,
            CompletionAction = includeDisabled ? "无" : "关闭软件",
        }, Json);
    }

    private bool Enabled(string setting) => Values[setting] == "true";
}

public sealed record BetterGiPlanFiles(string ProfileName, string ProfileJson, string SettingsJson)
{
    internal bool RequiresStockCraft { get; init; }
}
