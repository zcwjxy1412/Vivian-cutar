using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Assistant;
using VIvian.Infrastructure.Engines.BetterGi;
using VIvian.Infrastructure.Engines.OneDragon;

namespace VIvian.Infrastructure;

/// <summary>Read-only preparation shared by confirmation and the check under the batch's desktop lease.</summary>
public sealed class SavedExecutionPlanReader(LocalDatabase database, IConfigurationControl configuration)
{
    public async Task<ExecutionPlanItem> ReadAsync(ExecutionPlanRequestItem request, int ordinal,
        string installRoot, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Game) || request.AccountId == Guid.Empty || ordinal < 0 || string.IsNullOrWhiteSpace(installRoot))
            throw new ArgumentException("计划目标或引擎路径无效。");
        var account = (await database.LoadAccountsAsync(request.Game, ct)).SingleOrDefault(x => x.Id == request.AccountId)
            ?? throw new InvalidOperationException("计划账号不存在。");
        if (!account.Enabled || !account.AutoRun || account.IsLegacyPlaceholder)
            throw new InvalidOperationException("请勾选已登记、已启用的独立账号。");
        var taskPlan = await database.LoadAccountTaskPlanAsync(account.Id, ct);
        // account_task_plans is the Star Rail daily toggle. GI/ZZZ use their existing task providers below.
        if (request.Game == GameId.StarRail && !taskPlan.DailyEnabled)
            throw new InvalidOperationException("该账号未启用日常任务。");
        var login = (await database.LoadLoginProfilesAsync(request.Game, ct)).SingleOrDefault(x => x.AccountId == account.Id);
        if (login is null || string.IsNullOrWhiteSpace(login.NormalizedPhone))
            throw new InvalidOperationException("该账号尚未绑定登录界面标识。");
        var settings = await database.LoadAccountSettingsAsync(account.Id, ct);
        string revision; string resources;
        if (request.Game == GameId.StarRail)
        {
            var view = await new StarRailAccountConfigurationProvider(database, configuration, () => installRoot, () => false)
                .ReadAsync(new(request.Game, account.Id), ct);
            if (view.WriteUnavailableReason is not null) throw new InvalidOperationException(view.WriteUnavailableReason);
            string? Value(string key) => view.Values.SingleOrDefault(x => x.Schema.Path == key)?.Value;
            var fuel = Value("use_fuel"); var reserve = Value("use_reserved_trailblaze_power");
            if (Value("break_down_level_four_relicset") != "false" || fuel is not ("true" or "false") || reserve is not ("true" or "false"))
                throw new InvalidOperationException("请先核对燃料、后备开拓力和关闭每周遗器分解。");
            revision = view.Revision;
            resources = $"按已保存副本策略；燃料：{(fuel == "true" ? "允许" : "禁止")}；后备开拓力：{(reserve == "true" ? "允许" : "禁止")}；遗器分解：关闭。";
        }
        else if (request.Game == GameId.Genshin)
        {
            var view = await new GenshinAccountConfigurationProvider(database).ReadAsync(new(request.Game, account.Id), ct);
            var plan = BetterGiAccountPlans.Compile(settings.Values);
            if (plan.Tasks.Count == 0) throw new InvalidOperationException("原神账号没有启用任务。");
            revision = view.Revision;
            resources = string.Join("、", plan.Tasks.Select(x => x.Title)) + "；" + string.Join("；",
                view.Values.Where(x => x.Value is not null).Select(x => $"{x.Schema.Title}={x.Value}"));
        }
        else
        {
            var binding = OneDragonProfileBinding.FromSettings(settings.Values)
                ?? throw new InvalidOperationException("绝区零账号未绑定 OneDragon 实例。");
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(binding.Root)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OneDragon 实例与当前引擎路径不一致。");
            var source = await OneDragonSourceProfile.ReadAsync(binding, ct);
            if (!source.Tasks.Any(x => x.Enabled)) throw new InvalidOperationException("OneDragon 实例没有启用任务。");
            revision = source.Revision + ":" + source.GamePath + ":" + binding.Instance;
            // The task-group revision alone does not cover resource settings. Hash fixed application files too;
            // never include game_account.yml, notification credentials or raw configuration in the receipt.
            foreach (var taskId in source.Tasks.Where(x => x.Enabled).Select(x => x.Id)
                         .Concat(["coffee", "charge_plan"]).Distinct().Order(StringComparer.Ordinal))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(taskId, "^[a-z][a-z0-9_]{0,63}$"))
                    throw new InvalidOperationException("OneDragon 任务编号无法安全核验。");
                var file = Path.Combine(binding.Instance.ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
                    "one_dragon", taskId + ".yml");
                var text = await OneDragonConfigRead.ReadAsync(binding.Root, file, ct);
                revision += ":" + taskId + ":" + (text is null ? "missing" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
                if (text is null)
                {
                    var fallback = await OneDragonConfigRead.ReadAsync(binding.Root,
                        Path.Combine(binding.Instance.ToString("D2", System.Globalization.CultureInfo.InvariantCulture), taskId + ".yml"), ct);
                    revision += ":legacy:" + (fallback is null ? "missing" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fallback))));
                }
            }
            resources = string.Join("、", source.Tasks.Where(x => x.Enabled).Select(x => x.Title)) +
                "；已冻结启用任务、咖啡和体力配置版本。资源用量及购买规则沿用 OneDragon 设置，详细上限尚未解析，请先在原程序核对。";
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            account.Id, account.Revision, SettingsRevision = settings.Revision, SourceRevision = revision,
            InstallRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)), Login = login
        }))));
        return new(ordinal, request.Game, account.Id, account.Name, "routine", hash,
            request.Game == GameId.StarRail ? taskPlan.Revision : settings.Revision,
            "saved_account_settings", resources);
    }
}
