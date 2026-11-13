using VIvian.Infrastructure.Processes;
using Xunit;

namespace VIvian.Tests.Processes;

public sealed class AssistantInputGateTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("vivian-input-gate-").FullName;
    private DesktopOperationLease Acquire(string owner = "chat") =>
        DesktopOperationLease.AcquireAt(Path.Combine(root, "desktop.lock"), Path.Combine(root, owner));

    [Fact]
    public void InputPermissionExcludesDesktopStartUntilReleased()
    {
        var gate = new AssistantInputGate(() => false, () => Acquire());
        Assert.True(gate.TryAcquire(out var permission, out var reason));
        Assert.Null(reason);
        Assert.NotNull(permission);
        Assert.Throws<InvalidOperationException>(() => Acquire("game"));
        permission.Dispose();
        using var game = Acquire("game");
        Assert.False(game.Recovering);
    }

    [Fact]
    public void RunningOrRecoveryStateNeverAcquiresInput()
    {
        var attempted = false;
        var gate = new AssistantInputGate(() => true, () => { attempted = true; return Acquire(); });
        Assert.False(gate.TryAcquire(out var permission, out var reason));
        Assert.Null(permission);
        Assert.False(attempted);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void BusyDesktopAndDirtyMarkerRemainBlocked()
    {
        var gate = new AssistantInputGate(() => false, () => Acquire());
        using (var game = Acquire("game"))
        {
            Assert.False(gate.TryAcquire(out var busy, out _));
            Assert.Null(busy);
            game.MarkDirty();
        }
        Assert.False(gate.TryAcquire(out var dirty, out _));
        Assert.Null(dirty);
        Assert.Throws<DesktopRecoveryRequiredException>(() => Acquire("game"));
    }

    [Fact]
    public void StateChangedDuringAcquisitionReleasesPermission()
    {
        var blocked = false;
        var gate = new AssistantInputGate(() => blocked, () => { var lease = Acquire(); blocked = true; return lease; });
        Assert.False(gate.TryAcquire(out var permission, out _));
        Assert.Null(permission);
        using var game = Acquire("game");
    }

    public void Dispose() => Directory.Delete(root, true);
}
