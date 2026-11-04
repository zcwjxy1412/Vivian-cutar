using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VIvian.Core.Engines;
using VIvian.Core.Execution;
using VIvian.Infrastructure.Diagnostics;
using VIvian.Infrastructure.Engines;
using VIvian.Infrastructure.Engines.Cli;
using VIvian.Infrastructure.Engines.Isolation;
using VIvian.Infrastructure.Processes;
using VIvian.Tests.Processes;
using Xunit;

namespace VIvian.Tests.Engines;

public sealed class IsolatedCopyTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "vivian-iso-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_base, "install");
    private string Workspace => Path.Combine(_base, "workspace");
    public void Dispose() => TestDirectories.DeleteWithRetry(_base);

    private void CreateSource()
    {
        Directory.CreateDirectory(Path.Combine(Source, "assets", "config"));
        File.WriteAllText(Path.Combine(Source, "m7a.exe"), "synthetic program bytes");
        File.WriteAllText(Path.Combine(Source, "assets", "config", "version.txt"), "v2026.10.31");
    }

    private Dictionary<string, string> Hashes(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(root, p).Replace('\\', '/'), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant());

    [Fact]
    public async Task CopyIsByteIdenticalIndependentAndLeavesSourceUntouched()
    {
        CreateSource();
        var before = Hashes(Source);
        var copy = await IsolatedCopyBuilder.PrepareAsync(Source, Workspace);
        Assert.Equal(IsolationState.Ready, copy.State);
        Assert.Equal(before, Hashes(copy.Root!));
        Assert.Equal(before, copy.Files.ToDictionary(f => f.RelativePath, f => f.Sha256));
        Assert.True(File.Exists(Path.Combine(copy.Container!, IsolatedCopyBuilder.MarkerName)));
        Assert.False(File.Exists(Path.Combine(copy.Root!, IsolatedCopyBuilder.MarkerName)));

        File.WriteAllText(Path.Combine(copy.Root!, "m7a.exe"), "changed in copy");
        Assert.Equal(before, Hashes(Source));

        Assert.True(IsolatedCopyBuilder.Delete(copy));
        Assert.False(Directory.Exists(copy.Container));
        Assert.Equal(before, Hashes(Source));
    }

    [Fact]
    public async Task WorkspaceInsideSourceOrSourceInsideWorkspaceIsRejected()
    {
        CreateSource();
        var inside = await IsolatedCopyBuilder.PrepareAsync(Source, Path.Combine(Source, "tmp"));
        Assert.Equal(IsolationState.Rejected, inside.State);
        Assert.Equal(EngineErrorCategory.UnsafeLayout, inside.Category);
        Assert.False(Directory.Exists(Path.Combine(Source, "tmp")));
        var outside = await IsolatedCopyBuilder.PrepareAsync(Source, _base);
        Assert.Equal(IsolationState.Rejected, outside.State);
    }

    [Fact]
    public async Task MissingOrRelativeSourceIsRejected()
    {
        Assert.Equal(EngineErrorCategory.NotInstalled, (await IsolatedCopyBuilder.PrepareAsync(Source, Workspace)).Category);
        Assert.Equal(EngineErrorCategory.PathInvalid, (await IsolatedCopyBuilder.PrepareAsync("relative", Workspace)).Category);
    }

    [Fact]
    public async Task LimitsStopTheCopyAndRemoveThePartialCopy()
    {
        CreateSource();
        var tooMany = await IsolatedCopyBuilder.PrepareAsync(Source, Workspace, new IsolationLimits(1, long.MaxValue, TimeSpan.FromMinutes(1)));
        Assert.Equal(IsolationState.Rejected, tooMany.State);
        var tooBig = await IsolatedCopyBuilder.PrepareAsync(Source, Workspace, new IsolationLimits(100, 5, TimeSpan.FromMinutes(1)));
        Assert.Equal(IsolationState.Rejected, tooBig.State);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Workspace));
    }

    [Fact]
    public async Task SymbolicLinkInsideInstallationIsRejected()
    {
        CreateSource();
        var outsideFile = Path.Combine(_base, "secret.txt");
        File.WriteAllText(outsideFile, "outside the installation");
        try { File.CreateSymbolicLink(Path.Combine(Source, "link.txt"), outsideFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // no symlink privilege on this runner
        var copy = await IsolatedCopyBuilder.PrepareAsync(Source, Workspace);
        Assert.Equal(IsolationState.Rejected, copy.State);
        Assert.Equal(EngineErrorCategory.LinkRejected, copy.Category);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Workspace));
    }

    [Fact]
    public async Task DirectoryLinkInsideInstallationIsRejected()
    {
        CreateSource();
        var outside = Directory.CreateDirectory(Path.Combine(_base, "elsewhere")).FullName;
        try { Directory.CreateSymbolicLink(Path.Combine(Source, "linked-dir"), outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        var copy = await IsolatedCopyBuilder.PrepareAsync(Source, Workspace);
        Assert.Equal(EngineErrorCategory.LinkRejected, copy.Category);
    }

    [WindowsFact]
    public async Task HardLinkedFileIsRejected()
    {
        CreateSource();
        var production = Path.Combine(_base, "production-config.yaml");
        File.WriteAllText(production, "real user config");
        Assert.True(CreateHardLink(Path.Combine(Source, "config.yaml"), production, IntPtr.Zero));
        var copy = await IsolatedCopyBuilder.PrepareAsync(Source, Workspace);
        Assert.Equal(IsolationState.Rejected, copy.State);
        Assert.Equal(EngineErrorCategory.UnsafeLayout, copy.Category);
        Assert.Equal("real user config", File.ReadAllText(production));
    }

    [Fact]
    public void DeleteNeverTouchesFoldersWithoutMarker()
    {
        Directory.CreateDirectory(Source);
        var fake = new IsolatedCopy(IsolationState.Ready, Source, Source, [], 0, EngineErrorCategory.None, "");
        Assert.False(IsolatedCopyBuilder.Delete(fake));
        Assert.True(Directory.Exists(Source));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}

public sealed class FileTreeSnapshotTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vivian-snap-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() => TestDirectories.DeleteWithRetry(_root);

    [Fact]
    public async Task DiffReportsAddedRemovedAndModifiedFiles()
    {
        File.WriteAllText(Path.Combine(_root, "keep.txt"), "same");
        File.WriteAllText(Path.Combine(_root, "change.txt"), "before");
        File.WriteAllText(Path.Combine(_root, "remove.txt"), "x");
        var before = await FileTreeSnapshot.CaptureAsync(_root);
        File.WriteAllText(Path.Combine(_root, "change.txt"), "after!");
        File.Delete(Path.Combine(_root, "remove.txt"));
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
        File.WriteAllText(Path.Combine(_root, "logs", "new.log"), "y");
        var diff = before.Compare(await FileTreeSnapshot.CaptureAsync(_root));
        Assert.Equal(["logs", "logs/new.log"], diff.Added);
        Assert.Equal(["remove.txt"], diff.Removed);
        Assert.Equal(["change.txt"], diff.Modified);
        Assert.False(diff.Incomplete);
        Assert.True(before.Compare(before).IsEmpty);
    }

    [Fact]
    public async Task SnapshotIsBounded()
    {
        for (var i = 0; i < 5; i++) File.WriteAllText(Path.Combine(_root, $"{i}.txt"), "x");
        var snapshot = await FileTreeSnapshot.CaptureAsync(_root, maxEntries: 2);
        Assert.True(snapshot.Truncated);
        Assert.True(snapshot.Compare(snapshot).Incomplete);
    }
}

public sealed class March7thCliParserTests
{
    // SYNTHETIC fixtures: invented to exercise the parsers. They are not observations of the real program.
    [Fact]
    public void VersionParsing()
    {
        Assert.Equal("v2026.10.31", March7thCliParsers.ParseVersion(["", "v2026.10.31", ""]).Version);
        Assert.Equal("v2026.10.31", March7thCliParsers.ParseVersion(["2026.10.31"]).Version);
        Assert.Equal(ParseState.Unsupported, March7thCliParsers.ParseVersion(["March7th 1.0"]).State);
        Assert.Equal(ParseState.Unsupported, March7thCliParsers.ParseVersion(["v2026.10.31", "v2026.9.30"]).State);
        Assert.Equal(ParseState.Empty, March7thCliParsers.ParseVersion([" "]).State);
    }

    [Fact]
    public void StatusParsingKeepsUnknownFieldsUninterpreted()
    {
        var parsed = March7thCliParsers.ParseStatus(["{\"first_run_completed\": true, \"game_path_exists\": false, \"active_run\": {\"task\":\"x\"}, \"extra\": 1}"]);
        Assert.Equal(ParseState.Parsed, parsed.State);
        Assert.True(parsed.FirstRunCompleted);
        Assert.False(parsed.GamePathExists);
        Assert.True(parsed.ActiveRunPresent);
        Assert.Equal(["extra"], parsed.UnknownFields);
        Assert.Contains("尚未", parsed.Detail);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"first_run_completed\": \"yes\", \"game_path_exists\": true, \"active_run\": null}")]
    [InlineData("{\"game_path_exists\": true, \"active_run\": null}")]
    public void UnsupportedStatusOutputIsNeverInterpreted(string text)
    {
        var parsed = March7thCliParsers.ParseStatus([text]);
        Assert.Equal(ParseState.Unsupported, parsed.State);
        Assert.Null(parsed.FirstRunCompleted);
    }

    [Fact]
    public void TaskListIsNeverTurnedIntoTaskIdentifiers()
    {
        var list = March7thCliParsers.ParseTaskList(["[{\"id\":\"daily\"},{\"id\":\"power\"}]"]);
        Assert.Equal(ParseState.Unsupported, list.State);
        Assert.Equal("array", list.TopLevelKind);
        Assert.Equal(2, list.ItemCount);
        Assert.Equal(ParseState.Unsupported, March7thCliParsers.ParseTaskList(["<html>"]).State);
        Assert.Equal(ParseState.Empty, March7thCliParsers.ParseTaskList([]).State);
    }
}

