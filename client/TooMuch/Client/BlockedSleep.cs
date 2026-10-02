namespace TooMuch.Client;

// Uses monotonic service time, not the wall clock or the usage-accounting clock.
internal sealed class BlockedSleep
{
    private static readonly TimeSpan Delay = TimeSpan.FromMinutes(5);
    private TimeSpan? nextAttempt;

    public void RestartDelay(TimeSpan now)
    {
        if (nextAttempt.HasValue) nextAttempt = now + Delay;
    }

    public void Tick(TimeSpan now, bool blocked, bool disconnectedChild,
        Action sleep, Action<Exception> log)
    {
        if (!blocked)
        {
            nextAttempt = null;
            return;
        }
        // A disconnected session disappears from ActiveChild. Keep the pending
        // deadline, but don't put an unused PC to sleep just because policy is locked.
        if (!nextAttempt.HasValue && disconnectedChild) nextAttempt = now + Delay;
        if (!nextAttempt.HasValue || now < nextAttempt.Value) return;

        nextAttempt = now + Delay;
        try { sleep(); }
        catch (Exception ex)
        {
            nextAttempt = now + TimeSpan.FromMinutes(1);
            log(ex);
        }
    }
}
