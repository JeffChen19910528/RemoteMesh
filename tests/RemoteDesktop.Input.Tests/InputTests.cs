using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Input.Tests;

public sealed class InputTests
{
    private sealed class Recorder : IInputController
    {
        public List<InputEvent> Injected { get; } = new();
        public Task InjectAsync(InputEvent e, CancellationToken ct = default)
        {
            Injected.Add(e);
            return Task.CompletedTask;
        }
    }

    private static SessionInfo Session(Permission p, SessionState state = SessionState.Connected) => new()
    {
        SessionId = SessionId.New(),
        CreatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        State = state,
        Permissions = new PermissionSet(p)
    };

    private static InputEvent Move(int x = 10, int y = 10) =>
        new() { EventType = InputEventType.MouseMove, X = x, Y = y };

    private static InputEvent Key(int code = 65, InputEventType t = InputEventType.KeyDown) =>
        new() { EventType = t, KeyCode = code };

    private static (InputGate gate, Recorder rec) Gate(SessionInfo s, InputGateOptions? o = null,
        Func<DateTimeOffset>? clock = null)
    {
        var rec = new Recorder();
        return (new InputGate(s, rec, o, clock), rec);
    }

    // ── Serialization ────────────────────────────────────────────────────

    [Fact]
    public void Serializer_RoundTrips()
    {
        var e = new InputEvent
        {
            EventType = InputEventType.MouseButtonDown, X = 5, Y = 6, Button = 2,
            Delta = -3, KeyCode = 17, Modifiers = 4
        };
        Assert.True(InputEventSerializer.TryDeserialize(InputEventSerializer.Serialize(e), out var r));
        Assert.Equal(e.EventId, r!.EventId);
        Assert.Equal(e.Timestamp.UtcTicks, r.Timestamp.UtcTicks);
        Assert.Equal((e.EventType, e.X, e.Y, e.Button, e.Delta, e.KeyCode, e.Modifiers),
                     (r.EventType, r.X, r.Y, r.Button, r.Delta, r.KeyCode, r.Modifiers));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(48)]
    [InlineData(50)]
    public void Serializer_RejectsWrongLength(int len) =>
        Assert.False(InputEventSerializer.TryDeserialize(new byte[len], out _));

    [Fact]
    public void Serializer_RejectsUnknownEventType()
    {
        var b = InputEventSerializer.Serialize(Move());
        b[24] = 200;
        Assert.False(InputEventSerializer.TryDeserialize(b, out _));
    }

    [Fact]
    public void Serializer_RejectsOutOfRangeTimestamp()
    {
        var b = InputEventSerializer.Serialize(Move());
        for (var i = 16; i < 24; i++) b[i] = 0x7F;
        Assert.False(InputEventSerializer.TryDeserialize(b, out _));
    }

    // ── Permission enforcement ───────────────────────────────────────────

    [Fact]
    public async Task ViewOnly_BlocksMouseAndKeyboard()
    {
        var (gate, rec) = Gate(Session(Permission.ViewOnly));
        Assert.Equal(InputDecision.PermissionDenied, await gate.ProcessAsync(Move()));
        Assert.Equal(InputDecision.PermissionDenied, await gate.ProcessAsync(Key()));
        Assert.Empty(rec.Injected);
    }

    [Fact]
    public async Task MouseOnly_AllowsMouse_BlocksKeyboard()
    {
        var (gate, rec) = Gate(Session(Permission.ViewScreen | Permission.ControlMouse));
        Assert.Equal(InputDecision.Injected, await gate.ProcessAsync(Move()));
        Assert.Equal(InputDecision.Injected,
            await gate.ProcessAsync(new InputEvent { EventType = InputEventType.MouseWheel, Delta = 120 }));
        Assert.Equal(InputDecision.PermissionDenied, await gate.ProcessAsync(Key()));
        Assert.Equal(2, rec.Injected.Count);
    }

    [Fact]
    public async Task KeyboardOnly_AllowsKeys_BlocksMouse()
    {
        var (gate, _) = Gate(Session(Permission.ControlKeyboard));
        Assert.Equal(InputDecision.Injected, await gate.ProcessAsync(Key()));
        Assert.Equal(InputDecision.Injected, await gate.ProcessAsync(Key(65, InputEventType.KeyUp)));
        Assert.Equal(InputDecision.PermissionDenied, await gate.ProcessAsync(Move()));
    }

    [Fact]
    public async Task RevokedPermission_AppliesToNextEvent()
    {
        var s = Session(Permission.FullControl);
        var (gate, rec) = Gate(s);
        Assert.Equal(InputDecision.Injected, await gate.ProcessAsync(Move()));
        s.Permissions.Revoke(Permission.ControlMouse);
        Assert.Equal(InputDecision.PermissionDenied, await gate.ProcessAsync(Move()));
        Assert.Single(rec.Injected);
    }

    [Theory]
    [InlineData(SessionState.Created)]
    [InlineData(SessionState.Authorizing)]
    [InlineData(SessionState.EstablishingTransport)]
    [InlineData(SessionState.Disconnecting)]
    [InlineData(SessionState.Failed)]
    public async Task NonConnectedSession_BlocksInput(SessionState state)
    {
        var (gate, rec) = Gate(Session(Permission.FullControl, state));
        Assert.Equal(InputDecision.SessionNotActive, await gate.ProcessAsync(Move()));
        Assert.Empty(rec.Injected);
    }

