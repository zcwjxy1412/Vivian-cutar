namespace VIvian.Core.Execution;

/// <summary>Holding a permission excludes real desktop work. A denied request never authorizes focus or input.</summary>
public interface IAssistantInputGate
{
    bool TryAcquire(out IDisposable? permission, out string? reason);
}
