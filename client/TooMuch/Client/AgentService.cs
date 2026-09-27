using System.Diagnostics;
using System.ServiceProcess;
using TooMuch.Core;

namespace TooMuch.Client;

internal sealed class AgentService : ServiceBase
{
    private CancellationTokenSource? stop;
    private Task? worker;
    private int resetClock;

    public AgentService()
    {
        ServiceName = "TooMuch";
        CanHandlePowerEvent = true;
        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        var config = Program.LoadConfig();
        stop = new CancellationTokenSource();
        worker = Task.Run(() => Run(config, stop.Token));
    }

    protected override void OnStop()
    {
        stop?.Cancel();
        if (worker != null && !worker.Wait(TimeSpan.FromSeconds(15)))
            throw new System.TimeoutException("Service worker did not stop.");
        stop?.Dispose();
    }
    protected override void OnShutdown() => OnStop();
    protected override bool OnPowerEvent(PowerBroadcastStatus status)
    { Interlocked.Exchange(ref resetClock, 1); return true; }
    protected override void OnSessionChange(SessionChangeDescription change)
    { Interlocked.Exchange(ref resetClock, 1); }

    private void Log(Exception error)
    {
        try { EventLog.WriteEntry(error.ToString(), EventLogEntryType.Error); } catch { }
    }

    private async Task Run(AgentConfig config, CancellationToken ct)
    {
        try
        {
            using var agent = new Agent(config, logPersistenceError: Log);
            // Do not disconnect a freshly installed child session because the
            // empty in-memory policy evaluates fail-closed before first poll.
            // If the network is unavailable, Poll returns after timeout and
            // the normal fail-closed enforcement uses whatever policy exists.
            if (!agent.HasPolicy) await Poll(agent, ct);
            var messages = new SessionMessages(config, Log);
            var publishedStatus = new StatusSnapshotStore();
            var pipeServer = Task.Run(() => StatusPipe.Serve(config.ChildSid, publishedStatus.Read, Log, ct));
            var clock = new UsageClock();
            var watch = Stopwatch.StartNew();
            Task network = Task.CompletedTask;
            var nextPoll = TimeSpan.Zero;
            var nextBeat = TimeSpan.Zero;
            while (!ct.IsCancellationRequested)
            {
                // Enumeration failure is fatal: let SCM recovery restart the service
                // instead of silently skipping enforcement forever. Per-session errors
                // are isolated by ScanSessions; only verified child sessions are targeted.
                var sessionScan = SessionGuard.ScanSessions(config.ChildSid, Log);
                try
                {
                    if (Interlocked.Exchange(ref resetClock, 0) != 0) clock.Reset();
                    var oldDay = agent.TodayKey;
                    agent.TickDayRollover();
                    if (oldDay != agent.TodayKey) clock.Reset();
                    var sessions = sessionScan.Unlocked;
                    var decision = PolicyEvaluator.Evaluate(agent.Policy, agent.LocalNow, agent.ActiveMinToday);
                    var counting = sessions.Count > 0 && decision.State != State.Locked;
                    var seconds = clock.Sample(watch.Elapsed, counting);
                    if (seconds > 0)
                    {
                        agent.AddSeconds(seconds);
                    }
                    decision = PolicyEvaluator.Evaluate(agent.Policy, agent.LocalNow, agent.ActiveMinToday);
                    // A tick can reach the limit; publish the post-tick state.
                    counting = sessions.Count > 0 && decision.State != State.Locked;
                    publishedStatus.Publish(ChildStatus.Create(agent.Policy, agent.LocalNow, agent.ActiveSeconds, DateTimeOffset.UtcNow));
                    if (decision.State == State.Locked)
                    {
                        foreach (var session in sessionScan.ActiveChild)
                        {
                            try { SessionGuard.Disconnect(session); }
                            catch (Exception ex) { Log(ex); }
                        }
                    }
                    else messages.Tick(agent, sessions, ct);

                    // HTTP cannot delay session enforcement. Only one network operation at a time.
                    if (network.IsCompleted)
                    {
                        if (watch.Elapsed >= nextPoll)
                        {
                            nextPoll = watch.Elapsed + TimeSpan.FromSeconds(15);
                            network = Poll(agent, ct);
                        }
                        else if (watch.Elapsed >= nextBeat)
                        {
                            nextBeat = watch.Elapsed + TimeSpan.FromSeconds(30);
                            network = Beat(agent, decision.State == State.Locked, counting, ct);
                        }
                    }
                }
                catch (Exception ex) { clock.Reset(); Log(ex); }
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
            await network;
            await pipeServer;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log(ex);
            // Nonzero termination lets SCM recovery restart a failed worker.
            Environment.Exit(1);
        }
    }

    private async Task Poll(Agent agent, CancellationToken ct)
    { try { await agent.PollConfigAsync(ct); } catch (Exception ex) { if (!ct.IsCancellationRequested) Log(ex); } }
    private async Task Beat(Agent agent, bool locked, bool counting, CancellationToken ct)
    { try { await agent.SendHeartbeatAsync(locked, counting, ct); } catch (Exception ex) { if (!ct.IsCancellationRequested) Log(ex); } }
}
