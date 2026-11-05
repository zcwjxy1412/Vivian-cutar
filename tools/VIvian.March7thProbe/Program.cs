using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.Cli;
using VIvian.Infrastructure.Engines.Isolation;
using VIvian.Infrastructure.Processes;

if (!OperatingSystem.IsWindows()) { Console.WriteLine("只能在 Windows 上运行。"); return 2; }
Console.WriteLine("星铁 CLI 状态验证：仅在临时隔离副本内运行 --status；不会启动游戏或申请 UAC。");
Console.Write("粘贴上游安装目录：");
var install = Console.ReadLine()?.Trim().Trim('"');
Console.Write("粘贴官方 v2026.10.31 完整 ZIP 路径：");
var archive = Console.ReadLine()?.Trim().Trim('"');
var verified = await March7thReleaseVerifier.VerifyAsync(archive);
if (verified.Check != ReleaseCheck.Verified || verified.Evidence is null)
{
    Console.WriteLine("官方包核对失败：" + verified.Detail); return 1;
}
var installation = await March7thReadOnlyInspector.InspectAsync(install, verified.Evidence);
if (installation.Compatibility != March7thCompatibility.Verified)
{
    Console.WriteLine("安装核对失败：" + installation.Detail); return 1;
}
Console.WriteLine("将复制安装目录到临时隔离区。仅运行隔离副本内的 m7a.exe --status；状态命令可能创建临时文件。输入“同意检测”继续：");
if (Console.ReadLine()?.Trim() != "同意检测") { Console.WriteLine("已取消，未执行 CLI。"); return 0; }
var work = Path.Combine(Path.GetTempPath(), "VIvian-M4-" + Guid.NewGuid().ToString("N"));
IsolatedCopy? copy = null;
try
{
    copy = await IsolatedCopyBuilder.PrepareAsync(install!, work);
    if (copy.State != IsolationState.Ready) { Console.WriteLine("隔离副本失败：" + copy.Detail); return 1; }
    var operation = March7thCliOperations.Status;
    var authorization = new CliAuthorization(operation.Id, verified.Evidence.CliSha256, "本机用户", DateTimeOffset.UtcNow, "隔离副本状态检查");
    var runner = new CliDiagnosticRunner(new WindowsProcessSupervisor(), new ApprovedStatusGate());
    var result = await runner.RunAsync(copy, "m7a.exe", operation, authorization);
    Console.WriteLine($"CLI 状态：{result.State}；退出码：{result.ExitCode?.ToString() ?? "未知"}；清理：{result.Cleanup}；耗时：{result.Duration.TotalSeconds:F1} 秒");
    if (result.State != CliRunState.Completed || result.ExitCode != 0 || result.Cleanup != CleanupState.Confirmed
        || result.OutputTruncated || result.Changes is null || result.Changes.Incomplete)
    {
        Console.WriteLine("状态查询或证据采集未通过；请勿执行真实任务。"); return 1;
    }
    var status = March7thStatusContract.Parse(string.Join('\n', result.Output));
    Console.WriteLine(status.ReadyForUacValidation ? "状态检查通过；下一步仍需单独 UAC 归属验证。" : "状态检查阻断：" + status.Detail);
    return status.ReadyForUacValidation ? 0 : 1;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EngineStartException)
{
    Console.WriteLine("检测失败：" + ex.GetType().Name); return 1;
}
finally
{
    if (copy is { State: IsolationState.Ready } && !IsolatedCopyBuilder.Delete(copy))
        Console.WriteLine("临时隔离副本未能删除，请稍后手动检查：" + copy.Container);
    try { if (Directory.Exists(work) && !Directory.EnumerateFileSystemEntries(work).Any()) Directory.Delete(work); }
    catch (IOException) { }
}

internal sealed class ApprovedStatusGate : ICliExecutionGate
{
    public ExecutionDecision Check(CliOperation operation, string executableSha256, CliAuthorization? authorization) =>
        authorization is not null && operation.Id == CliOperationId.Status
        && authorization.Operation == operation.Id
        && string.Equals(authorization.ExpectedExecutableSha256, executableSha256, StringComparison.OrdinalIgnoreCase)
        ? new(true, "approved", "用户对隔离状态查询明确确认")
        : new(false, "m4_status_not_approved", "仅允许本次经确认的隔离状态查询");
}