public sealed class RedactorTests
{
    [Fact]
    public void PersonalValuesAndSecretsAreRemoved()
    {
        var redactor = new Redactor(@"C:\Users\Alice", "Alice", "ALICE-PC", ["hunter22"]);
        var text = @"path C:\Users\Alice\Games\m7a.exe on ALICE-PC by alice; other C:\Users\Bob\x; token=abc123 password: ""p w"" mail a.b@example.com Bearer abcdefghijk hunter22";
        var result = redactor.Redact(text);
        Assert.DoesNotContain("Alice", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bob", result);
        Assert.DoesNotContain("abc123", result);
        Assert.DoesNotContain("p w", result);
        Assert.DoesNotContain("example.com", result);
        Assert.DoesNotContain("abcdefghijk", result);
        Assert.DoesNotContain("hunter22", result);
        Assert.Contains(@"%USERPROFILE%\Games\m7a.exe", result);
        Assert.Contains("<machine>", result);
        Assert.Contains(@"C:\Users\Public\", new Redactor(null, null, null).Redact(@"C:\Users\Public\x"));
    }
}

[Collection(ProcessCollection.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class CliDiagnosticRunnerTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "vivian-cli-" + Guid.NewGuid().ToString("N"));
    public void Dispose() => TestDirectories.DeleteWithRetry(_base);

    /// <summary>Permits only the synthetic FakeEngine program whose hash the test supplies.</summary>
    private sealed class SyntheticOnlyGate(string allowedSha) : ICliExecutionGate
    {
        public ExecutionDecision Check(CliOperation operation, string executableSha256, CliAuthorization? authorization) =>
            executableSha256 == allowedSha && authorization?.Operation == operation.Id
                ? new(true, "synthetic", "synthetic test program")
                : new(false, "denied", "not the synthetic program");
    }

    private sealed class CountingSupervisor : IProcessSupervisor
    {
        public int Calls;
        public Task<ISupervisedProcessGroup> StartAsync(ProcessLaunchSpec spec, Guid runId, CancellationToken ct)
        { Interlocked.Increment(ref Calls); throw new InvalidOperationException("must not start"); }
    }

    private async Task<IsolatedCopy> FixtureCopyAsync(string programName = "m7a.exe", string content = "synthetic")
    {
        var source = Directory.CreateDirectory(Path.Combine(_base, "install")).FullName;
        File.WriteAllText(Path.Combine(source, programName), content);
        return await IsolatedCopyBuilder.PrepareAsync(source, Path.Combine(_base, "ws"));
    }

    [Fact]
    public async Task ApplicationGateRefusesUpstreamProgramsEvenWithAuthorization()
    {
        var copy = await FixtureCopyAsync();
        var supervisor = new CountingSupervisor();
        var runner = new CliDiagnosticRunner(supervisor, new BuildCliExecutionGate());
        var sha = copy.Files.Single().Sha256;
        foreach (var auth in new CliAuthorization?[] { null, new(CliOperationId.Status, sha, "tester", DateTimeOffset.UtcNow, "test"), new(CliOperationId.Version, sha, "t", DateTimeOffset.UtcNow, "wrong op") })
        {
            var result = await runner.RunAsync(copy, March7thCliOperations.RelativeExecutable, March7thCliOperations.Status, auth);
            Assert.Equal(CliRunState.Refused, result.State);
            Assert.Null(result.Changes);
        }
        Assert.Equal(0, supervisor.Calls);
    }

    [Fact]
    public async Task PathEscapeMissingCopyAndChangedProgramAreRefused()
    {
        var copy = await FixtureCopyAsync();
        var supervisor = new CountingSupervisor();
        var runner = new CliDiagnosticRunner(supervisor, new SyntheticOnlyGate(copy.Files.Single().Sha256));
        Assert.Equal("path_escape", (await runner.RunAsync(copy, @"..\..\m7a.exe", March7thCliOperations.Status, null)).ReasonCode);
        Assert.Equal("path_escape", (await runner.RunAsync(copy, "../../m7a.exe", March7thCliOperations.Status, null)).ReasonCode);
        Assert.Equal("isolation_not_ready", (await runner.RunAsync(copy with { State = IsolationState.Rejected }, "m7a.exe", March7thCliOperations.Status, null)).ReasonCode);
        File.WriteAllText(Path.Combine(copy.Root!, "m7a.exe"), "tampered");
        Assert.Equal("isolated_copy_changed", (await runner.RunAsync(copy, "m7a.exe", March7thCliOperations.Status, null)).ReasonCode);
        Assert.Equal(0, supervisor.Calls);
    }

    private async Task<(IsolatedCopy Copy, CliDiagnosticRunner Runner, string Sha)> SyntheticEngineAsync()
    {
        var source = Directory.CreateDirectory(Path.Combine(_base, "fake-install")).FullName;
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "VIvian.FakeEngine*"))
            File.Copy(file, Path.Combine(source, Path.GetFileName(file)));
        var copy = await IsolatedCopyBuilder.PrepareAsync(source, Path.Combine(_base, "ws"));
        Assert.Equal(IsolationState.Ready, copy.State);
        var sha = copy.Files.Single(f => f.RelativePath == "VIvian.FakeEngine.exe").Sha256;
        return (copy, new CliDiagnosticRunner(new WindowsProcessSupervisor(), new SyntheticOnlyGate(sha)), sha);
    }

