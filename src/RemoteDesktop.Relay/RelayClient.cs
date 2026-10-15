using System.Net.WebSockets;
using RemoteDesktop.Core;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Relay;

public sealed class RelayClient : IRelayClient
{
    private ClientWebSocket? _webSocket;
    private RelayTransport? _transport;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public async Task ConnectAsync(Uri relayUri, SessionId sessionId, string sessionToken,
        DeviceId deviceId, CancellationToken ct = default)
    {
        _webSocket = new ClientWebSocket();
        // Token in a header, not the URL: URLs are logged by proxies and servers.
        _webSocket.Options.SetRequestHeader("X-Relay-Token", sessionToken);
        var builder = new UriBuilder(relayUri)
        {
            Query = $"sessionId={sessionId}&deviceId={deviceId}"
        };
        await _webSocket.ConnectAsync(builder.Uri, ct);
        _transport = new RelayTransport(_webSocket, sessionId);
    }

    public Task<ITransport> GetTransportAsync(CancellationToken ct = default)
    {
        if (_transport is null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
        return Task.FromResult<ITransport>(_transport);
    }

    public Task DisconnectAsync(CancellationToken ct = default)
        => _transport?.CloseAsync(ct) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_transport is not null)
            await _transport.DisposeAsync();
        _webSocket?.Dispose();
    }
}
