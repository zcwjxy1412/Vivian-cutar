using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Engines;
using VIvian.Tests.Execution;
using Xunit;

namespace VIvian.Tests.Engines;

public sealed class EngineCatalogTests
{
    [Fact]
    public void EveryGameHasExactlyOnePinnedProfileWithTheFullCapabilityList()
    {
        Assert.Equal(3, EngineCatalog.All.Count);
        foreach (var game in Enum.GetValues<GameId>())
        {
            var profile = EngineCatalog.For(game);
            Assert.Equal(game, profile.Game);
            Assert.Equal(Enum.GetValues<EngineCapabilityId>().Order(), profile.Capabilities.Select(c => c.Id).Order());
            Assert.False(string.IsNullOrWhiteSpace(profile.RuleVersion));
            Assert.Contains(profile.Launch.EntryProgram, profile.ExpectedPrograms);
        }
        Assert.Equal(3, EngineCatalog.All.Select(p => p.RuleVersion).Distinct().Count());
    }

    [Fact]
    public void NoCapabilityThatLaunchesUpstreamCodeIsEnabledOrVerified()
    {
        foreach (var capability in EngineCatalog.All.SelectMany(p => p.Capabilities))
        {
            if (capability.LaunchesUpstreamCode) Assert.False(capability.EnabledInVivian, capability.Title);
            // Nothing has been observed on a real release or desktop yet.
            Assert.NotEqual(CapabilityStatus.Verified, capability.Status);
            Assert.NotEqual(EvidenceLevel.A, capability.Evidence);
            Assert.False(string.IsNullOrWhiteSpace(capability.Source));
        }
    }

    [Fact]
    public void TasksLaunchRequirementsAndCompletionContractsArePendingOrUnsupported()
    {
        foreach (var profile in EngineCatalog.All)
        {
            Assert.All(profile.Tasks, t => Assert.NotEqual(CapabilityStatus.Verified, t.Status));
            Assert.NotEqual(CapabilityStatus.Verified, profile.Launch.Status);
            Assert.All(profile.CompletionContract, r => Assert.NotEqual(CapabilityStatus.Verified, r.Status));
            Assert.Equal(Enum.GetValues<EvidenceOutcome>().Order(), profile.CompletionContract.Select(r => r.Outcome).Order());
        }
    }

    [Fact]
    public void OnlyMarch7thAssetDigestsComeFromOfficialMetadataAndMatchTheVerifier()
    {
        var m7 = EngineCatalog.March7th.OfficialAssets;
        Assert.Equal(
            March7thReleaseVerifier.OfficialAssets.Select(a => (a.FileName, (long?)a.Length, a.Sha256)),
            m7.Select(a => (a.FileName, a.Length, a.Sha256)));
        Assert.All(EngineCatalog.BetterGi.OfficialAssets.Concat(EngineCatalog.OneDragon.OfficialAssets),
            a => Assert.Equal(CapabilityStatus.Pending, a.DigestStatus));
        Assert.All(EngineCatalog.All.SelectMany(p => p.OfficialAssets), a => Assert.Matches("^[0-9a-f]{64}$", a.Sha256));
    }

    [Fact]
    public void OneDragonManagedEntryIsTheRuntimeLauncherAndPathRuleIsDeclared()
    {
        var od = EngineCatalog.OneDragon;
        Assert.Equal("OneDragon-RuntimeLauncher.exe", od.Launch.EntryProgram);
        Assert.Equal("OneDragon-RuntimeLauncher.exe", od.ExpectedPrograms[0]);
        Assert.Equal(PathConstraint.AsciiWithoutSpaces, od.Launch.Path);
        Assert.Equal(ProcessTopology.GuiMayRestartElevated, EngineCatalog.BetterGi.Launch.Topology);
        Assert.False(EngineCatalog.BetterGi.Launch.WritesToStandardOutput);
    }

    [Theory]
    [InlineData("v0.66.0", "0.66.0", true)]
    [InlineData("v0.66.0", "0.66.0.0", true)]
    [InlineData("v0.66.0", "0.66.0+abc", true)]
    [InlineData("v0.66.0", "0.66.1", false)]
    [InlineData("v0.66.0", "0.66", false)]
    [InlineData("v2.6.0", "2.6.0.1", false)]
    [InlineData("v2.6.0", "", false)]
    [InlineData("v2.6.0", "dev", false)]
    [InlineData("v2.6.0", null, false)]
    public void VersionMatchingIsExact(string pinned, string? detected, bool expected) =>
        Assert.Equal(expected, EngineReadinessInspector.VersionMatches(pinned, detected));