    private static readonly Dictionary<string, string> SyntheticCli = new() { ["VIVIAN_FAKE_SCENARIO"] = "synthetic-cli" };

    [WindowsFact]
    public async Task SyntheticStatusRecordsOutputExitCodeCleanupAndFileChanges()
    {
        var (copy, runner, sha) = await SyntheticEngineAsync();
        var auth = new CliAuthorization(CliOperationId.Status, sha, @"C:\Users\Tester", DateTimeOffset.UtcNow, "synthetic test");
        var result = await runner.RunAsync(copy, "VIvian.FakeEngine.exe", March7thCliOperations.Status, auth, CliRunLimits.Default, SyntheticCli, default);

        Assert.Equal(CliRunState.Completed, result.State);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(CleanupState.Confirmed, result.Cleanup);
        Assert.Equal(["config.yaml"], result.Changes!.Added);
        Assert.Empty(result.Changes.Removed);
        Assert.DoesNotContain(".synthetic_write_probe", result.Changes.Added); // created and deleted: invisible to snapshots
        var parsed = March7thCliParsers.ParseStatus(result.Output.Where(l => l.StartsWith('{')).ToList());
        Assert.Equal(ParseState.Parsed, parsed.State);
        Assert.Equal(["synthetic"], parsed.UnknownFields);

        var json = CliEvidenceExporter.ToJson(EngineCatalog.March7th, result, parsed, new Redactor(@"C:\Users\Tester", "Tester", null), synthetic: true);
        Assert.Contains("\"synthetic\": true", json);
        Assert.Contains("\"verificationStatus\": \"synthetic-fixture\"", json);
        Assert.Contains("config.yaml", json);
        Assert.DoesNotContain("Tester", json);
    }

