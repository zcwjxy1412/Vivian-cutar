using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Diagnostics;
using VIvian.Infrastructure.Engines.Isolation;

namespace VIvian.Infrastructure.Engines.Cli;

public enum CliOperationId { Version, TaskList, Status }

/// <summary>A candidate diagnostic command. Its arguments come from pinned source (level B) and are unverified.</summary>
public sealed record CliOperation(CliOperationId Id, IReadOnlyList<string> Arguments, CapabilityStatus Status, EngineCapabilityId Capability, string Note);

/// <summary>Candidate diagnostic queries for March7thAssistant v2026.10.31 (m7a.exe). None is verified on the release.</summary>
public static class March7thCliOperations
{
    public const string RelativeExecutable = "m7a.exe";

    public static CliOperation Version { get; } = new(CliOperationId.Version, ["--version"], CapabilityStatus.Pending, EngineCapabilityId.VersionQuery,
        "可能触发配置初始化（级别 B/D）。");
    public static CliOperation TaskList { get; } = new(CliOperationId.TaskList, ["--list", "--json"], CapabilityStatus.Pending, EngineCapabilityId.TaskListQuery,
        "JSON 结构未经实包确认。");
    public static CliOperation Status { get; } = new(CliOperationId.Status, ["--status"], CapabilityStatus.Pending, EngineCapabilityId.StatusQuery,
        "不是严格只读：可能创建/删除写权限探测文件或生成 config.yaml。");

    public static IReadOnlyList<CliOperation> All { get; } = [Version, TaskList, Status];
}

/// <summary>
/// Explicit, per-operation approval to run one diagnostic command against one exact executable inside an isolated copy.
/// It is recorded in the evidence export. In this build no gate accepts it for upstream programs.
/// </summary>
public sealed record CliAuthorization(CliOperationId Operation, string ExpectedExecutableSha256, string ApprovedBy, DateTimeOffset ApprovedAt, string Purpose);

public interface ICliExecutionGate
{
    ExecutionDecision Check(CliOperation operation, string executableSha256, CliAuthorization? authorization);
}

/// <summary>The gate used by the application: in the pre-desktop builds it refuses every upstream diagnostic command.</summary>
public sealed class BuildCliExecutionGate : ICliExecutionGate
{
    public ExecutionDecision Check(CliOperation operation, string executableSha256, CliAuthorization? authorization)
    {
        if (authorization is null)
            return new(false, "cli_not_authorized", "没有用户对本次诊断命令的明确授权。");
        if (authorization.Operation != operation.Id || !string.Equals(authorization.ExpectedExecutableSha256, executableSha256, StringComparison.Ordinal))
            return new(false, "cli_authorization_mismatch", "授权与命令或程序文件摘要不一致。");
        // Approved or not, this build never executes upstream binaries (pre-desktop phase).
        return new(false, ReasonCodes.RealAdapterUnverified, "此开发版不运行上游程序；隔离 CLI 诊断将在桌面验证阶段经授权后启用。");
    }
}

public enum CliRunState { Completed, Refused, StartFailed, TimedOut, Cancelled, CleanupUnconfirmed }

public sealed record CliDiagnosticResult(
    CliRunState State, CliOperationId Operation, IReadOnlyList<string> Arguments, string? ExecutableRelativePath,
    string? ExecutableSha256, int? ExitCode, IReadOnlyList<string> Output, bool OutputTruncated, TimeSpan Duration,
    CleanupState Cleanup, FileTreeDiff? Changes, string ReasonCode, string Detail, CliAuthorization? Authorization,
    DateTimeOffset StartedAt);

public sealed record CliRunLimits(TimeSpan Timeout, int MaxLines, int MaxLineLength, TimeSpan CleanupTimeout)
{
    public static CliRunLimits Default { get; } = new(TimeSpan.FromSeconds(60), 2000, 4000, TimeSpan.FromSeconds(10));
}