    [Theory]
    [InlineData(@"D:\OneDragon", true)]
    [InlineData(@"D:\Games\Zzz-OD_2", true)]
    [InlineData(@"D:\Program Files\OneDragon", false)]
    [InlineData(@"D:\游戏\OneDragon", false)]
    [InlineData(@"\\server\share\OneDragon", false)]
    public void OneDragonPathRule(string path, bool ok) => Assert.Equal(ok, EngineReadinessInspector.IsAsciiPathWithoutSpaces(path));
}

public sealed class CompletionEvidenceEvaluatorTests
{
    private static readonly Guid Run = Guid.NewGuid();
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;
    private static ObservedSignal S(SignalKind kind, int? exit = null, Guid? run = null) => new(run ?? Run, kind, "synthetic", At, exit);

    [Fact]
    public void UnverifiedContractNeverDecidesEvenWithPerfectSignals()
    {
        var verdict = CompletionEvidenceEvaluator.Evaluate(Run, contractVerified: false,
            [S(SignalKind.CompletionMarker), S(SignalKind.ProcessExit, 0)]);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
        Assert.Equal(ReasonCodes.RealAdapterUnverified, verdict.ReasonCode);
        Assert.All(verdict.Evidence, e => Assert.Equal(EvidenceKind.Note, e.Kind));
    }

    [Fact]
    public void SuccessNeedsCompletionMarkerAndExitZero()
    {
        Assert.Equal(RunOutcome.Success, CompletionEvidenceEvaluator.Evaluate(Run, true,
            [S(SignalKind.CompletionMarker), S(SignalKind.ProcessExit, 0)]).Outcome);
        var exitOnly = CompletionEvidenceEvaluator.Evaluate(Run, true, [S(SignalKind.ProcessExit, 0)]);
        Assert.Equal(RunOutcome.Indeterminate, exitOnly.Outcome);
        Assert.Equal(ReasonCodes.NoCompletionEvidence, exitOnly.ReasonCode);
        var markerOnly = CompletionEvidenceEvaluator.Evaluate(Run, true, [S(SignalKind.CompletionMarker)]);
        Assert.Equal(RunOutcome.Indeterminate, markerOnly.Outcome);
    }

    [Fact]
    public void InconsistentEvidenceIsIndeterminate()
    {
        var markerAndCrash = CompletionEvidenceEvaluator.Evaluate(Run, true, [S(SignalKind.CompletionMarker), S(SignalKind.ProcessExit, 3)]);
        Assert.Equal(RunOutcome.Indeterminate, markerAndCrash.Outcome);
        Assert.Equal(ReasonCodes.ConflictingEvidence, markerAndCrash.ReasonCode);
        var markerAndFatal = CompletionEvidenceEvaluator.Evaluate(Run, true, [S(SignalKind.CompletionMarker), S(SignalKind.FatalError), S(SignalKind.ProcessExit, 0)]);
        Assert.Equal(RunOutcome.Indeterminate, markerAndFatal.Outcome);
        var skipped = CompletionEvidenceEvaluator.Evaluate(Run, true, [S(SignalKind.CompletionMarker), S(SignalKind.NothingDone), S(SignalKind.ProcessExit, 0)]);
        Assert.Equal(RunOutcome.Indeterminate, skipped.Outcome);
    }

    [Fact]
    public void FailureAndCancellationAreReported()
    {
        Assert.Equal(RunOutcome.Failed, CompletionEvidenceEvaluator.Evaluate(Run, true, [S(SignalKind.ProcessExit, 1)]).Outcome);
        Assert.Equal(RunOutcome.Failed, CompletionEvidenceEvaluator.Evaluate(Run, true, [S(SignalKind.FailureMarker), S(SignalKind.ProcessExit, 1)]).Outcome);
        Assert.Equal(RunOutcome.Cancelled, CompletionEvidenceEvaluator.Evaluate(Run, true, [S(SignalKind.CancelMarker)]).Outcome);
        // An unknown exit code is not a failure report.
        Assert.Equal(RunOutcome.Indeterminate, CompletionEvidenceEvaluator.Evaluate(Run, true, [S(SignalKind.ProcessExit, null)]).Outcome);
    }

    [Fact]
    public void SignalsOfOtherRunsAreIgnored()
    {
        var other = Guid.NewGuid();
        var verdict = CompletionEvidenceEvaluator.Evaluate(Run, true,
            [S(SignalKind.CompletionMarker, run: other), S(SignalKind.ProcessExit, 0, other), S(SignalKind.ProcessExit, 0)]);
        Assert.Equal(RunOutcome.Indeterminate, verdict.Outcome);
        Assert.All(verdict.Evidence, e => Assert.Equal(Run, e.RunId));
    }
}

