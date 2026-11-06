using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.Cli;

namespace VIvian.App;

public sealed record RealTaskAuthorization(string InstallDirectory, March7thReleaseEvidence Release,
    Func<bool> StillConfirmed, bool InstallationVerified);

/// <summary>One controlled entry for UI and later in-process AI tools; execution always uses the existing scheduler.</summary>
public interface IUnifiedTaskControl : IConfigurationControl
{
    TaskPlan CreateTaskPlan(GameId game, IReadOnlyList<string> taskIds);
    Task<QueueResult?> ExecuteTaskPlan(TaskPlan plan, RealTaskAuthorization authorization);
    Task<RunRecord?> GetRunStatus(Guid runId);
}

public sealed class UnifiedTaskControl(QueueViewModel queue, IRunHistory history, IProcessSupervisor supervisor)
    : IUnifiedTaskControl
{
    private readonly IConfigurationControl _configuration = new VersionedConfigurationControl();
    public IReadOnlyList<EngineTaskDefinition> ListTasks(GameId game) => _configuration.ListTasks(game);
    public EngineTaskDefinition GetTaskSchema(GameId game, string taskId) => _configuration.GetTaskSchema(game, taskId);
    public ConfigurationSnapshot ReadConfiguration(GameId game, string installDirectory) =>
        _configuration.ReadConfiguration(game, installDirectory);
    public ConfigurationProposal ProposeChanges(ConfigurationSnapshot snapshot, IReadOnlyDictionary<string, string> values) =>
        _configuration.ProposeChanges(snapshot, values);
    public ConfigurationValidation ValidateChanges(ConfigurationProposal proposal, bool engineRunning) =>
        _configuration.ValidateChanges(proposal, engineRunning);
    public ConfigurationApplyResult ApplyChanges(ConfigurationProposal proposal, string auditDirectory, bool engineRunning) =>
        _configuration.ApplyChanges(proposal, auditDirectory, engineRunning);

    public TaskPlan CreateTaskPlan(GameId game, IReadOnlyList<string> taskIds)
    {
        foreach (var id in taskIds) GetTaskSchema(game, id);
        return new(game, taskIds.Select(id => new TaskPlanEntry(id, true)).ToArray(), 0);
    }
    public async Task<QueueResult?> ExecuteTaskPlan(TaskPlan plan, RealTaskAuthorization authorization)
    {
        if (plan.Game != GameId.StarRail || plan.Entries.Count != 1 || !plan.Entries[0].Enabled ||
            !RealExecutionPolicy.AllowsTask(plan.Entries[0].TaskId) || !authorization.StillConfirmed() ||
            !authorization.InstallationVerified || !queue.IsIdle || queue.HasUnresolved)
            throw new InvalidOperationException("本次任务、授权、安装或清理状态未通过检查。");
        var task = plan.Entries[0].TaskId;
        March7thTaskContract.StartArguments(task);
        if (RealExecutionPolicy.IsM5DailyTest)
        {
            var config = ReadConfiguration(GameId.StarRail, authorization.InstallDirectory);
            if (config.Values.Single(v => v.Schema.Path == "pause_after_success").Value != "false" ||
                config.Values.Single(v => v.Schema.Path == "exit_after_failure").Value != "true")
                throw new InvalidOperationException("上游配置尚未准备好：请关闭成功后暂停并开启失败后退出。");
        }
        var adapter = new March7thAdapter(authorization.InstallDirectory, authorization.Release, task,
            authorization.StillConfirmed, supervisor);
        return await queue.RunMarch7thAsync(adapter);
    }
    public async Task<RunRecord?> GetRunStatus(Guid runId) =>
        (await history.ReadRecentRunsAsync(100)).FirstOrDefault(r => r.RunId == runId);
}
