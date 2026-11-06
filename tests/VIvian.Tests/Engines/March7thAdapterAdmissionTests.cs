using VIvian.Core;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Simulation;
using Xunit;
#if M4_SCREEN_TEST || M4_MANUAL_TEST
using System.Security.Cryptography;
#endif

namespace VIvian.Tests.Engines;

public class March7thAdapterAdmissionTests
{
#if M4_SCREEN_TEST || M4_MANUAL_TEST
    private sealed class FakeGroup(Guid id, params string[] output) : ISupervisedProcessGroup
    {
        public Guid RunId => id;
        public int RootProcessId => 1;
        public DateTimeOffset RootStartTime => DateTimeOffset.UtcNow;
        public int ActiveProcessCount => 0;
        public bool OwnsProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process) => false;
        public IAsyncEnumerable<string> OutputLines => Lines();
        private async IAsyncEnumerable<string> Lines()
        {
            foreach (var line in output) yield return line;
            await Task.CompletedTask;
        }
        public Task<int> WaitForRootExitAsync(CancellationToken ct) => Task.FromResult(0);
        public Task<bool> SendInputLineAsync(string line, CancellationToken ct) => Task.FromResult(false);
        public Task<CleanupState> TerminateAllAsync(TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(CleanupState.Confirmed);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSupervisor(Func<ProcessLaunchSpec, Guid, ISupervisedProcessGroup> launch) : IProcessSupervisor
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct)
        {
            Calls.Add(spec.Arguments);
            return Task.FromResult(launch(spec, runId));
        }
    }

    [Fact]
    public async Task StatusAndExactStopUseAuxiliarySupervisorWhileTaskUsesSingleRunLaunch()
    {
        var root = Path.Combine(Path.GetTempPath(), "vivian-m7a-adapter-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "assets", "config"));
            var cli = Path.Combine(root, "m7a.exe");
            await File.WriteAllTextAsync(cli, "harmless test bytes");
            await File.WriteAllTextAsync(Path.Combine(root, "assets", "config", "version.txt"), "v2026.10.31");
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(cli))).ToLowerInvariant();
            var evidence = new March7thReleaseEvidence("v2026.10.31", "March7thAssistant_full.zip", new string('a', 64), hash);
            const string upstreamId = "20261105-090000-abcd";
            var auxiliary = new FakeSupervisor((spec, id) => spec.Arguments[0] == "--status"
                ? new FakeGroup(id, "{\"schema\":1,\"first_run_completed\":true,\"game_path_exists\":true,\"cloud_game_enable\":false,\"active_run\":null}")
                : new FakeGroup(id, "{\"run_id\":\"" + upstreamId + "\",\"action\":\"stop\",\"acknowledged\":true}"));
            var taskScope = new FakeSupervisor((_, id) => new FakeGroup(id, "[m7a] 运行编号 " + upstreamId));
            var adapter = new March7thAdapter(root, evidence, "screen_test", () => true, auxiliary);
            var runId = Guid.NewGuid();
            var context = new RunContext(runId, Guid.NewGuid(), GameId.StarRail, DateTimeOffset.UtcNow);
            await using var run = await adapter.StartAsync(context, taskScope, CancellationToken.None);
            await run.WaitForVerdictAsync(CancellationToken.None);
            var stop = await run.RequestStopAsync(StopMode.Emergency, CancellationToken.None);
            Assert.Equal(CleanupState.Pending, stop.Cleanup);
            Assert.Single(taskScope.Calls);
            Assert.Equal("screen_test", taskScope.Calls[0][0]);
            Assert.Equal(2, auxiliary.Calls.Count);
            Assert.Equal("--status", auxiliary.Calls[0][0]);
            Assert.Equal(new[] { "--control", upstreamId, "stop", "--json" }, auxiliary.Calls[1]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
#endif

    [Fact]
    public async Task RealTaskCannotStartBeforeUacOwnershipIsVerified()
    {
        var evidence = new March7thReleaseEvidence("v2026.10.31", "March7thAssistant_full.zip", "archive", "cli");
        var adapter = new March7thAdapter("C:\\missing", evidence, "daily", () => true,
            new RefusingProcessSupervisor());
        var context = new RunContext(Guid.NewGuid(), Guid.NewGuid(), GameId.StarRail, DateTimeOffset.UtcNow);
        var error = await Assert.ThrowsAsync<EngineStartException>(() =>
            adapter.StartAsync(context, new RefusingProcessSupervisor(), CancellationToken.None));
        Assert.True(error.NothingStarted);
        Assert.Equal(ReasonCodes.RealAdapterUnverified, error.ReasonCode);
    }
}
