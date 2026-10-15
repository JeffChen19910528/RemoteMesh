using RemoteDesktop.Core;

namespace RemoteDesktop.Input;

public enum InputDecision
{
    Injected,
    Malformed,
    SessionNotActive,
    PermissionDenied,
    InvalidEvent,
    Replay,
    Stale,
    RateLimited
}

public sealed class InputGateOptions
{
    public int MaxCoordinate { get; init; } = 32767;
    public int MaxWheelDelta { get; init; } = 1200;
    public int MaxButton { get; init; } = 4;
    public int MaxKeyCode { get; init; } = 0xFFFF;
    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromSeconds(30);
    public int ReplayWindow { get; init; } = 4096;
    public int MaxEventsPerSecond { get; init; } = 1000;
}

/// <summary>
/// The only path to <see cref="IInputController"/> for remote input: session check → permission check →
/// validation → replay/staleness → rate limit → inject. Permissions are read live from the session, so a
/// revoke applies to the very next event. Event contents (key codes, coordinates) are never logged.
/// </summary>
public sealed class InputGate
{
    private readonly SessionInfo _session;
    private readonly IInputController _controller;
    private readonly InputGateOptions _opt;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<InputDecision>? _onDecision;

    private readonly HashSet<Guid> _seen = new();
    private readonly Queue<Guid> _seenOrder = new();
    private DateTimeOffset _windowStart;
    private int _windowCount;

    public InputGate(SessionInfo session, IInputController controller, InputGateOptions? options = null,
        Func<DateTimeOffset>? clock = null, Action<InputDecision>? onDecision = null)
    {
        _session = session;
        _controller = controller;
        _opt = options ?? new InputGateOptions();
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _onDecision = onDecision;
        _windowStart = _now();
    }

    public async Task<InputDecision> ProcessAsync(InputEvent e, CancellationToken ct = default)
    {
        var decision = Evaluate(e);
        if (decision == InputDecision.Injected)
            await _controller.InjectAsync(e, ct);
        _onDecision?.Invoke(decision);
        return decision;
    }

    public async Task<InputDecision> ProcessRawAsync(ReadOnlyMemory<byte> raw, CancellationToken ct = default)
    {
        if (!InputEventSerializer.TryDeserialize(raw.Span, out var e) || e is null)
        {
            _onDecision?.Invoke(InputDecision.Malformed);
            return InputDecision.Malformed;
        }
        return await ProcessAsync(e, ct);
    }

    private InputDecision Evaluate(InputEvent e)
    {
        if (_session.State != SessionState.Connected || _session.IsExpired)
            return InputDecision.SessionNotActive;

        var required = RequiredPermission(e.EventType);
        if (!_session.Permissions.Has(required))
            return InputDecision.PermissionDenied;

        if (!IsValid(e)) return InputDecision.InvalidEvent;

        var now = _now();
        if ((now - e.Timestamp).Duration() > _opt.MaxClockSkew) return InputDecision.Stale;
        if (_seen.Contains(e.EventId)) return InputDecision.Replay;

        if (now - _windowStart >= TimeSpan.FromSeconds(1))
        {
            _windowStart = now;
            _windowCount = 0;
        }
        if (++_windowCount > _opt.MaxEventsPerSecond) return InputDecision.RateLimited;

        _seen.Add(e.EventId);
        _seenOrder.Enqueue(e.EventId);
        while (_seenOrder.Count > _opt.ReplayWindow)
            _seen.Remove(_seenOrder.Dequeue());

        return InputDecision.Injected;
    }

    public static Permission RequiredPermission(InputEventType t) => t switch
    {
        InputEventType.KeyDown or InputEventType.KeyUp => Permission.ControlKeyboard,
        _ => Permission.ControlMouse
    };

    private bool IsValid(InputEvent e) => e.EventType switch
    {
        InputEventType.MouseMove => InRange(e.X, e.Y),
        InputEventType.MouseButtonDown or InputEventType.MouseButtonUp =>
            InRange(e.X, e.Y) && e.Button >= 0 && e.Button <= _opt.MaxButton,
        InputEventType.MouseWheel => Math.Abs((long)e.Delta) <= _opt.MaxWheelDelta,
        InputEventType.KeyDown or InputEventType.KeyUp => e.KeyCode > 0 && e.KeyCode <= _opt.MaxKeyCode,
        _ => false
    };

    private bool InRange(int x, int y) =>
        x >= 0 && y >= 0 && x <= _opt.MaxCoordinate && y <= _opt.MaxCoordinate;
}
