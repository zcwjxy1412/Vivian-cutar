using VIvian.Infrastructure.Processes;
using Xunit;

namespace VIvian.Tests.Processes;

public sealed class DesktopOperationLeaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vivian-desktop-").FullName;

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
        Assert.Throws<InvalidOperationException>(() => Acquire("zzz"));
        Assert.Throws<InvalidOperationException>(() => Acquire("genshin", true));
        using (var recovery = Acquire("zzz", true))
        {
            Assert.True(recovery.Recovering);
            recovery.ConfirmClean();
        }
        using var next = Acquire("genshin");
        Assert.False(next.Recovering);
    }

    private DesktopOperationLease Acquire(string profile, bool recovery = false) =>
        DesktopOperationLease.AcquireAt(Path.Combine(_root, "desktop.lock"), Path.Combine(_root, profile), recovery);

    public void Dispose() => TestDirectories.DeleteWithRetry(_root);
}
