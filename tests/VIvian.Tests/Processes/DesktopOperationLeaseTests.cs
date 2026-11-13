using System.Diagnostics;
using System.Text;
using VIvian.Infrastructure.Processes;
using Xunit;

namespace VIvian.Tests.Processes;

public sealed class DesktopOperationLeaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vivian-desktop-").FullName;

    [Fact]
    public void SharedPathUsesTheUserProfileOutsidePackagedAppData()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(profile, ".vivian-m6", "desktop-operation.lock"), DesktopOperationLease.LockPath);
    }

    [Fact]
    public void DifferentProfilesCannotOverlapAndCleanHandoffReleasesTheDesktop()
    {
        using (var first = Acquire("ai"))
        {
            first.MarkDirty();
            Assert.Throws<InvalidOperationException>(() => Acquire("genshin"));
            first.ConfirmClean();
        }
        using var next = Acquire("genshin");
        Assert.False(next.Recovering);
    }

    [Fact]
    public void UnconfirmedCleanupBlocksAllNewRunsAndOnlyOwningProfileCanRecover()
    {
        using (var first = Acquire("zzz")) first.MarkDirty();
        Assert.Throws<DesktopRecoveryRequiredException>(() => Acquire("zzz"));
        Assert.Throws<DesktopRecoveryRequiredException>(() => Acquire("genshin", true));
        using (var recovery = Acquire("zzz", true))
        {
            Assert.True(recovery.Recovering);
            recovery.ConfirmClean();
        }
        using var next = Acquire("genshin");
        Assert.False(next.Recovering);
    }

    [Fact]
    public void ElevatedModeCannotCreateTheLockOrItsDirectory()
    {
        var missing = Path.Combine(_root, "missing", "desktop.lock");
        Assert.Throws<InvalidOperationException>(() =>
            DesktopOperationLease.AcquireAt(missing, _root, requireExisting: true));
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [Fact]
    public void FailedRecoveryDoesNotReleaseTheDirtyMarker()
    {
        using (var first = Acquire("starrail")) first.MarkDirty();
        Assert.Throws<IOException>((Action)(() =>
        {
            using var recovery = Acquire("starrail", true);
            Assert.True(recovery.Recovering);
            throw new IOException("Simulated configuration restore failure");
        }));
        Assert.Throws<DesktopRecoveryRequiredException>(() => Acquire("starrail"));
        Assert.Throws<DesktopRecoveryRequiredException>(() => Acquire("other", true));
        using (var recovery = Acquire("starrail", true)) recovery.ConfirmClean();
        using var next = Acquire("other");
        Assert.False(next.Recovering);
    }

    [WindowsFact]
    public async Task LeaseExcludesASecondProcessUntilConfirmedCleanAndDisposed()
    {
        // A separate OS process probes the same file, so a process-local lock cannot satisfy this test.
        using (var first = Acquire("first-data-directory"))
        {
            first.MarkDirty();
            Assert.Equal(23, await ProbeFromAnotherProcess());
            first.ConfirmClean();
            Assert.Equal(23, await ProbeFromAnotherProcess());
        }
        Assert.Equal(0, await ProbeFromAnotherProcess());
        using var next = Acquire("second-data-directory");
        Assert.False(next.Recovering);
    }

    private async Task<int> ProbeFromAnotherProcess()
    {
        const string script = """
            try {
                $file = [System.IO.File]::Open($env:VIVIAN_TEST_LOCK, 'Open', 'ReadWrite', 'ReadWrite')
                $file.Dispose()
                exit 0
            } catch [System.IO.IOException] { exit 23 }
              catch { exit 24 }
            """;
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        start.Environment["VIVIAN_TEST_LOCK"] = Path.Combine(_root, "desktop.lock");
        using var process = Process.Start(start)!;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(); // Only the exact harmless probe created by this test.
                await process.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public void LockWithAHardLinkIsRejectedBeforeWriting()
    {
        if (!OperatingSystem.IsWindows()) return;
        var original = Path.Combine(_root, "unrelated.txt");
        var link = Path.Combine(_root, "desktop.lock");
        File.WriteAllText(original, "unchanged");
        Assert.True(CreateHardLink(link, original, IntPtr.Zero));
        Assert.Throws<InvalidOperationException>(() =>
            DesktopOperationLease.AcquireAt(link, _root, recovery: true, requireExisting: true));
        Assert.Equal("unchanged", File.ReadAllText(original));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string target, IntPtr securityAttributes);

    private DesktopOperationLease Acquire(string profile, bool recovery = false) =>
        DesktopOperationLease.AcquireAt(Path.Combine(_root, "desktop.lock"), Path.Combine(_root, profile), recovery);

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
