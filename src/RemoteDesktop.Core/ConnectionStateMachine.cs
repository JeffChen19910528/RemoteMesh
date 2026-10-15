namespace RemoteDesktop.Core;

/// <summary>Client-side connection lifecycle. Reconnecting is entered from Connected on loss.</summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Negotiating,
    Authenticating,
    Authorizing,
    EstablishingTransport,
    Connected,
    Reconnecting,
    Disconnecting
}

public sealed class InvalidConnectionTransitionException(ConnectionState from, ConnectionState to)
    : InvalidOperationException($"Illegal connection state transition: {from} -> {to}")
{
    public ConnectionState From { get; } = from;
    public ConnectionState To { get; } = to;
}

/// <summary>
/// Single source of truth for connection state — no scattered booleans. Only the transitions in
/// <see cref="Allowed"/> are legal, so impossible combinations (e.g. "connected but not authorized") cannot exist.
/// </summary>
public sealed class ConnectionStateMachine
{
    private static readonly Dictionary<ConnectionState, ConnectionState[]> Allowed = new()
    {
        [ConnectionState.Disconnected] = [ConnectionState.Connecting],
        [ConnectionState.Connecting] = [ConnectionState.Negotiating, ConnectionState.Disconnecting],
        [ConnectionState.Negotiating] = [ConnectionState.Authenticating, ConnectionState.Disconnecting],
        [ConnectionState.Authenticating] = [ConnectionState.Authorizing, ConnectionState.Disconnecting],
        [ConnectionState.Authorizing] = [ConnectionState.EstablishingTransport, ConnectionState.Disconnecting],
        [ConnectionState.EstablishingTransport] =
            [ConnectionState.Connected, ConnectionState.Reconnecting, ConnectionState.Disconnecting],
        [ConnectionState.Connected] = [ConnectionState.Reconnecting, ConnectionState.Disconnecting],
        [ConnectionState.Reconnecting] = [ConnectionState.EstablishingTransport, ConnectionState.Disconnecting],
        [ConnectionState.Disconnecting] = [ConnectionState.Disconnected]
    };

    private readonly object _lock = new();
    private ConnectionState _state = ConnectionState.Disconnected;

    public ConnectionState State
    {
        get { lock (_lock) return _state; }
    }

    public bool IsConnected => State == ConnectionState.Connected;

    public event Action<ConnectionState, ConnectionState>? StateChanged;

    public static bool CanTransition(ConnectionState from, ConnectionState to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    public bool TryTransition(ConnectionState to)
    {
        ConnectionState from;
        lock (_lock)
        {
            from = _state;
            if (!CanTransition(from, to)) return false;
            _state = to;
        }
        StateChanged?.Invoke(from, to);
        return true;
    }

    public void Transition(ConnectionState to)
    {
        if (!TryTransition(to)) throw new InvalidConnectionTransitionException(State, to);
    }

    /// <summary>Atomically moves from an expected state; false if the state moved on concurrently.</summary>
    public bool TryTransition(ConnectionState expectedFrom, ConnectionState to)
    {
        lock (_lock)
        {
            if (_state != expectedFrom || !CanTransition(expectedFrom, to)) return false;
            _state = to;
        }
        StateChanged?.Invoke(expectedFrom, to);
        return true;
    }
}
