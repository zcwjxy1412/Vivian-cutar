using System.IO;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.Cli;

namespace VIvian.App;

/// <param name="AllowTemporaryConfiguration">The user explicitly allowed the listed temporary upstream setting
/// changes for this one run; VIvian restores them afterwards.</param>
public sealed record RealTaskAuthorization(string InstallDirectory, March7thReleaseEvidence Release,
    Func<bool> StillConfirmed, bool InstallationVerified, bool AllowTemporaryConfiguration = false);

/// <summary>Queue result plus what happened to temporary configuration (null when nothing was changed).</summary>
public sealed record TaskPlanExecution(QueueResult? Queue, RestoreResult? Restore);

/// <summary>One controlled entry for UI and later in-process AI tools; execution always uses the existing scheduler.</summary>
public interface IUnifiedTaskControl : IConfigurationControl
{
    TaskPlan CreateTaskPlan(GameId game, IReadOnlyList<string> taskIds);
    /// <summary>Settings VIvian would temporarily change for this task (empty: nothing to change).</summary>
    IReadOnlyList<TemporaryChange> RequiredTemporaryChanges(string installDirectory, string taskId);
    Task<TaskPlanExecution> ExecuteTaskPlan(TaskPlan plan, RealTaskAuthorization authorization);
    /// <summary>Retries restoring a temporary change left by an interrupted run.</summary>
    RestoreResult RestorePendingConfiguration();
    bool HasPendingConfigurationRestore { get; }
    Task<RunRecord?> GetRunStatus(Guid runId);
}

public sealed class UnifiedTaskControl : IUnifiedTaskControl
{
    private readonly QueueViewModel queue;
    private readonly IRunHistory history;
    private readonly IProcessSupervisor supervisor;
    private readonly IConfigurationControl _configuration;
    private readonly TemporaryConfigurationManager _temporary;

    public UnifiedTaskControl(QueueViewModel queue, IRunHistory history, IProcessSupervisor supervisor, string dataDirectory,
        IConfigurationControl? configuration = null)
    {
        this.queue = queue; this.history = history; this.supervisor = supervisor;
        _configuration = configuration ?? new VersionedConfigurationControl();
        _temporary = new TemporaryConfigurationManager(_configuration, Path.Combine(dataDirectory, "configuration-recovery"),
            Path.Combine(dataDirectory, "configuration-audit"));
    }

    public bool HasPendingConfigurationRestore => _temporary.HasPending;
    public RestoreResult RestorePendingConfiguration() => _temporary.Restore();
    public IReadOnlyList<TemporaryChange> RequiredTemporaryChanges(string installDirectory, string taskId) =>
        TemporaryConfigurationManager.Plan(ReadConfiguration(GameId.StarRail, installDirectory),
            March7thTaskRequirements.For(taskId));
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
    public async Task<TaskPlanExecution> ExecuteTaskPlan(TaskPlan plan, RealTaskAuthorization authorization)
    {
        if (plan.Game != GameId.StarRail || plan.Entries.Count != 1 || !plan.Entries[0].Enabled ||
            !RealExecutionPolicy.AllowsTask(plan.Entries[0].TaskId) || !authorization.StillConfirmed() ||
            !authorization.InstallationVerified || !queue.IsIdle || queue.HasUnresolved)
            throw new InvalidOperationException("本次任务、授权、安装或清理状态未通过检查。");
        if (_temporary.HasPending)
            throw new InvalidOperationException("上一次运行的临时配置尚未恢复；请先点击“恢复临时配置”。");
        var task = plan.Entries[0].TaskId;
        March7thTaskContract.StartArguments(task);
        var config = ReadConfiguration(GameId.StarRail, authorization.InstallDirectory);
        var changes = TemporaryConfigurationManager.Plan(config, March7thTaskRequirements.For(task));
        if (changes.Count > 0 && !authorization.AllowTemporaryConfiguration)
            throw new InvalidOperationException("本任务需要临时修改：" + TemporaryConfigurationManager.Describe(changes, config) +
                "。请勾选“允许本次临时修改”后再开始。");
        if (changes.Count > 0) _temporary.Apply(config, changes, authorization.InstallDirectory, task);
        QueueResult? result;
        try
        {
            var adapter = new March7thAdapter(authorization.InstallDirectory, authorization.Release, task,
                authorization.StillConfirmed, supervisor);
            result = await queue.RunMarch7thAsync(adapter);
        }
        catch
        {
            // Restoring checks the real file; while upstream is still running it is refused and the record stays.
            if (changes.Count > 0) _temporary.Restore();
            throw;
        }
        return new(result, changes.Count > 0 ? _temporary.Restore() : null);
    }
    public async Task<RunRecord?> GetRunStatus(Guid runId) =>
        (await history.ReadRecentRunsAsync(100)).FirstOrDefault(r => r.RunId == runId);
}
