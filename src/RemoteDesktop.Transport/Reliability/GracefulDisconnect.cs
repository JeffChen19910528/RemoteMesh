namespace RemoteDesktop.Transport.Reliability;

public sealed record DisconnectStepResult(string Name, bool Succeeded, bool TimedOut, string? Error);

/// <summary>
/// Ordered teardown: stop input, capture, encoder, file transfers, close channels, release
/// resources, notify server, write audit log. A failing or hanging step never prevents the later steps,
/// so no ghost session is left behind. Runs once; later calls return the first result.
/// </summary>
public sealed class GracefulDisconnect
{
    private readonly List<(string Name, Func<CancellationToken, Task> Step)> _steps = new();
    private readonly TimeSpan _stepTimeout;
    private readonly object _lock = new();
    private Task<IReadOnlyList<DisconnectStepResult>>? _run;

    public GracefulDisconnect(TimeSpan? perStepTimeout = null) =>
        _stepTimeout = perStepTimeout ?? TimeSpan.FromSeconds(5);

    public GracefulDisconnect Add(string name, Func<CancellationToken, Task> step)
    {
        lock (_lock)
        {
            if (_run is not null) throw new InvalidOperationException("Disconnect already started.");
            _steps.Add((name, step));
        }
        return this;
    }

    public Task<IReadOnlyList<DisconnectStepResult>> RunAsync()
    {
        lock (_lock) return _run ??= ExecuteAsync();
    }

    private async Task<IReadOnlyList<DisconnectStepResult>> ExecuteAsync()
    {
        var results = new List<DisconnectStepResult>();
        foreach (var (name, step) in _steps)
        {
            using var cts = new CancellationTokenSource(_stepTimeout);
            try
            {
                await step(cts.Token).WaitAsync(_stepTimeout);
                results.Add(new DisconnectStepResult(name, true, false, null));
            }
            catch (TimeoutException)
            {
                results.Add(new DisconnectStepResult(name, false, true, "timed out"));
            }
            catch (OperationCanceledException)
            {
                results.Add(new DisconnectStepResult(name, false, true, "timed out"));
            }
            catch (Exception ex)
            {
                // Type only: messages can carry paths or identifiers.
                results.Add(new DisconnectStepResult(name, false, false, ex.GetType().Name));
            }
        }
        return results;
    }
}