    [Fact]
    public async Task ExpiredSession_BlocksInput()
    {
        var s = new SessionInfo
        {
            SessionId = SessionId.New(),
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            State = SessionState.Connected,
            Permissions = PermissionSet.FullControl()
        };
        var (gate, _) = Gate(s);
        Assert.Equal(InputDecision.SessionNotActive, await gate.ProcessAsync(Move()));
    }

    // ── Validation ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -5)]
    [InlineData(40000, 0)]
    public async Task OutOfRangeCoordinates_Rejected(int x, int y)
    {
        var (gate, rec) = Gate(Session(Permission.FullControl));
        Assert.Equal(InputDecision.InvalidEvent, await gate.ProcessAsync(Move(x, y)));
        Assert.Empty(rec.Injected);
    }

    [Fact]
    public async Task InvalidButtonWheelKey_Rejected()
    {
        var (gate, _) = Gate(Session(Permission.FullControl));
        Assert.Equal(InputDecision.InvalidEvent, await gate.ProcessAsync(
            new InputEvent { EventType = InputEventType.MouseButtonDown, Button = 99 }));
        Assert.Equal(InputDecision.InvalidEvent, await gate.ProcessAsync(
            new InputEvent { EventType = InputEventType.MouseWheel, Delta = int.MinValue }));
        Assert.Equal(InputDecision.InvalidEvent, await gate.ProcessAsync(Key(0)));
        Assert.Equal(InputDecision.InvalidEvent, await gate.ProcessAsync(Key(0x10000)));
    }

    // ── Replay / staleness / rate limit ──────────────────────────────────

    [Fact]
    public async Task DuplicateEventId_IsReplay()
    {
        var (gate, rec) = Gate(Session(Permission.FullControl));
        var e = Move();
        Assert.Equal(InputDecision.Injected, await gate.ProcessAsync(e));
        Assert.Equal(InputDecision.Replay, await gate.ProcessAsync(e));
        Assert.Single(rec.Injected);
    }

    [Fact]
    public async Task OldAndFutureTimestamps_AreStale()
    {
        var (gate, _) = Gate(Session(Permission.FullControl));
        Assert.Equal(InputDecision.Stale, await gate.ProcessAsync(new InputEvent
            { EventType = InputEventType.MouseMove, Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5) }));
        Assert.Equal(InputDecision.Stale, await gate.ProcessAsync(new InputEvent
            { EventType = InputEventType.MouseMove, Timestamp = DateTimeOffset.UtcNow.AddMinutes(5) }));
    }

    [Fact]
    public async Task RateLimit_BlocksFloodThenRecovers()
    {
        var now = DateTimeOffset.UtcNow;
        var (gate, rec) = Gate(Session(Permission.FullControl),
            new InputGateOptions { MaxEventsPerSecond = 3 }, () => now);

        for (var i = 0; i < 3; i++)
            Assert.Equal(InputDecision.Injected, await gate.ProcessAsync(Move()));
        Assert.Equal(InputDecision.RateLimited, await gate.ProcessAsync(Move()));

        now = now.AddSeconds(1.1);
        Assert.Equal(InputDecision.Injected, await gate.ProcessAsync(
            new InputEvent { EventType = InputEventType.MouseMove, Timestamp = now }));
        Assert.Equal(4, rec.Injected.Count);
    }

    [Fact]
    public async Task ReplayWindow_IsBounded()
    {
        var (gate, _) = Gate(Session(Permission.FullControl), new InputGateOptions
            { ReplayWindow = 2, MaxEventsPerSecond = 100 });
        var first = Move();
        await gate.ProcessAsync(first);
        await gate.ProcessAsync(Move());
        await gate.ProcessAsync(Move());
        // first has been evicted from the window; memory stays bounded.
        Assert.Equal(InputDecision.Injected, await gate.ProcessAsync(first));
    }

    // ── Channel end to end ───────────────────────────────────────────────

    private static (LoopbackChannel viewer, LoopbackChannel host) Pair(ChannelType t = ChannelType.Input)
    {
        var viewer = new LoopbackChannel(t);
        var host = new LoopbackChannel(t);
        viewer.PeerWriter = host.Writer;
        return (viewer, host);
    }

    [Fact]
    public async Task Channel_EndToEnd_InjectsAllowedAndDropsDenied()
    {
        var (viewerCh, hostCh) = Pair();
        var decisions = new List<InputDecision>();
        var rec = new Recorder();
        var gate = new InputGate(Session(Permission.ViewScreen | Permission.ControlMouse), rec,
            onDecision: decisions.Add);
        var rx = new InputReceiver(hostCh, gate).RunAsync();
        var tx = new InputSender(viewerCh);

        await tx.SendAsync(Move(1, 2));
        await tx.SendAsync(Key());                       // no keyboard permission
        await viewerCh.SendAsync(new byte[] { 9, 9 });   // garbage
        await tx.SendAsync(Move(3, 4));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (decisions.Count < 4 && !cts.IsCancellationRequested) await Task.Delay(10);
        await hostCh.DisposeAsync();
        await rx;

        Assert.Equal(new[] { InputDecision.Injected, InputDecision.PermissionDenied,
            InputDecision.Malformed, InputDecision.Injected }, decisions);
        Assert.Equal(new[] { 1, 3 }, rec.Injected.Select(e => e.X));
    }

    [Fact]
    public void SenderAndReceiver_RejectNonInputChannel()
    {
        var ch = new LoopbackChannel(ChannelType.Desktop);
        Assert.Throws<ArgumentException>(() => new InputSender(ch));
        Assert.Throws<ArgumentException>(() =>
            new InputReceiver(ch, new InputGate(Session(Permission.FullControl), new Recorder())));
    }
}
