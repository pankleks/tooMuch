using TooMuch.Core;

namespace TooMuch.Client;

internal static class UsageAccounting
{
    // This is the service's actual accounting path, also exercised by regression tests.
    // Re-evaluate after charging: the current tick can reach the daily limit.
    public static (Decision Decision, bool Counting) Tick(Agent agent, UsageClock clock,
        TimeSpan now, bool activeChildSession)
    {
        var decision = PolicyEvaluator.Evaluate(agent.Policy, agent.LocalNow, agent.ActiveMinToday);
        var seconds = clock.Sample(now, activeChildSession && decision.State != State.Locked);
        if (seconds > 0) agent.AddSeconds(seconds);
        decision = PolicyEvaluator.Evaluate(agent.Policy, agent.LocalNow, agent.ActiveMinToday);
        return (decision, activeChildSession && decision.State != State.Locked);
    }
}
