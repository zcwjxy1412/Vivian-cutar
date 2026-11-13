using VIvian.Core.Execution;

namespace VIvian.Infrastructure.Processes;

/// <summary>Idle-only input permission. Pausing has not been validated as safe keyboard/mouse release.</summary>
public sealed class AssistantInputGate : IAssistantInputGate
{
    private readonly Func<bool> blocked;
    private readonly Func<DesktopOperationLease> acquire;

    public AssistantInputGate(string profileDirectory, Func<bool> blocked)
        : this(blocked, () => DesktopOperationLease.Acquire(profileDirectory)) { }

    internal AssistantInputGate(Func<bool> blocked, Func<DesktopOperationLease> acquire)
    {
        this.blocked = blocked;
        this.acquire = acquire;
    }

    public bool TryAcquire(out IDisposable? permission, out string? reason)
    {
        permission = null;
        reason = "请先停止任务并确认桌面及配置恢复，再使用聊天输入。";
        DesktopOperationLease? lease = null;
        try
        {
            if (blocked()) return false;
            lease = acquire();
            // Recheck after acquiring the same OS lease used by task starts, closing the idle-check race.
            if (blocked()) return false;
            permission = lease;
            lease = null;
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally { lease?.Dispose(); }
    }
}