/// <summary>
/// Runs one approved diagnostic command inside an isolated copy, through the shared <see cref="IProcessSupervisor"/>
/// (Job Object ownership), with a time limit, bounded output capture, exit code, cleanup verification and a before/after
/// file report. It never runs anything outside the isolated copy and never uses the user's installation.
/// Standard output and standard error arrive merged, in order, from the supervisor.
/// </summary>
public sealed class CliDiagnosticRunner(IProcessSupervisor supervisor, ICliExecutionGate gate, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public Task<CliDiagnosticResult> RunAsync(IsolatedCopy copy, string relativeExecutable, CliOperation operation,
        CliAuthorization? authorization, CliRunLimits? limits = null, CancellationToken ct = default) =>
        RunAsync(copy, relativeExecutable, operation, authorization, limits, environment: null, ct);

    /// <param name="environment">Test seam for the synthetic FakeEngine; the application passes null.</param>
    internal async Task<CliDiagnosticResult> RunAsync(IsolatedCopy copy, string relativeExecutable, CliOperation operation,
        CliAuthorization? authorization, CliRunLimits? limits, IReadOnlyDictionary<string, string>? environment, CancellationToken ct)
    {
        limits ??= CliRunLimits.Default;
        var started = _clock.GetUtcNow();
        CliDiagnosticResult Refuse(string reason, string detail, string? sha = null) =>
            new(CliRunState.Refused, operation.Id, operation.Arguments, relativeExecutable, sha, null, [], false, TimeSpan.Zero,
                CleanupState.NotRequired, null, reason, detail, authorization, started);

        if (copy.State != IsolationState.Ready || copy.Root is null)
            return Refuse("isolation_not_ready", "没有可用的隔离副本，未运行任何程序。");
        var normalized = relativeExecutable.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized)) return Refuse("path_escape", "程序路径必须是隔离副本内的相对路径。");
        var executable = Path.GetFullPath(Path.Combine(copy.Root, normalized));
        if (!IsolatedCopyBuilder.IsSameOrUnder(executable, copy.Root) || Path.GetRelativePath(copy.Root, executable).StartsWith("..", StringComparison.Ordinal))
            return Refuse("path_escape", "程序路径不在隔离副本内，已拒绝。");
        if (!File.Exists(executable) || ReadOnlyFileAccess.FindReparsePoint(executable) is not null)
            return Refuse("executable_missing", "隔离副本中没有该程序文件，或它是链接。");
        string sha;
        await using (var stream = ReadOnlyFileAccess.OpenForStableRead(executable))
            sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).ToLowerInvariant();
        var expected = copy.Files.FirstOrDefault(f => string.Equals(f.RelativePath, relativeExecutable.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (expected is null || expected.Sha256 != sha)
            return Refuse("isolated_copy_changed", "隔离副本中的程序与复制时记录的摘要不一致，已拒绝。", sha);

        var decision = gate.Check(operation, sha, authorization);
        if (!decision.Allowed) return Refuse(decision.ReasonCode, decision.Explanation, sha);

        var before = await FileTreeSnapshot.CaptureAsync(copy.Root, ct: ct).ConfigureAwait(false);
        var runId = Guid.NewGuid();
        var clock = Stopwatch.StartNew();
        ISupervisedProcessGroup group;
        try
        {
            group = await supervisor.StartAsync(new ProcessLaunchSpec(executable, operation.Arguments, copy.Root, environment), runId, ct).ConfigureAwait(false);
        }
        catch (EngineStartException ex)
        {
            return new(CliRunState.StartFailed, operation.Id, operation.Arguments, relativeExecutable, sha, null, [], false, clock.Elapsed,
                ex.NothingStarted ? CleanupState.NotRequired : CleanupState.Unconfirmed, null, ex.ReasonCode, ex.Message, authorization, started);
        }

        await using (group.ConfigureAwait(false))
        {
            var output = new List<string>();
            var truncated = false;
            var reader = Task.Run(async () =>
            {
                await foreach (var line in group.OutputLines.ConfigureAwait(false))
                {
                    lock (output)
                    {
                        if (output.Count >= limits.MaxLines) { truncated = true; continue; }
                        if (line.Length > limits.MaxLineLength) truncated = true;
                        output.Add(line.Length > limits.MaxLineLength ? line[..limits.MaxLineLength] : line);
                    }
                }
            }, CancellationToken.None);

            var state = CliRunState.Completed;
            var reason = "completed";
            int? exitCode = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(limits.Timeout);
            try
            {
                exitCode = await group.WaitForRootExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                (state, reason) = ct.IsCancellationRequested ? (CliRunState.Cancelled, ReasonCodes.UserStop) : (CliRunState.TimedOut, ReasonCodes.TaskTimeout);
            }
            // Always ends with a cleanup check: leftovers that the job still owns are terminated and must reach zero.
            var cleanup = await group.TerminateAllAsync(limits.CleanupTimeout, CancellationToken.None).ConfigureAwait(false);
            if (cleanup != CleanupState.Confirmed) { state = CliRunState.CleanupUnconfirmed; reason = ReasonCodes.CleanupUnconfirmed; }
            await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)).ConfigureAwait(false);
            var duration = clock.Elapsed;

            var after = await FileTreeSnapshot.CaptureAsync(copy.Root, ct: CancellationToken.None).ConfigureAwait(false);
            List<string> captured;
            lock (output) captured = [.. output];
            var detail = state switch
            {
                CliRunState.Completed => $"进程已退出（退出码 {exitCode}），归属进程已全部结束。退出码只是原始信号，不代表命令语义正确。",
                CliRunState.TimedOut => "超过时间上限，已终止归属进程。",
                CliRunState.Cancelled => "已取消，已终止归属进程。",
                _ => "无法确认归属进程已全部结束：后续操作应保持阻断。",
            };
            return new(state, operation.Id, operation.Arguments, relativeExecutable, sha, exitCode, captured, truncated, duration,
                cleanup, before.Compare(after), reason, detail, authorization, started);
        }
    }
}

