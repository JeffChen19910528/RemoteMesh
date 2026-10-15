using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport.Reliability;

namespace RemoteDesktop.Transport.Tests;

public sealed class ReliabilityTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan d) => _now += d;
    }

    private static (LoopbackChannel a, LoopbackChannel b) ControlPair()
    {
        var a = new LoopbackChannel(ChannelType.Control);
        var b = new LoopbackChannel(ChannelType.Control);
        a.PeerWriter = b.Writer;
        b.PeerWriter = a.Writer;
        return (a, b);
    }

    private static async Task<ReadOnlyMemory<byte>> Next(IChannel ch)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var e = ch.ReceiveAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await e.MoveNextAsync());
        return e.Current;
    }

    // ── KeepAlive ────────────────────────────────────────────────────────

    [Fact]
    public async Task KeepAlive_PingPong_MeasuresRtt()
    {
        var clock = new ManualClock();
        var (chA, chB) = ControlPair();
        var a = new KeepAliveService(chA, clock: clock);
        var b = new KeepAliveService(chB, clock: clock);
        var measured = new List<TimeSpan>();
        a.RttMeasured += measured.Add;

        await a.TickAsync();                         // A sends ping
        await b.HandleAsync(await Next(chB), default); // B answers immediately
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await a.HandleAsync(await Next(chA), default);

        Assert.Equal(TimeSpan.FromMilliseconds(50), a.LastRtt);
        Assert.Single(measured);
        Assert.False(a.IsLost);
    }

    [Fact]
    public async Task KeepAlive_SilentPeer_RaisesLostOnce()
    {
        var clock = new ManualClock();
        var (chA, _) = ControlPair();
        var a = new KeepAliveService(chA, new KeepAliveOptions { Timeout = TimeSpan.FromSeconds(15) }, clock);
        var lost = 0;
        a.ConnectionLost += () => lost++;

        await a.TickAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        await a.TickAsync();
        Assert.False(a.IsLost);

        clock.Advance(TimeSpan.FromSeconds(10)); // 20s of silence > 15s
        await a.TickAsync();
        await a.TickAsync();

        Assert.True(a.IsLost);
        Assert.Equal(1, lost);
    }

    [Fact]
    public async Task KeepAlive_Reset_ForgivesTheSilenceOfAnOutage_AndCanDetectLossAgain()
    {
        var clock = new ManualClock();
        var (chA, _) = ControlPair();
        var a = new KeepAliveService(chA, new KeepAliveOptions { Timeout = TimeSpan.FromSeconds(15) }, clock);
        var lost = 0;
        a.ConnectionLost += () => lost++;

        clock.Advance(TimeSpan.FromSeconds(20));
        await a.TickAsync();
        Assert.True(a.IsLost);

        // The connection under the channel was replaced: the 20 seconds of silence belong to the old one.
        a.Reset();
        Assert.False(a.IsLost);
        clock.Advance(TimeSpan.FromSeconds(5));
        await a.TickAsync();
        Assert.False(a.IsLost);
        Assert.Equal(1, lost);

        clock.Advance(TimeSpan.FromSeconds(20)); // and the new connection can die too
        await a.TickAsync();
        Assert.True(a.IsLost);
        Assert.Equal(2, lost);
    }

    [Fact]
    public async Task KeepAlive_InboundTraffic_ResetsTimeout()
    {
        var clock = new ManualClock();
        var (chA, _) = ControlPair();
        var a = new KeepAliveService(chA, new KeepAliveOptions { Timeout = TimeSpan.FromSeconds(15) }, clock);

        clock.Advance(TimeSpan.FromSeconds(12));
        await a.HandleAsync(new byte[] { 0x01, 0x02 }, default); // any control traffic is proof of life
        clock.Advance(TimeSpan.FromSeconds(12));
        await a.TickAsync();

        Assert.False(a.IsLost);
    }

    [Fact]
    public async Task KeepAlive_ForwardsNonKeepAliveMessages()
    {
        var (chA, _) = ControlPair();
        var a = new KeepAliveService(chA, clock: new ManualClock());
        byte[]? got = null;
        a.ControlMessage += m => got = m.ToArray();

        await a.HandleAsync(new byte[] { 9, 9, 9 }, default);

        Assert.Equal(new byte[] { 9, 9, 9 }, got);
    }

    [Fact]
    public async Task KeepAlive_UnknownOrReplayedPong_DoesNotProduceRtt()
    {
        var clock = new ManualClock();
        var (chA, chB) = ControlPair();
        var a = new KeepAliveService(chA, clock: clock);
        var b = new KeepAliveService(chB, clock: clock);

        await a.HandleAsync(new byte[] { 0xF1, 0, 0, 0, 77 }, default); // never sent
        Assert.Null(a.LastRtt);

        await a.TickAsync();
        await b.HandleAsync(await Next(chB), default);
        var pong = await Next(chA);
        await a.HandleAsync(pong, default);
        var first = a.LastRtt;
        clock.Advance(TimeSpan.FromSeconds(1));
        await a.HandleAsync(pong, default); // replay of the same pong
        Assert.Equal(first, a.LastRtt);
    }

    [Fact]
    public async Task KeepAlive_RunAsync_EndToEnd_MeasuresRtt_AndDetectsDeadPeer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var (chA, chB) = ControlPair();
        var opt = new KeepAliveOptions { Interval = TimeSpan.FromMilliseconds(20), Timeout = TimeSpan.FromMilliseconds(300) };
        var a = new KeepAliveService(chA, opt);
        var b = new KeepAliveService(chB, opt);
        var lostTcs = new TaskCompletionSource();
        a.ConnectionLost += () => lostTcs.TrySetResult();

        using var bCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var aTask = a.RunAsync(cts.Token);
        var bTask = b.RunAsync(bCts.Token);

        while (a.LastRtt is null && !cts.IsCancellationRequested) await Task.Delay(10);
        Assert.NotNull(a.LastRtt);
        Assert.False(a.IsLost);

        bCts.Cancel(); // B goes silent
        await bTask;
        await lostTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(a.IsLost);

        await aTask;
    }

    [Fact]
    public void KeepAlive_RejectsNonControlChannel() =>
        Assert.Throws<ArgumentException>(() => new KeepAliveService(new LoopbackChannel(ChannelType.Input)));

    // ── Backoff ──────────────────────────────────────────────────────────

    [Fact]
    public void Backoff_GrowsExponentially_AndCapsAtMax()
    {
        var p = new ReconnectPolicy();
        var seq = Enumerable.Range(1, 8).Select(i => p.DelayFor(i, () => 0.5).TotalSeconds).ToArray();
        Assert.Equal(new double[] { 1, 2, 4, 8, 16, 30, 30, 30 }, seq);
    }

    [Fact]
    public void Backoff_JitterStaysWithinBounds()
    {
        var p = new ReconnectPolicy { JitterRatio = 0.2 };
        Assert.Equal(0.8, p.DelayFor(1, () => 0).TotalSeconds, 3);
        Assert.Equal(1.2, p.DelayFor(1, () => 1).TotalSeconds, 3);
        for (var i = 0; i < 200; i++)
        {
            var d = p.DelayFor(3).TotalSeconds;
            Assert.InRange(d, 3.2 - 0.001, 4.8 + 0.001);
        }
    }

    [Fact]
    public void Backoff_RejectsAttemptBelowOne() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectPolicy().DelayFor(0));

    // ── Network change detection ─────────────────────────────────────────

    private static NetworkSnapshot Snap(params (string nic, string ip)[] e) =>
        new(e.Select(x => new NetworkEndpoint(x.nic, x.ip)));

    [Fact]
    public void NetworkCompare_ClassifiesChanges()
    {
        var wifi = Snap(("wifi", "192.168.1.5"));
        Assert.Equal(NetworkChangeKind.None, NetworkSnapshot.Compare(wifi, Snap(("wifi", "192.168.1.5"))));
        Assert.Equal(NetworkChangeKind.AddressChanged, NetworkSnapshot.Compare(wifi, Snap(("wifi", "192.168.1.9"))));
        Assert.Equal(NetworkChangeKind.InterfaceChanged, NetworkSnapshot.Compare(wifi, Snap(("eth", "10.0.0.2"))));
        Assert.Equal(NetworkChangeKind.InterfaceChanged,
            NetworkSnapshot.Compare(wifi, Snap(("wifi", "192.168.1.5"), ("vpn", "10.8.0.2"))));
        Assert.Equal(NetworkChangeKind.Lost, NetworkSnapshot.Compare(wifi, Snap()));
        Assert.Equal(NetworkChangeKind.Restored, NetworkSnapshot.Compare(Snap(), wifi));
        Assert.Equal(NetworkChangeKind.None, NetworkSnapshot.Compare(Snap(), Snap()));
    }

    [Fact]
    public void Detector_RaisesOnlyOnRealChanges()
    {
        var current = Snap(("wifi", "192.168.1.5"));
        var d = new NetworkChangeDetector(() => current);
        var events = new List<NetworkChangeKind>();
        d.NetworkChanged += events.Add;

        Assert.Equal(NetworkChangeKind.None, d.Poll());
        current = Snap(("eth", "10.0.0.2"));
        Assert.Equal(NetworkChangeKind.InterfaceChanged, d.Poll());
        Assert.Equal(NetworkChangeKind.None, d.Poll()); // baseline moved; no repeat
        current = Snap();
        d.Poll();
        current = Snap(("eth", "10.0.0.7"));
        d.Poll();

        Assert.Equal(new[] { NetworkChangeKind.InterfaceChanged, NetworkChangeKind.Lost, NetworkChangeKind.Restored }, events);
    }

    [Fact]
    public void SystemSnapshot_CaptureDoesNotThrow_AndSystemEventsDetach()
    {
        var snap = NetworkSnapshot.Capture();
        Assert.NotNull(snap.Endpoints);
        var d = new NetworkChangeDetector();
        d.AttachToSystemEvents().Dispose();
    }

    // ── Connection state machine ─────────────────────────────────────────

    [Fact]
    public void StateMachine_FullLifecycle()
    {
        var sm = new ConnectionStateMachine();
        var seen = new List<(ConnectionState, ConnectionState)>();
        sm.StateChanged += (f, t) => seen.Add((f, t));

        foreach (var s in new[]
                 {
                     ConnectionState.Connecting, ConnectionState.Negotiating, ConnectionState.Authenticating,
                     ConnectionState.Authorizing, ConnectionState.EstablishingTransport, ConnectionState.Connected,
                     ConnectionState.Disconnecting, ConnectionState.Disconnected
                 })
            sm.Transition(s);

        Assert.Equal(8, seen.Count);
        Assert.Equal(ConnectionState.Disconnected, sm.State);
        Assert.False(sm.IsConnected);
    }

    [Theory]
    [InlineData(ConnectionState.Disconnected, ConnectionState.Connected)]
    [InlineData(ConnectionState.Connecting, ConnectionState.Connected)]
    [InlineData(ConnectionState.Authenticating, ConnectionState.EstablishingTransport)]
    [InlineData(ConnectionState.Connected, ConnectionState.Authorizing)]
    [InlineData(ConnectionState.Connected, ConnectionState.Disconnected)]
    [InlineData(ConnectionState.Disconnecting, ConnectionState.Connected)]
    public void StateMachine_IllegalTransitions_Throw(ConnectionState from, ConnectionState to)
    {
        Assert.False(ConnectionStateMachine.CanTransition(from, to));
        var sm = new ConnectionStateMachine();
        Drive(sm, from);
        var ex = Assert.Throws<InvalidConnectionTransitionException>(() => sm.Transition(to));
        Assert.Equal(from, ex.From);
        Assert.Equal(from, sm.State);
    }

    [Fact]
    public void StateMachine_TransitionTable_IsExactlyTheDocumentedOne()
    {
        var legal = new HashSet<(ConnectionState, ConnectionState)>
        {
            (ConnectionState.Disconnected, ConnectionState.Connecting),
            (ConnectionState.Connecting, ConnectionState.Negotiating),
            (ConnectionState.Negotiating, ConnectionState.Authenticating),
            (ConnectionState.Authenticating, ConnectionState.Authorizing),
            (ConnectionState.Authorizing, ConnectionState.EstablishingTransport),
            (ConnectionState.EstablishingTransport, ConnectionState.Connected),
            (ConnectionState.EstablishingTransport, ConnectionState.Reconnecting),
            (ConnectionState.Connected, ConnectionState.Reconnecting),
            (ConnectionState.Reconnecting, ConnectionState.EstablishingTransport),
            (ConnectionState.Connecting, ConnectionState.Disconnecting),
            (ConnectionState.Negotiating, ConnectionState.Disconnecting),
            (ConnectionState.Authenticating, ConnectionState.Disconnecting),
            (ConnectionState.Authorizing, ConnectionState.Disconnecting),
            (ConnectionState.EstablishingTransport, ConnectionState.Disconnecting),
            (ConnectionState.Connected, ConnectionState.Disconnecting),
            (ConnectionState.Reconnecting, ConnectionState.Disconnecting),
            (ConnectionState.Disconnecting, ConnectionState.Disconnected)
        };
        var all = Enum.GetValues<ConnectionState>();
        foreach (var f in all)
            foreach (var t in all)
                Assert.Equal(legal.Contains((f, t)), ConnectionStateMachine.CanTransition(f, t));
    }

    [Fact]
    public void StateMachine_ConcurrentTransitions_OnlyOneWins()
    {
        for (var round = 0; round < 50; round++)
        {
            var sm = new ConnectionStateMachine();
            Drive(sm, ConnectionState.Connected);
            var wins = 0;
            Parallel.For(0, 8, _ =>
            {
                if (sm.TryTransition(ConnectionState.Connected, ConnectionState.Reconnecting))
                    Interlocked.Increment(ref wins);
            });
            Assert.Equal(1, wins);
        }
    }

    private static void Drive(ConnectionStateMachine sm, ConnectionState target)
    {
        ConnectionState[] path = target switch
        {
            ConnectionState.Disconnected => [],
            ConnectionState.Connecting => [ConnectionState.Connecting],
            ConnectionState.Authenticating => [ConnectionState.Connecting, ConnectionState.Negotiating, ConnectionState.Authenticating],
            ConnectionState.Connected => [ConnectionState.Connecting, ConnectionState.Negotiating, ConnectionState.Authenticating,
                ConnectionState.Authorizing, ConnectionState.EstablishingTransport, ConnectionState.Connected],
            ConnectionState.Disconnecting => [ConnectionState.Connecting, ConnectionState.Disconnecting],
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        foreach (var s in path) sm.Transition(s);
    }

    // ── Supervisor / recovery ────────────────────────────────────────────

    private static ConnectionStateMachine ConnectedMachine()
    {
        var sm = new ConnectionStateMachine();
        Drive(sm, ConnectionState.Connected);
        return sm;
    }

    private static readonly Func<TimeSpan, CancellationToken, Task> InstantDelay = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task Supervisor_RecoversAfterTransientFailures_WithBackoff()
    {
        var sm = ConnectedMachine();
        var visited = new List<ConnectionState>();
        sm.StateChanged += (_, t) => visited.Add(t);
        var waits = new List<TimeSpan>();
        var attempts = 0;
        var sup = new ConnectionSupervisor(sm, new ReconnectPolicy(),
            (_, _) => Task.FromResult(++attempts == 3), delay: InstantDelay, random: () => 0.5);
        sup.AttemptScheduled += (_, w) => waits.Add(w);

        var outcome = await sup.OnConnectionLostAsync();

        Assert.Equal(RecoveryOutcome.Recovered, outcome);
        Assert.Equal(ConnectionState.Connected, sm.State);
        Assert.Equal(3, attempts);
        Assert.Equal(new[] { 1.0, 2.0, 4.0 }, waits.Select(w => w.TotalSeconds));
        Assert.Equal(new[]
        {
            ConnectionState.Reconnecting, ConnectionState.EstablishingTransport,
            ConnectionState.Reconnecting, ConnectionState.EstablishingTransport,
            ConnectionState.Reconnecting, ConnectionState.EstablishingTransport, ConnectionState.Connected
        }, visited);
    }

    [Fact]
    public async Task Supervisor_ImmediateFirstAttempt_SkipsBackoff()
    {
        var sm = ConnectedMachine();
        var waits = new List<TimeSpan>();
        var sup = new ConnectionSupervisor(sm, new ReconnectPolicy(), (_, _) => Task.FromResult(true),
            delay: InstantDelay, random: () => 0.5);
        sup.AttemptScheduled += (_, w) => waits.Add(w);

        Assert.Equal(RecoveryOutcome.Recovered, await sup.OnConnectionLostAsync(immediateFirstAttempt: true));
        Assert.Equal(TimeSpan.Zero, waits.Single());
    }

    [Fact]
    public async Task Supervisor_GivesUpAfterMaxAttempts_AndTearsDown()
    {
        var sm = ConnectedMachine();
        var teardown = new GracefulDisconnect();
        var tornDown = false;
        teardown.Add("close", _ => { tornDown = true; return Task.CompletedTask; });
        var attempts = 0;
        var sup = new ConnectionSupervisor(sm, new ReconnectPolicy { MaxAttempts = 4 },
            (_, _) => Task.FromResult(attempts++ < 0), teardown, delay: InstantDelay);
        RecoveryOutcome? finished = null;
        sup.RecoveryFinished += o => finished = o;

        var outcome = await sup.OnConnectionLostAsync();

        Assert.Equal(RecoveryOutcome.GaveUp, outcome);
        Assert.Equal(RecoveryOutcome.GaveUp, finished);
        Assert.Equal(4, attempts);
        Assert.Equal(ConnectionState.Disconnected, sm.State);
        Assert.True(tornDown);
    }

    [Fact]
    public async Task Supervisor_RecoveryTimeout_StopsBeforeMaxAttempts()
    {
        var clock = new ManualClock();
        var sm = ConnectedMachine();
        var attempts = 0;
        var sup = new ConnectionSupervisor(sm,
            new ReconnectPolicy { MaxAttempts = 100, InitialDelay = TimeSpan.FromSeconds(10), MaxDelay = TimeSpan.FromSeconds(10),
                RecoveryTimeout = TimeSpan.FromSeconds(35), JitterRatio = 0 },
            (_, _) => { attempts++; return Task.FromResult(false); },
            clock: clock, delay: (d, _) => { clock.Advance(d); return Task.CompletedTask; });

        Assert.Equal(RecoveryOutcome.GaveUp, await sup.OnConnectionLostAsync());
        Assert.InRange(attempts, 3, 4);
        Assert.Equal(ConnectionState.Disconnected, sm.State);
    }

    [Fact]
    public async Task Supervisor_ReestablishThrowing_IsTreatedAsFailure()
    {
        var sm = ConnectedMachine();
        var attempts = 0;
        var sup = new ConnectionSupervisor(sm, new ReconnectPolicy(), (_, _) =>
        {
            if (++attempts == 1) throw new InvalidOperationException("boom");
            return Task.FromResult(true);
        }, delay: InstantDelay);

        Assert.Equal(RecoveryOutcome.Recovered, await sup.OnConnectionLostAsync());
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Supervisor_NotConnected_IsNotApplicable()
    {
        var sm = new ConnectionStateMachine();
        var sup = new ConnectionSupervisor(sm, new ReconnectPolicy(), (_, _) => Task.FromResult(true), delay: InstantDelay);
        Assert.Equal(RecoveryOutcome.NotApplicable, await sup.OnConnectionLostAsync());
        Assert.Equal(ConnectionState.Disconnected, sm.State);
    }

    [Fact]
    public async Task Supervisor_DuplicateLossReports_ShareOneRecovery()
    {
        var sm = ConnectedMachine();
        var gate = new TaskCompletionSource<bool>();
        var calls = 0;
        var sup = new ConnectionSupervisor(sm, new ReconnectPolicy(), (_, _) => { calls++; return gate.Task; },
            delay: InstantDelay);

        var first = sup.OnConnectionLostAsync();
        var second = sup.OnConnectionLostAsync();
        Assert.Same(first, second);
        Assert.True(sup.IsRecovering);

        gate.SetResult(true);
        Assert.Equal(RecoveryOutcome.Recovered, await first);
        Assert.Equal(1, calls);
        Assert.False(sup.IsRecovering);
    }

    [Fact]
    public async Task Supervisor_AddressChange_MigratesImmediately_WhileConnected()
    {
        var sm = ConnectedMachine();
        var recovered = new TaskCompletionSource<RecoveryOutcome>();
        var waits = new List<TimeSpan>();
        var sup = new ConnectionSupervisor(sm, new ReconnectPolicy(), (_, _) => Task.FromResult(true), delay: InstantDelay);
        sup.AttemptScheduled += (_, w) => waits.Add(w);
        sup.RecoveryFinished += o => recovered.TrySetResult(o);

        sup.OnNetworkChanged(NetworkChangeKind.InterfaceChanged); // Wi-Fi → Ethernet

        Assert.Equal(RecoveryOutcome.Recovered, await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.Zero, waits.Single());
        Assert.Equal(ConnectionState.Connected, sm.State);
    }

    [Fact]
    public async Task Supervisor_NetworkLost_WhileConnected_DoesNotStartRecovery()
    {
        var sm = ConnectedMachine();
        var sup = new ConnectionSupervisor(sm, new ReconnectPolicy(), (_, _) => Task.FromResult(true), delay: InstantDelay);
        sup.OnNetworkChanged(NetworkChangeKind.Lost);
        await Task.Delay(50);
        Assert.Equal(ConnectionState.Connected, sm.State);
        Assert.False(sup.IsRecovering);
    }

    [Fact]
    public async Task Supervisor_NetworkRestored_WakesBackoffWait()
    {
        var sm = ConnectedMachine();
        var waiting = new TaskCompletionSource();
        var sup = new ConnectionSupervisor(sm, new ReconnectPolicy(), (_, _) => Task.FromResult(true),
            delay: (_, ct) => { waiting.TrySetResult(); return Task.Delay(Timeout.Infinite, ct); });

        var recovery = sup.OnConnectionLostAsync(); // would wait ~1s... or forever with this delay
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(recovery.IsCompleted);

        sup.OnNetworkChanged(NetworkChangeKind.Restored);

        Assert.Equal(RecoveryOutcome.Recovered, await recovery.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Supervisor_Cancellation_StopsRecovery_AndDisconnects()
    {
        var sm = ConnectedMachine();
        using var cts = new CancellationTokenSource();
        var waiting = new TaskCompletionSource();
        var sup = new ConnectionSupervisor(sm, new ReconnectPolicy(), (_, _) => Task.FromResult(true),
            delay: (_, ct) => { waiting.TrySetResult(); return Task.Delay(Timeout.Infinite, ct); });

        var recovery = sup.OnConnectionLostAsync(ct: cts.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        Assert.Equal(RecoveryOutcome.Cancelled, await recovery.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ConnectionState.Disconnected, sm.State);
    }

    [Fact]
    public async Task Supervisor_UserDisconnect_RunsTeardownInOrder()
    {
        var sm = ConnectedMachine();
        var order = new List<string>();
        var td = new GracefulDisconnect();
        foreach (var n in new[] { "stop-input", "stop-capture", "close-channels", "notify-server" })
            td.Add(n, _ => { order.Add(n); return Task.CompletedTask; });
        var sup = new ConnectionSupervisor(sm, new ReconnectPolicy(), (_, _) => Task.FromResult(true), td);

        var results = await sup.DisconnectAsync();

        Assert.Equal(new[] { "stop-input", "stop-capture", "close-channels", "notify-server" }, order);
        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.Equal(ConnectionState.Disconnected, sm.State);
        Assert.Empty(await sup.DisconnectAsync()); // already disconnected: nothing to do
    }

    // ── Graceful disconnect ──────────────────────────────────────────────

    [Fact]
    public async Task Graceful_FailingStep_DoesNotSkipLaterSteps()
    {
        var ran = new List<string>();
        var g = new GracefulDisconnect()
            .Add("a", _ => { ran.Add("a"); return Task.CompletedTask; })
            .Add("b", _ => throw new IOException("secret path C:\\x"))
            .Add("c", _ => { ran.Add("c"); return Task.CompletedTask; });

        var results = await g.RunAsync();

        Assert.Equal(new[] { "a", "c" }, ran);
        Assert.False(results[1].Succeeded);
        Assert.Equal("IOException", results[1].Error); // type only, no message
        Assert.True(results[2].Succeeded);
    }

    [Fact]
    public async Task Graceful_HangingStep_TimesOut_AndContinues()
    {
        var ranAfter = false;
        var g = new GracefulDisconnect(TimeSpan.FromMilliseconds(100))
            .Add("hang", _ => new TaskCompletionSource().Task)
            .Add("after", _ => { ranAfter = true; return Task.CompletedTask; });

        var results = await g.RunAsync();

        Assert.True(results[0].TimedOut);
        Assert.True(ranAfter);
    }

    [Fact]
    public async Task Graceful_RunsOnlyOnce_AndRejectsLateSteps()
    {
        var count = 0;
        var g = new GracefulDisconnect().Add("a", _ => { count++; return Task.CompletedTask; });

        var first = await g.RunAsync();
        var second = await g.RunAsync();

        Assert.Equal(1, count);
        Assert.Same(first, second);
        Assert.Throws<InvalidOperationException>(() => g.Add("late", _ => Task.CompletedTask));
    }
}
