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
