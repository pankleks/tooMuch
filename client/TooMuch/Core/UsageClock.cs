namespace TooMuch.Core;

// Both endpoints must belong to the same unlocked session interval.
// Long gaps (sleep, blocked thread, suspend) are never charged retroactively.
public sealed class UsageClock
{
    private TimeSpan? previous;
    private bool wasActive;

    public double Sample(TimeSpan now, bool active)
    {
        var elapsed = previous.HasValue ? now - previous.Value : TimeSpan.Zero;
        previous = now;
        var charge = active && wasActive && elapsed > TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(5);
        wasActive = active;
        return charge ? elapsed.TotalSeconds : 0;
    }

    public void Reset() { previous = null; wasActive = false; }
}
