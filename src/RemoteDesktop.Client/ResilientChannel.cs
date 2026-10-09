using System.Runtime.CompilerServices;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Client;

/// <summary>
/// A channel that survives the connection under it being replaced. The loops that use a channel (screen, input,
/// keep-alive, files...) keep holding this one object; when the relay connection dies and a new one is built, the new
/// channel is attached here and they carry on. While nothing is attached, sending waits (or, for input, is dropped:
/// a mouse move from ten seconds ago is worse than none) and receiving simply has nothing to deliver.
/// </summary>
internal sealed class ResilientChannel : IChannel
{
    private readonly object _lock = new();
    private readonly bool _dropWhileDown;
    private IChannel? _inner;
    private long _version;               // bumped on every attach, so a waiter knows whether "its" channel was replaced
    private bool _closed;
    private TaskCompletionSource _changed = NewSignal();

    public ResilientChannel(ChannelType type, bool dropWhileDown = false)
    {
        Type = type;
        _dropWhileDown = dropWhileDown;
    }

    public Guid ChannelId { get; } = Guid.NewGuid();
    public ChannelType Type { get; }
    public bool IsOpen { get { lock (_lock) return !_closed && _inner is { IsOpen: true }; } }

    /// <summary>Makes <paramref name="inner"/> the channel in use; the previous one (if any) is disposed.</summary>
    public async Task AttachAsync(IChannel inner)
    {
        IChannel? old;
        TaskCompletionSource signal;
        lock (_lock)
        {
            if (_closed) { old = inner; signal = _changed; }
            else
            {
                old = _inner;
                _inner = inner;
                _version++;
                signal = _changed;
                _changed = NewSignal();
            }
        }
        signal.TrySetResult();
        if (old is not null) { try { await old.DisposeAsync(); } catch (Exception) { } }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        while (true)
        {
            var (inner, version) = await CurrentAsync(ct, wait: !_dropWhileDown);
            if (inner is null)
            {
                lock (_lock) if (_closed) throw new ObjectDisposedException(nameof(ResilientChannel));
                return; // dropped on purpose while down (see the class comment)
            }
            try
            {
                await inner.SendAsync(data, ct);
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested && WasLost(inner, version))
            {
                // The connection died under this message: it is lost with it. Wait for the next connection (or give
                // up if this channel drops its traffic while down).
                if (_dropWhileDown) return;
            }
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            var (inner, version) = await CurrentAsync(ct, wait: true);
            if (inner is null) yield break; // closed for good

            var items = inner.ReceiveAsync(ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    bool more;
                    try { more = await items.MoveNextAsync(); }
                    catch (Exception) when (!ct.IsCancellationRequested) { more = false; } // the connection died
                    if (!more) break;
                    yield return items.Current;
                }
            }
            finally { await items.DisposeAsync(); }

            // This connection is over; deliver nothing until it has been replaced (or the channel was closed).
            await WaitForReplacementAsync(version, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        IChannel? inner;
        TaskCompletionSource signal;
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
            inner = _inner;
            _inner = null;
            signal = _changed;
        }
        signal.TrySetResult();
        if (inner is not null) { try { await inner.DisposeAsync(); } catch (Exception) { } }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private bool WasLost(IChannel inner, long version)
    {
        lock (_lock) return _closed || _version != version || !inner.IsOpen;
    }

    private async Task<(IChannel? Channel, long Version)> CurrentAsync(CancellationToken ct, bool wait)
    {
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                if (_closed) return (null, _version);
                if (_inner is { IsOpen: true }) return (_inner, _version);
                if (!wait) return (null, _version);
                changed = _changed.Task;
            }
            // Nothing usable right now (never attached, or attached but already dead): wait for the next attach.
            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task WaitForReplacementAsync(long version, CancellationToken ct)
    {
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                if (_closed || _version != version) return;
                changed = _changed.Task;
            }
            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