    [WindowsFact]
    public async Task SyntheticListCapturesStandardErrorAndKeepsTaskListUnsupported()
    {
        var (copy, runner, sha) = await SyntheticEngineAsync();
        var result = await runner.RunAsync(copy, "VIvian.FakeEngine.exe", March7thCliOperations.TaskList,
            new(CliOperationId.TaskList, sha, "t", DateTimeOffset.UtcNow, "synthetic"), CliRunLimits.Default, SyntheticCli, default);
        Assert.Equal(CliRunState.Completed, result.State);
        Assert.Contains("SYNTHETIC stderr line", result.Output);
        Assert.Equal(ParseState.Unsupported, March7thCliParsers.ParseTaskList(result.Output.Where(l => l.StartsWith('[')).ToList()).State);
    }

    [WindowsFact]
    public async Task HangingCommandTimesOutAndIsCleanedUp()
    {
        var (copy, runner, sha) = await SyntheticEngineAsync();
        var result = await runner.RunAsync(copy, "VIvian.FakeEngine.exe", March7thCliOperations.Version,
            new(CliOperationId.Version, sha, "t", DateTimeOffset.UtcNow, "synthetic"),
            CliRunLimits.Default with { Timeout = TimeSpan.FromSeconds(2) },
            new Dictionary<string, string> { ["VIVIAN_FAKE_SCENARIO"] = "hang-silent" }, default);
        Assert.Equal(CliRunState.TimedOut, result.State);
        Assert.Null(result.ExitCode);
        Assert.Equal(CleanupState.Confirmed, result.Cleanup);
    }