public sealed class EngineReadinessInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-readiness-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestDirectories.DeleteWithRetry(_root);

    [Theory]
    [InlineData(GameId.Genshin)]
    [InlineData(GameId.StarRail)]
    [InlineData(GameId.ZenlessZoneZero)]
    public async Task EmptyPathIsNotConfiguredAndExecutionIsAlwaysDisabled(GameId game)
    {
        var report = await EngineReadinessInspector.InspectAsync(game, "  ");
        Assert.Equal(CompatibilityClass.NotConfigured, report.Compatibility);
        Assert.Equal(EngineErrorCategory.NotConfigured, report.PrimaryError);
        Assert.False(report.RealExecutionAllowed);
        Assert.Contains(report.Checks, c => c.Id == "execution" && c.Category == EngineErrorCategory.ExecutionDisabled);
    }

    [Theory]
    [InlineData(GameId.Genshin)]
    [InlineData(GameId.StarRail)]
    [InlineData(GameId.ZenlessZoneZero)]
    public async Task InvalidAndMissingPathsAreClassified(GameId game)
    {
        Assert.Equal(EngineErrorCategory.PathInvalid, (await EngineReadinessInspector.InspectAsync(game, "relative\\path")).PrimaryError);
        Assert.Equal(EngineErrorCategory.PathInvalid, (await EngineReadinessInspector.InspectAsync(game, _root + "\u0001")).PrimaryError);
        var missing = await EngineReadinessInspector.InspectAsync(game, _root);
        Assert.Equal(CompatibilityClass.NotInstalled, missing.Compatibility);
        Assert.Equal(EngineErrorCategory.NotInstalled, missing.PrimaryError);
    }

    [Theory]
    [InlineData(GameId.Genshin)]
    [InlineData(GameId.ZenlessZoneZero)]
    public async Task FolderWithoutProgramIsNotInstalled(GameId game)
    {
        Directory.CreateDirectory(_root);
        var report = await EngineReadinessInspector.InspectAsync(game, _root);
        Assert.Equal(CompatibilityClass.NotInstalled, report.Compatibility);
        Assert.Contains(report.Checks, c => c.Id == "program" && c.State == CheckState.Failed);
    }

    [Fact]
    public async Task BetterGiFixtureWithoutVersionIsHashedButUnknownAndNothingChanges()
    {
        Directory.CreateDirectory(Path.Combine(_root, "User", "OneDragon"));
        var exe = Path.Combine(_root, "BetterGI.exe");
        await File.WriteAllTextAsync(exe, "harmless fixture, never executed");
        var before = Snapshot();

        var report = await EngineReadinessInspector.InspectAsync(GameId.Genshin, _root);

        Assert.Equal(CompatibilityClass.UnknownVersion, report.Compatibility);
        Assert.Matches("^[0-9a-f]{64}$", report.ProgramSha256!);
        Assert.Contains(report.Checks, c => c.Id == "identity" && c.State == CheckState.Pending);
        Assert.Contains(report.Checks, c => c.Id == @"path:User\OneDragon" && c.State == CheckState.Passed
            || c.Id == @"path:User\OneDragon" && !OperatingSystem.IsWindows());
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task OtherVersionIsUnsupported()
    {
        // A real managed assembly carries version resources; its version (1.0.0) is not the pinned BetterGI release.
        Directory.CreateDirectory(_root);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "VIvian.FakeEngine.dll"), Path.Combine(_root, "BetterGI.exe"));
        var report = await EngineReadinessInspector.InspectAsync(GameId.Genshin, _root);
        Assert.Equal(CompatibilityClass.UnsupportedVersion, report.Compatibility);
        Assert.Equal(EngineErrorCategory.UnsupportedVersion, report.PrimaryError);
        Assert.False(report.RealExecutionAllowed);
    }

    [Fact]
    public async Task OneDragonPlainLauncherOnlyIsFlaggedAsUnmanagedEntry()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "OneDragon-Launcher.exe"), "harmless fixture");
        var report = await EngineReadinessInspector.InspectAsync(GameId.ZenlessZoneZero, _root);
        Assert.Contains(report.Checks, c => c.Id == "entry" && c.State == CheckState.Warning);
        Assert.EndsWith("OneDragon-Launcher.exe", report.ProgramPath);
    }

    [Fact]
    public async Task OneDragonRuntimeLauncherIsPreferredWhenBothExist()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "OneDragon-Launcher.exe"), "a");
        await File.WriteAllTextAsync(Path.Combine(_root, "OneDragon-RuntimeLauncher.exe"), "b");
        var report = await EngineReadinessInspector.InspectAsync(GameId.ZenlessZoneZero, _root);
        Assert.EndsWith("OneDragon-RuntimeLauncher.exe", report.ProgramPath);
        Assert.DoesNotContain(report.Checks, c => c.Id == "entry");
    }

    [Fact]
    public async Task March7thFixtureIsPendingUntilOfficialEvidenceAndMismatchFails()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets", "config"));
        await File.WriteAllTextAsync(Path.Combine(_root, "m7a.exe"), "harmless test bytes");
        await File.WriteAllTextAsync(Path.Combine(_root, "assets", "config", "version.txt"), "v2026.10.31");

        var pending = await EngineReadinessInspector.InspectAsync(GameId.StarRail, _root);
        Assert.Equal(CompatibilityClass.PendingValidation, pending.Compatibility);
        Assert.Contains(pending.Checks, c => c.Id == "identity" && c.State == CheckState.Pending && c.Category == EngineErrorCategory.EvidencePending);

        var other = new March7thReleaseEvidence("v2026.10.31", "March7thAssistant_full.zip", new string('0', 64), new string('1', 64));
        var mismatch = await EngineReadinessInspector.InspectAsync(GameId.StarRail, _root, other, TimeProvider.System);
        Assert.Equal(EngineErrorCategory.IdentityMismatch, mismatch.PrimaryError);

        var same = other with { CliSha256 = "ab8d086c47edf52bf89163880b2768e53154580566321694a1c89e0be4d08bd7" };
        var verified = await EngineReadinessInspector.InspectAsync(GameId.StarRail, _root, same, TimeProvider.System);
        Assert.Equal(CompatibilityClass.IdentityVerified, verified.Compatibility);
        Assert.False(verified.RealExecutionAllowed);
    }

    [Fact]
    public async Task March7thUnsupportedVersionIsReported()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets", "config"));
        await File.WriteAllTextAsync(Path.Combine(_root, "m7a.exe"), "harmless test bytes");
        await File.WriteAllTextAsync(Path.Combine(_root, "assets", "config", "version.txt"), "v2025.1.1");
        var report = await EngineReadinessInspector.InspectAsync(GameId.StarRail, _root);
        Assert.Equal(CompatibilityClass.UnsupportedVersion, report.Compatibility);
    }

    [Fact]
    public async Task ChangedExecutableHashIsVisible()
    {
        Directory.CreateDirectory(_root);
        var exe = Path.Combine(_root, "BetterGI.exe");
        await File.WriteAllTextAsync(exe, "first");
        var first = await EngineReadinessInspector.InspectAsync(GameId.Genshin, _root);
        await File.WriteAllTextAsync(exe, "second");
        var second = await EngineReadinessInspector.InspectAsync(GameId.Genshin, _root);
        Assert.NotEqual(first.ProgramSha256, second.ProgramSha256);
    }

    [Fact]
    public async Task LinkedInstallationFolderIsRejected()
    {
        var real = Directory.CreateDirectory(Path.Combine(_root, "real")).FullName;
        await File.WriteAllTextAsync(Path.Combine(real, "BetterGI.exe"), "fixture");
        var link = Path.Combine(_root, "link");
        try { Directory.CreateSymbolicLink(link, real); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // no link privilege here
        var report = await EngineReadinessInspector.InspectAsync(GameId.Genshin, link);
        Assert.Equal(CompatibilityClass.UnsafeLayout, report.Compatibility);
        Assert.Equal(EngineErrorCategory.LinkRejected, report.PrimaryError);
    }

    private Dictionary<string, string> Snapshot() => Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(p => p, p => File.Exists(p) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))) : "dir");
}

