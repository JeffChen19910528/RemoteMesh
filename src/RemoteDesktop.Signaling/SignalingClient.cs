using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Security;

namespace RemoteDesktop.Signaling;

public sealed class SignalingClient : ISignalingClient
{
    private HubConnection? _connection;
    private DeviceId _deviceId;
    private readonly Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions>? _configure;

    // Kept so that after an automatic reconnect (new server-side connection id) the device can re-register
    // and its sessions can be resumed. Recovery tokens are secrets: held in memory only, never logged.
    private DeviceInfo? _device;
    private IDeviceSigner? _signer;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SessionId, string> _recoveryTokens = new();

    /// <summary>Raised after a reconnect once the device is re-registered and each resumable session was attempted.</summary>
    public event Action<SessionId, bool>? SessionRecoveryAttempted;

    public SignalingClient(Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions>? configureConnection = null)
        => _configure = configureConnection;

    public bool HasRecoveryToken(SessionId sessionId) => _recoveryTokens.ContainsKey(sessionId);

    private readonly Channel<SessionInfo> _incomingRequests =
        Channel.CreateUnbounded<SessionInfo>(new UnboundedChannelOptions { SingleReader = false });

    private readonly Channel<SessionInfo> _stateUpdates =
        Channel.CreateUnbounded<SessionInfo>(new UnboundedChannelOptions { SingleReader = false });

    public bool IsConnected =>
        _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(Uri signalingUri, DeviceId deviceId, CancellationToken ct = default)
    {
        _deviceId = deviceId;

        _connection = new HubConnectionBuilder()
            .WithUrl(signalingUri, o => _configure?.Invoke(o))
            .WithAutomaticReconnect()
            .Build();

        _connection.On<string, string>("RecoveryTokenIssued", (sessionId, token) =>
        {
            if (SessionId.TryParse(sessionId, out var id)) _recoveryTokens[id] = token;
        });

        _connection.On<string, string>("SessionClosed", (sessionId, _) =>
        {
            if (SessionId.TryParse(sessionId, out var id)) _recoveryTokens.TryRemove(id, out _);
        });

        _connection.Reconnected += _ => RestoreAfterReconnectAsync();

        _connection.On<SessionDto>("IncomingSessionRequest", dto =>
            _incomingRequests.Writer.TryWrite(dto.ToSessionInfo()));

        _connection.On<SessionDto>("SessionStateChanged", dto =>
            _stateUpdates.Writer.TryWrite(dto.ToSessionInfo()));

        await _connection.StartAsync(ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_connection is not null)
            await _connection.StopAsync(ct);
    }

    public async Task RegisterDeviceAsync(DeviceInfo device, IDeviceSigner signer, CancellationToken ct = default)
    {
        EnsureConnected();
        // Challenge-response: the server sends a fresh random challenge bound to this connection; we sign it.
        // The private key stays inside the signer and nothing secret goes over the wire.
        var challenge = Convert.FromBase64String(await _connection!.InvokeAsync<string>("BeginRegistration", ct));
        var dto = DeviceInfoDto.From(device, signer.PublicKey);
        var payload = RegistrationProof.BuildPayload(challenge, dto.DeviceId, _connection.ConnectionId ?? string.Empty);
        await _connection.InvokeAsync("RegisterDevice", dto, Convert.ToBase64String(signer.Sign(payload)), ct);
        _device = device;
        _signer = signer;
    }

    /// <summary>Resumes a Paused session using the recovery token the server issued when it connected.</summary>
    public async Task<SessionInfo> ResumeSessionAsync(SessionId sessionId, CancellationToken ct = default)
    {
        EnsureConnected();
        if (!_recoveryTokens.TryGetValue(sessionId, out var token))
            throw new InvalidOperationException("No recovery token for this session.");
        var dto = await _connection!.InvokeAsync<SessionDto>("ResumeSession", sessionId.ToString(), token, ct);
        // Recovery tokens are single-use: the reply carries the replacement for the next drop.
        if (dto.Auth is { SessionToken.Length: > 0 } auth) _recoveryTokens[sessionId] = auth.SessionToken;
        else _recoveryTokens.TryRemove(sessionId, out _);
        return dto.ToSessionInfo();
    }

    // Automatic reconnect gives a new server connection id: registration is lost, so redo it, then resume.
    private async Task RestoreAfterReconnectAsync()
    {
        try
        {
            if (_device is null || _signer is null) return;
            await RegisterDeviceAsync(_device, _signer);
        }
        catch (Exception) { return; } // the next reconnect or an explicit call retries

        foreach (var sessionId in _recoveryTokens.Keys)
        {
            var ok = false;
            try
            {
                var s = await ResumeSessionAsync(sessionId);
                ok = true;
                _stateUpdates.Writer.TryWrite(s);
            }
            catch (HubException)
            {
                // The server refused (expired/closed/token used): drop the stale token so it is not retried.
                _recoveryTokens.TryRemove(sessionId, out _);
            }
            catch (Exception)
            {
                // Transient failure (network): keep the token for the next attempt.
            }
            SessionRecoveryAttempted?.Invoke(sessionId, ok);
        }
    }

    public async Task<SessionInfo> RequestSessionAsync(DeviceId targetDeviceId, CancellationToken ct = default)
    {
        EnsureConnected();
        var dto = await _connection!.InvokeAsync<SessionDto>("RequestSession", targetDeviceId.ToString(), ct);
        return dto.ToSessionInfo();
    }

    public async Task RespondToSessionAsync(SessionId sessionId, bool accept, CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("RespondToSession", sessionId.ToString(), accept, ct);
    }

    public async Task HeartbeatAsync(CancellationToken ct = default)
    {
        if (IsConnected)
            await _connection!.InvokeAsync("Heartbeat", ct);
    }

    public IAsyncEnumerable<SessionInfo> IncomingSessionRequestsAsync(CancellationToken ct = default) =>
        _incomingRequests.Reader.ReadAllAsync(ct);

    public IAsyncEnumerable<SessionInfo> SessionStateUpdatesAsync(CancellationToken ct = default) =>
        _stateUpdates.Reader.ReadAllAsync(ct);

    public async ValueTask DisposeAsync()
    {
        _incomingRequests.Writer.TryComplete();
        _stateUpdates.Writer.TryComplete();
        if (_connection is not null)
            await _connection.DisposeAsync();
    }

    private void EnsureConnected()
    {
        if (_connection is null || _connection.State == HubConnectionState.Disconnected)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
    }
}
