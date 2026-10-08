using RemoteDesktop.Core;

namespace RemoteDesktop.Transport.Reliability;

public enum RecoveryOutcome { Recovered, GaveUp, Cancelled, NotApplicable }

/// <summary>
/// Keeps an established session alive: on peer loss (keep-alive) or local network change it moves the state
/// machine to Reconnecting and re-establishes the transport with backoff until it succeeds, runs out of attempts,
/// or exceeds the recovery timeout. A local network change (Wi-Fi ↔ Ethernet, new IP) retries immediately
/// (connection migration) instead of waiting out the backoff.
/// </summary>
public sealed class ConnectionSupervisor
{
    private readonly ConnectionStateMachine _state;
    private readonly ReconnectPolicy _policy;
    private readonly Func<int, CancellationToken, Task<bool>> _reestablish;
    private readonly GracefulDisconnect? _teardown;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double>? _random;
    private readonly object _lock = new();
    private Task<RecoveryOutcome>? _recovery;
    private TaskCompletionSource _wake = NewWake();

    public ConnectionSupervisor(ConnectionStateMachine state, ReconnectPolicy policy,
        Func<int, CancellationToken, Task<bool>> reestablish, GracefulDisconnect? teardown = null,
        TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null, Func<double>? random = null)
    {
        _state = state;
        _policy = policy;
        _reestablish = reestablish;
        _teardown = teardown;
        _clock = clock ?? TimeProvider.System;
        _delay = delay ?? Task.Delay;
        _random = random;
    }

    public event Action<int, TimeSpan>? AttemptScheduled;
    public event Action<RecoveryOutcome>? RecoveryFinished;

    public bool IsRecovering
    {
        get { lock (_lock) return _recovery is { IsCompleted: false }; }
    }

    /// <summary>Peer stopped responding or the transport failed. Idempotent while a recovery is running.</summary>
    public Task<RecoveryOutcome> OnConnectionLostAsync(bool immediateFirstAttempt = false, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_recovery is { IsCompleted: false }) return _recovery;
            if (!_state.TryTransition(ConnectionState.Connected, ConnectionState.Reconnecting))
                return Task.FromResult(RecoveryOutcome.NotApplicable);
            _wake = NewWake();
            return _recovery = RecoverAsync(immediateFirstAttempt, ct);
        }
    }

    public void OnNetworkChanged(NetworkChangeKind kind)
    {
        switch (kind)
        {
            case NetworkChangeKind.AddressChanged or NetworkChangeKind.InterfaceChanged:
                if (_state.State == ConnectionState.Connected)
                    _ = OnConnectionLostAsync(immediateFirstAttempt: true);
                else
                    Wake();
                break;
            case NetworkChangeKind.Restored:
                Wake(); // network is back: stop waiting out the backoff
                break;
            // Lost: nothing to do; keep-alive reports the dead peer and the backoff covers the outage.
        }
    }

    /// <summary>User-initiated disconnect: ordered teardown, then Disconnected.</summary>
    public async Task<IReadOnlyList<DisconnectStepResult>> DisconnectAsync()
    {
        var results = new List<DisconnectStepResult>();
        if (_state.State != ConnectionState.Disconnected)
        {
            // From any state a teardown is legal except already-Disconnecting.
            _state.TryTransition(ConnectionState.Disconnecting);
            if (_teardown is not null) results.AddRange(await _teardown.RunAsync());
            _state.TryTransition(ConnectionState.Disconnecting, ConnectionState.Disconnected);
        }
        return results;
    }

    private async Task<RecoveryOutcome> RecoverAsync(bool immediateFirst, CancellationToken ct)
    {
        var started = _clock.GetUtcNow();
        RecoveryOutcome outcome = RecoveryOutcome.GaveUp;
        try
        {
            for (var attempt = 1; attempt <= _policy.MaxAttempts; attempt++)
            {
                if (_clock.GetUtcNow() - started > _policy.RecoveryTimeout) break;
                ct.ThrowIfCancellationRequested();

                var wait = immediateFirst && attempt == 1 ? TimeSpan.Zero : _policy.DelayFor(attempt, _random);
                AttemptScheduled?.Invoke(attempt, wait);
                if (wait > TimeSpan.Zero) await WaitOrWakeAsync(wait, ct);

                if (_state.State != ConnectionState.Reconnecting) { outcome = RecoveryOutcome.Cancelled; return outcome; }
                if (!_state.TryTransition(ConnectionState.Reconnecting, ConnectionState.EstablishingTransport)) break;

                bool ok;
                try { ok = await _reestablish(attempt, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { ok = false; }

                if (ok && _state.TryTransition(ConnectionState.EstablishingTransport, ConnectionState.Connected))
                {
                    outcome = RecoveryOutcome.Recovered;
                    return outcome;
                }
                _state.TryTransition(ConnectionState.EstablishingTransport, ConnectionState.Reconnecting);
            }
        }
        catch (OperationCanceledException) { outcome = RecoveryOutcome.Cancelled; }
        finally
        {
            if (outcome != RecoveryOutcome.Recovered)
            {
                await DisconnectAsync();
            }
            RecoveryFinished?.Invoke(outcome);
        }
        return outcome;
    }

    private async Task WaitOrWakeAsync(TimeSpan wait, CancellationToken ct)
    {
        Task wake;
        lock (_lock) wake = _wake.Task;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = _delay(wait, cts.Token);
        await Task.WhenAny(delay, wake);
        cts.Cancel();
        try { await delay; } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    private void Wake()
    {
        lock (_lock) _wake.TrySetResult();
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
