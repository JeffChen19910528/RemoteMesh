using System.Collections.Concurrent;
using System.Net.WebSockets;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Relay;

/// <summary>
/// Multiplexes the session channels over one relay WebSocket. Frames are <c>[channelType:1][payload]</c> and the
/// payload is already end-to-end encrypted by <c>SecureChannel</c>; the relay never sees plaintext.
/// </summary>
public sealed class RelayTransport : ITransport
{
    /// <summary>Largest reassembled message accepted from the relay; larger ones close the connection.</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    private readonly WebSocket _webSocket;
    private readonly ConcurrentDictionary<ChannelType, RelayChannel> _channels = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveLoop;

    public RelayTransport(WebSocket webSocket, SessionId sessionId)
    {
        _webSocket = webSocket;
        SessionId = sessionId;
        Mode = ConnectionMode.Relay;
        // Every channel exists before the first message can be read: the other side may start talking the moment the
        // connection is up, and a message for a channel that is not open yet would be thrown away.
        foreach (var type in Enum.GetValues<ChannelType>())
            _channels[type] = new RelayChannel(type, _webSocket, _sendLock);
        _receiveLoop = RunReceiveLoopAsync(_cts.Token);
    }

    public SessionId SessionId { get; }
    public ConnectionMode Mode { get; }
    public bool IsConnected => _webSocket.State == WebSocketState.Open;

    public event EventHandler<ConnectionMode>? ConnectionModeChanged;

    public Task<IChannel> OpenChannelAsync(ChannelType type, CancellationToken ct = default)
    {
        var channel = _channels.GetOrAdd(type, t => new RelayChannel(t, _webSocket, _sendLock));
        return Task.FromResult<IChannel>(channel);
    }

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_webSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                message.SetLength(0);
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(buffer.AsMemory(), ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxMessageBytes)
                    {
                        await _webSocket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Message too large",
                            CancellationToken.None);
                        return;
                    }
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Binary || message.Length < 1) continue;

                var data = message.GetBuffer();
                var type = (ChannelType)data[0];
                if (!Enum.IsDefined(type) || !_channels.TryGetValue(type, out var channel)) continue;

                var payload = data.AsMemory(1, (int)message.Length - 1).ToArray();
                // Bounded buffer: a flooding peer slows the receive loop instead of exhausting memory.
                await channel.ReceiveBuffer.Writer.WriteAsync(payload, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            foreach (var ch in _channels.Values)
                ch.ReceiveBuffer.Writer.TryComplete();
        }
    }

    public async Task CloseAsync(CancellationToken ct = default)
    {
        await _cts.CancelAsync();
        if (_webSocket.State == WebSocketState.Open)
        {
            try { await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", ct); }
            // The peer or the relay may already have torn the socket down (even disposed it): closing is best effort.
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        try { await _receiveLoop; } catch (Exception) { /* loop already reports via channel completion */ }
        _cts.Dispose();
        _sendLock.Dispose();
        _webSocket.Dispose();
    }
}
