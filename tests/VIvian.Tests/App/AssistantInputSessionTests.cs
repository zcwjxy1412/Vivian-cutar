using VIvian.App;
using VIvian.Core.Execution;
using Xunit;

namespace VIvian.Tests.App;

public sealed class AssistantInputSessionTests
{
    [Fact]
    public void PassiveConstructionAndMissingGateNeverEnableInput()
    {
        var gate = new Gate();
        using var session = new AssistantInputSession(gate);
        Assert.False(session.CanEdit);
        Assert.Equal(0, gate.Calls);
        using var unavailable = new AssistantInputSession(null);
        Assert.False(unavailable.TryEnter());
        Assert.False(unavailable.CanEdit);
    }

    [Fact]
    public void ExplicitEntryHoldsOnePermissionUntilInputIsDisabled()
    {
        var gate = new Gate();
        using var session = new AssistantInputSession(gate);
        gate.OnRelease = () => Assert.False(session.CanEdit);
        Assert.True(session.TryEnter());
        Assert.True(session.CanEdit);
        Assert.True(session.TryEnter());
        Assert.Equal(1, gate.Calls);
        session.Dispose();
        session.Dispose();
        Assert.Equal(1, gate.Releases);
        Assert.False(session.CanEdit);
        Assert.True(session.TryEnter());
        Assert.Equal(2, gate.Calls);
    }

    [Fact]
    public void RunningOrRecoveryDenialRemainsReadOnlyAfterReturningToWindow()
    {
        var gate = new Gate();
        using var session = new AssistantInputSession(gate);
        Assert.True(session.TryEnter());
        session.Dispose();
        gate.Allowed = false;
        Assert.False(session.TryEnter());
        Assert.False(session.CanEdit);
        Assert.Contains("运行", session.Hint);
    }

    private sealed class Gate : IAssistantInputGate
    {
        public bool Allowed = true;
        public int Calls, Releases;
        public Action? OnRelease;
        public bool TryAcquire(out IDisposable? permission, out string? reason)
        {
            Calls++;
            permission = Allowed ? new Permission(() => { Releases++; OnRelease?.Invoke(); }) : null;
            reason = Allowed ? null : "运行或恢复中，请先停止并完成恢复。";
            return Allowed;
        }
        private sealed class Permission(Action release) : IDisposable { public void Dispose() => release(); }
    }
}
