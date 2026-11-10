namespace VIvian.Infrastructure.Engines.BetterGi;

internal readonly record struct BetterGiResinSession(Guid RunId, int GameProcessId, DateTimeOffset ProcessStartedAt)
{
    internal bool IsBound => RunId != Guid.Empty && GameProcessId > 0 && ProcessStartedAt != default;
}

// A trusted game capture and input producer must assign the sequence and monotonic timestamp in the same stream.
// BetterGI's unmodified file logs cannot supply these observations.
internal readonly record struct BetterGiResinEvent(BetterGiResinSession Session, long Sequence, long Timestamp);

internal readonly record struct BetterGiResinInventory(BetterGiResinEvent Event, int? Original, int? Condensed);

internal enum BetterGiCraftAction { None, Attempt, Skipped, Conflict }

internal static class BetterGiResinSafety
{
    private const int CondensedCapacity = 5;
    private const int OriginalPerCraft = 60;

    internal static bool CraftVerified(BetterGiResinSession session, BetterGiResinInventory before, BetterGiResinEvent submission,
        BetterGiResinInventory after, int expectedCount, int minimumReserve)
    {
        if (!ValidInventoryChange(session, before, submission, after) || expectedCount is < 1 or > CondensedCapacity || minimumReserve < 0)
            return false;

        return after.Condensed - before.Condensed == expectedCount
            && before.Original - after.Original == expectedCount * OriginalPerCraft
            && after.Original >= minimumReserve;
    }

    /// <summary>The stock engine's attempt needs an independent inventory delta. A logged skip also needs proof.</summary>
    internal static int? ConfirmStockCraft(BetterGiResinSession session, BetterGiResinInventory before, BetterGiResinEvent observedAction,
        BetterGiResinInventory after, BetterGiCraftAction action)
    {
        if (!ValidInventoryChange(session, before, observedAction, after)) return null;
        var created = after.Condensed - before.Condensed;
        return action switch
        {
            BetterGiCraftAction.Attempt when created is >= 1 && before.Original - after.Original == created * OriginalPerCraft => created,
            BetterGiCraftAction.Skipped when created == 0 && before.Original == after.Original &&
                (before.Condensed == CondensedCapacity || before.Original < OriginalPerCraft) => 0,
            _ => null,
        };
    }

    private static bool ValidInventoryChange(BetterGiResinSession session, BetterGiResinInventory before, BetterGiResinEvent action,
        BetterGiResinInventory after) => session.IsBound && before.Event.Session == session && action.Session == session &&
        after.Event.Session == session && before.Event.Sequence > 0 && before.Event.Sequence < action.Sequence &&
        action.Sequence < after.Event.Sequence && before.Event.Timestamp < action.Timestamp && action.Timestamp < after.Event.Timestamp &&
        before.Original is >= 0 && after.Original is >= 0 && before.Condensed is >= 0 and <= CondensedCapacity &&
        after.Condensed is >= 0 and <= CondensedCapacity;
}

internal enum BetterGiResinKind { Unknown, Original20, Original40, Condensed }

// Amount is the displayed original resin cost or the displayed condensed item count.
internal readonly record struct BetterGiResinOffer(BetterGiResinEvent Event, BetterGiResinKind Kind, int Amount);

internal sealed class BetterGiDomainSpendBudget
{
    private static readonly TimeSpan MaxOfferAge = TimeSpan.FromSeconds(1);
    private readonly Lock _gate = new();
    private readonly BetterGiResinSession _session;
    private readonly TimeProvider _time;
    private readonly Dictionary<BetterGiResinKind, int> _remaining;
    private long _lastOffer;
    private bool _uncertain;

    internal BetterGiDomainSpendBudget(BetterGiResinSession session, BetterGiDomainResinPlan plan, TimeProvider time)
    {
        if (!session.IsBound)
            throw new ArgumentException("树脂预算需要绑定本次运行的原神进程。", nameof(session));
        if (!plan.HasExplicitBudget || plan.Original != 0 || plan.Transient != 0 || plan.Fragile != 0 || plan.AutoArtifactSalvage)
            throw new ArgumentException("仅支持明确指定的 20、40 原粹树脂及浓缩树脂次数。", nameof(plan));

        ArgumentNullException.ThrowIfNull(time);
        _session = session;
        _time = time;
        _remaining = new()
        {
            [BetterGiResinKind.Original20] = plan.Original20,
            [BetterGiResinKind.Original40] = plan.Original40,
            [BetterGiResinKind.Condensed] = plan.Condensed,
        };
    }

    // Dispatch must send exactly one input for the verified offer. Reserve before dispatch because a failed or
    // interrupted input can already have spent resin. A potential direct claim with no known offer stays blocked.
    internal bool TryDispatch(BetterGiResinOffer offer, Func<bool> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        lock (_gate)
        {
            if (_uncertain || offer.Event.Session != _session || offer.Event.Sequence <= _lastOffer)
                return false;
            _lastOffer = offer.Event.Sequence;

            var now = _time.GetTimestamp();
            if (offer.Event.Timestamp > now || _time.GetElapsedTime(offer.Event.Timestamp, now) > MaxOfferAge)
                return false;
            var amount = offer.Kind switch
            {
                BetterGiResinKind.Original20 => 20,
                BetterGiResinKind.Original40 => 40,
                BetterGiResinKind.Condensed => 1,
                _ => 0,
            };
            if (amount == 0 || offer.Amount != amount || _remaining.GetValueOrDefault(offer.Kind) == 0)
                return false;

            _remaining[offer.Kind]--;
            _uncertain = true;
            if (!dispatch()) return false;
            _uncertain = false;
            return true;
        }
    }
}
