using VIvian.Core.Accounts;

namespace VIvian.Infrastructure.Engines.BetterGi;

public enum GenshinLoginStepKind
{
    EnterSavedAccount,
    InvalidTarget,
    TargetNotVisible,
    AmbiguousAccount,
    NeedsUser,
    UnrecognizedScreen,
    WorldUnverified,
}

public sealed record GenshinLoginStep(GenshinLoginStepKind Kind, ScreenRegion? ActionRegion = null);

/// <summary>Plans at most one saved-card entry per attempt. A proposal carries no proof of input or world entry.</summary>
public sealed class GenshinLoginSession(string? targetMaskedPhone)
{
    private readonly string _target = GenshinLoginScreen.NormalizeExactMask(targetMaskedPhone);
    private readonly object _gate = new();
    private GenshinLoginStep? _stop;
    private bool _entryProposed;

    public GenshinLoginStep Next(GenshinLoginScreen screen)
    {
        lock (_gate)
        {
            if (_stop is not null) return _stop;
            if (screen.State == GenshinLoginScreenState.NeedsUser)
                return _stop = new(GenshinLoginStepKind.NeedsUser);
            if (_entryProposed)
                return _stop = new(GenshinLoginStepKind.WorldUnverified);
            if (_target.Length == 0)
                return _stop = new(GenshinLoginStepKind.InvalidTarget);
            if (screen.State == GenshinLoginScreenState.AmbiguousAccount)
                return _stop = new(GenshinLoginStepKind.AmbiguousAccount);
            if (screen.Account is null || screen.EnterLabel is null)
                return _stop = new(GenshinLoginStepKind.UnrecognizedScreen);
            if (screen.Account.NormalizedPhone != _target)
                return _stop = new(GenshinLoginStepKind.TargetNotVisible);

            _entryProposed = true;
            return new(GenshinLoginStepKind.EnterSavedAccount, screen.EnterLabel);
        }
    }
}
