using VIvian.Core.Execution;

namespace VIvian.App;

/// <summary>Window-owned permission; passive activation never acquires desktop access.</summary>
public sealed class AssistantInputSession(IAssistantInputGate? gate) : ObservableObject, IDisposable
{
    private IDisposable? _permission;
    private bool _canEdit;
    private string _hint = "请点击“进入聊天”取得安全输入许可。";
    public bool CanEdit { get => _canEdit; private set => Set(ref _canEdit, value); }
    public string Hint { get => _hint; private set => Set(ref _hint, value); }

    public bool TryEnter()
    {
        if (_permission is not null) return true;
        string? reason = null;
        if (gate is null || !gate.TryAcquire(out _permission, out reason) || _permission is null)
        {
            Hint = reason ?? "安全输入许可尚未接通，聊天保持只读。";
            return false;
        }
        CanEdit = true;
        Hint = "可输入 · 切离窗口后自动锁定；运行前将释放输入许可。";
        return true;
    }

    public void Dispose()
    {
        // Disable controls synchronously before allowing another desktop owner to start.
        CanEdit = false;
        var permission = _permission;
        _permission = null;
        permission?.Dispose();
        Hint = "聊天已锁定。桌面空闲且无需恢复时，可点击“进入聊天”。";
    }
}