public sealed class PendingEngineAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vivian-pending-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestDirectories.DeleteWithRetry(_root);

    [Theory]
    [InlineData(GameId.Genshin)]
    [InlineData(GameId.StarRail)]
    [InlineData(GameId.ZenlessZoneZero)]
    public async Task AdapterRefusesToStartAndTheSchedulerNeverCallsTheSupervisor(GameId game)
    {
        var adapter = new PendingEngineAdapter(EngineCatalog.For(game), () => _root);
        Assert.False(adapter.IsSimulation);
        var pre = await adapter.PreflightAsync(new RunContext(Guid.NewGuid(), Guid.NewGuid(), game, DateTimeOffset.UtcNow), default);
        Assert.False(pre.CanStart);
        Assert.Equal(ReasonCodes.RealAdapterUnverified, pre.ReasonCode);
        var start = await Assert.ThrowsAsync<EngineStartException>(() =>
            adapter.StartAsync(new RunContext(Guid.NewGuid(), Guid.NewGuid(), game, DateTimeOffset.UtcNow), new NoProcessSupervisor(), default));
        Assert.True(start.NothingStarted);

        var supervisor = new CountingSupervisor();
        var result = await new QueueScheduler(new InMemoryJournal(), supervisor).RunAsync([new QueueItem(game, adapter)]);
        Assert.Equal(QueueStatus.Blocked, result.Status);
        Assert.Equal(ReasonCodes.RealAdapterUnverified, result.BlockReason);
        Assert.Equal(0, supervisor.Calls);
    }

    private sealed class CountingSupervisor : IProcessSupervisor
    {
        public int Calls;
        public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("must not be called");
        }
    }
}
