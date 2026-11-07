using VIvian.Core.Accounts;

namespace VIvian.Infrastructure.Accounts;

public sealed record SessionVerification(bool InGame, string? Uid, double Confidence, string Detail);

public interface IGameSessionVerifier
{
    Task<SessionVerification> VerifyAsync(CancellationToken cancellationToken);
}

/// <summary>Confirms the game world, not just the absence of a dialog. UID OCR is the strongest signal we have.</summary>
public sealed class StarRailSessionVerifier(LoginScreenReader reader) : IGameSessionVerifier
{
    public async Task<SessionVerification> VerifyAsync(CancellationToken cancellationToken)
    {
        var view = await reader.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (view.Window is null) return new SessionVerification(false, null, 0, "没有星铁游戏窗口。");
        if (view.LoginDialogVisible) return new SessionVerification(false, null, 0, "仍停留在登录对话框。");
        if (view.Frame is null) return new SessionVerification(false, null, 0, "游戏画面捕获失败。");

        var full = await WindowsOcrReader.ReadAsync(view.Frame, cancellationToken).ConfigureAwait(false);
        if (!full.Available) return new SessionVerification(false, null, 0, full.Error ?? "本地 OCR 不可用。");
        var uid = GameUidParser.Find(full.Lines);
        if (uid is not null) return new SessionVerification(true, uid, 0.95, $"已进入游戏，UID {uid}。");
        if (GameUidParser.LooksInGame(full.Lines))
            return new SessionVerification(true, null, 0.78, "已进入游戏界面（未读取到 UID）。");
        return new SessionVerification(false, null, 0.2, "暂未识别到游戏内界面。");
    }
}
