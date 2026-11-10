namespace VIvian.Infrastructure.Engines.BetterGi;

internal sealed class GenshinResinReadiness(TimeProvider clock)
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan ReturnToWorldTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan VerificationTimeout = Timeout + ReturnToWorldTimeout + TimeSpan.FromSeconds(60);
    private readonly long _started = clock.GetTimestamp();
    private (int Original, int Condensed)? _previous;
    internal bool Expired => clock.GetElapsedTime(_started) >= Timeout;
    internal bool Observe((int Original, int Condensed)? counts)
    {
        if (Expired) return false;
        var ready = counts is not null && counts == _previous;
        _previous = counts;
        return ready;
    }
}