    [WindowsFact]
    public async Task CancellationStopsTheCommand()
    {
        var (copy, runner, sha) = await SyntheticEngineAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var result = await runner.RunAsync(copy, "VIvian.FakeEngine.exe", March7thCliOperations.Version,
            new(CliOperationId.Version, sha, "t", DateTimeOffset.UtcNow, "synthetic"), CliRunLimits.Default,
            new Dictionary<string, string> { ["VIVIAN_FAKE_SCENARIO"] = "hang-silent" }, cts.Token);
        Assert.Equal(CliRunState.Cancelled, result.State);
        Assert.Equal(CleanupState.Confirmed, result.Cleanup);
    }

    [WindowsFact]
    public async Task UnknownArgumentsGiveNonZeroExitAndOrphanChildIsCleaned()
    {
        var (copy, runner, sha) = await SyntheticEngineAsync();
        var bad = new CliOperation(CliOperationId.Version, ["--bogus"], CapabilityStatus.Unsupported, EngineCapabilityId.VersionQuery, "synthetic");
        var result = await runner.RunAsync(copy, "VIvian.FakeEngine.exe", bad,
            new(CliOperationId.Version, sha, "t", DateTimeOffset.UtcNow, "synthetic"), CliRunLimits.Default, SyntheticCli, default);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(ParseState.Unsupported, March7thCliParsers.ParseVersion(result.Output).State);

        var orphan = await runner.RunAsync(copy, "VIvian.FakeEngine.exe", March7thCliOperations.Version,
            new(CliOperationId.Version, sha, "t", DateTimeOffset.UtcNow, "synthetic"), CliRunLimits.Default,
            new Dictionary<string, string> { ["VIVIAN_FAKE_SCENARIO"] = "orphan-child" }, default);
        Assert.Equal(CliRunState.Completed, orphan.State);
        Assert.Equal(CleanupState.Confirmed, orphan.Cleanup);
    }
}