/// <summary>Machine-readable evidence for one diagnostic command, redacted before it leaves the machine.</summary>
public static class CliEvidenceExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToJson(EngineProfile profile, CliDiagnosticResult result, object? parsed, Redactor redactor, bool synthetic)
    {
        var document = new
        {
            schema = "vivian.cli-evidence/1",
            synthetic,
            engine = profile.EngineName,
            pinnedVersion = profile.PinnedVersion,
            ruleVersion = profile.RuleVersion,
            operation = result.Operation,
            arguments = result.Arguments,
            executable = result.ExecutableRelativePath,
            executableSha256 = result.ExecutableSha256,
            state = result.State,
            reasonCode = result.ReasonCode,
            detail = redactor.Redact(result.Detail),
            exitCode = result.ExitCode,
            durationMs = (long)result.Duration.TotalMilliseconds,
            cleanup = result.Cleanup,
            outputTruncated = result.OutputTruncated,
            output = result.Output.Select(redactor.Redact).ToList(),
            fileChanges = result.Changes is null ? null : new
            {
                added = result.Changes.Added, removed = result.Changes.Removed, modified = result.Changes.Modified,
                incomplete = result.Changes.Incomplete,
                note = "只比较运行前后两次快照；运行期间创建又删除的文件无法看到。",
            },
            parsed,
            authorization = result.Authorization is null ? null : new
            {
                result.Authorization.Operation, result.Authorization.ExpectedExecutableSha256,
                approvedBy = redactor.Redact(result.Authorization.ApprovedBy), result.Authorization.ApprovedAt,
                purpose = redactor.Redact(result.Authorization.Purpose),
            },
            startedAt = result.StartedAt,
            verificationStatus = synthetic ? "synthetic-fixture" : "unverified-observation",
        };
        return JsonSerializer.Serialize(document, Options);
    }
}
