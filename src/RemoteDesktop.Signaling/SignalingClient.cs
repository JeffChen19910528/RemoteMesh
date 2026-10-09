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

    /// <summary>
    /// After a reconnect, resume every session that has a recovery token (default). A caller that wants to decide
    /// itself, and in what order relative to rebuilding its own connections, turns this off and calls
    /// <see cref="ResumeSessionAsync"/>.
    /// </summary>
    public bool ResumeSessionsAfterReconnect { get; set; } = true;

    /// <summary>Raised after a lost connection came back and the device is registered again (new server connection).</summary>
    public event Action? ConnectionRestored;

    public event Action<AuthResultDto>? Approved;
    public event Action<AuthResultDto>? Authorized;
    public event Action<AuthResultDto>? Established;
    public event Action<SessionId, string>? SessionEnded;
    public event Action<SessionInfo>? SessionStateChanged;

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
            .WithAutomaticReconnect(new KeepTryingRetryPolicy())
            .Build();

        _connection.On<string, string>("RecoveryTokenIssued", (sessionId, token) =>
        {
            if (SessionId.TryParse(sessionId, out var id)) _recoveryTokens[id] = token;
        });

        _connection.On<string, string>("SessionClosed", (sessionId, reason) =>
        {
            if (!SessionId.TryParse(sessionId, out var id)) return;
            _recoveryTokens.TryRemove(id, out _);
            SessionEnded?.Invoke(id, reason);
        });

        _connection.On<AuthResultDto>("AuthSuccess", dto => Approved?.Invoke(dto));
        _connection.On<AuthResultDto>("SessionAuthorized", dto => Authorized?.Invoke(dto));
        _connection.On<AuthResultDto>("SessionEstablished", dto => Established?.Invoke(dto));

        _connection.Reconnected += _ => RestoreAfterReconnectAsync();

        _connection.On<SessionDto>("IncomingSessionRequest", dto =>
            _incomingRequests.Writer.TryWrite(dto.ToSessionInfo()));

        _connection.On<SessionDto>("SessionStateChanged", dto =>
        {
            var info = dto.ToSessionInfo();
            _stateUpdates.Writer.TryWrite(info);
            SessionStateChanged?.Invoke(info);
        });

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
        var connection = _connection!;
        var challenge = Convert.FromBase64String(await connection.InvokeAsync<string>("BeginRegistration", ct));
        var dto = DeviceInfoDto.From(device, signer.PublicKey);
        var payload = RegistrationProof.BuildPayload(challenge, dto.DeviceId, connection.ConnectionId ?? string.Empty);
        await connection.InvokeAsync("RegisterDevice", dto, Convert.ToBase64String(signer.Sign(payload)), ct);
        _device = device;
        _signer = signer;
    }

    public async Task RotateDeviceKeyAsync(DeviceKeyPair oldKeys, DeviceKeyPair newKeys, CancellationToken ct = default)
    {
        EnsureConnected();
        if (_device is null) throw new InvalidOperationException("Register the device first.");
        var payload = KeyRotationProof.BuildPayload(_device.DeviceId.ToString(), oldKeys.PublicKey, newKeys.PublicKey);
        await _connection!.InvokeAsync("RotateDeviceKey", Convert.ToBase64String(newKeys.PublicKey),
            Convert.ToBase64String(DeviceKeyGenerator.Sign(oldKeys, payload)),
            Convert.ToBase64String(DeviceKeyGenerator.Sign(newKeys, payload)), ct);
        _signer = new DeviceKeyPairSigner(newKeys); // the next registration (after a reconnect) is made with the new key
    }

    public async Task<IReadOnlyList<KeyRotation>> GetPeerKeyRotationsAsync(SessionId sessionId, DeviceId peer, CancellationToken ct = default)
    {
        EnsureConnected();
        var dtos = await _connection!.InvokeAsync<List<KeyRotationDto>>("GetPeerKeyRotations", sessionId.ToString(), ct);
        return dtos.Select(d => new KeyRotation
        {
            DeviceId = peer,
            Sequence = d.Sequence,
            OldKey = Convert.FromBase64String(d.OldKeyBase64),
            NewKey = Convert.FromBase64String(d.NewKeyBase64),
            OldKeySignature = Convert.FromBase64String(d.OldKeySignatureBase64),
            NewKeySignature = Convert.FromBase64String(d.NewKeySignatureBase64)
        }).ToList();
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
        if (_device is null || _signer is null) return;
        // The connection is back but the server does not know this device on it yet. A failure here (the network
        // flapping again) must not leave the device unregistered, so try a few times while the connection holds.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await RegisterDeviceAsync(_device, _signer);
                break;
            }
            catch (Exception) when (attempt < 5 && IsConnected)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (Exception) { return; } // gave up, or the connection dropped again (which reconnects and comes back here)
        }

        ConnectionRestored?.Invoke();
        if (!ResumeSessionsAfterReconnect) return;

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

    public Task RespondToSessionAsync(SessionId sessionId, bool accept, CancellationToken ct = default) =>
        RespondToSessionAsync(sessionId, accept, Permission.ViewOnly, ct);

    public async Task RespondToSessionAsync(SessionId sessionId, bool accept, Permission granted, CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("RespondToSession", sessionId.ToString(), accept, (long)granted, ct);
    }

    public Task<(SessionInfo Session, AuthResultDto? Auth)> RequestSessionWithAuthAsync(DeviceId targetDeviceId, CancellationToken ct = default) =>
        RequestSessionWithAuthAsync(targetDeviceId.ToString(), ct);

    public async Task<(SessionInfo Session, AuthResultDto? Auth)> RequestSessionWithAuthAsync(string target, CancellationToken ct = default)
    {
        EnsureConnected();
        var dto = await _connection!.InvokeAsync<SessionDto>("RequestSession", target, ct);
        return (dto.ToSessionInfo(), dto.Auth);
    }

    public async Task<string> ClaimShortIdAsync(string? preferred, CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<string>("ClaimShortId", preferred, ct);
    }

    public async Task<string> GenerateOtpAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<string>("GenerateOtp", ct);
    }

    public Task<AuthResultDto> RequestSessionWithOtpAsync(DeviceId targetDeviceId, string otp, CancellationToken ct = default) =>
        RequestSessionWithOtpAsync(targetDeviceId.ToString(), otp, ct);

    public async Task<AuthResultDto> RequestSessionWithOtpAsync(string target, string otp, CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<AuthResultDto>("RequestSessionWithOtp", target, otp, ct);
    }

    public async Task<(DeviceId DeviceId, string Name, byte[] PublicKey)> GetPeerIdentityAsync(SessionId sessionId, CancellationToken ct = default)
    {
        EnsureConnected();
        var dto = await _connection!.InvokeAsync<DeviceInfoDto>("GetPeerIdentity", sessionId.ToString(), ct);
        return (DeviceId.Parse(dto.DeviceId), dto.DeviceName, Convert.FromBase64String(dto.PublicKeyBase64));
    }

    public async Task<byte[]> GetPeerPublicKeyAsync(SessionId sessionId, CancellationToken ct = default)
    {
        EnsureConnected();
        return Convert.FromBase64String(await _connection!.InvokeAsync<string>("GetPeerPublicKey", sessionId.ToString(), ct));
    }

    public async Task<AuthResultDto> RequestRelayTokenAsync(SessionId sessionId, CancellationToken ct = default)
    {
        EnsureConnected();
        return await _connection!.InvokeAsync<AuthResultDto>("RequestRelayToken", sessionId.ToString(), ct);
    }

    public async Task ReportConnectionModeAsync(SessionId sessionId, ConnectionMode mode, CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("ReportConnectionMode", sessionId.ToString(), mode.ToString(), ct);
    }

    public async Task CloseSessionAsync(SessionId sessionId, string? reason = null, CancellationToken ct = default)
    {
        EnsureConnected();
        await _connection!.InvokeAsync("CloseSession", sessionId.ToString(), reason, ct);
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

    /// <summary>
    /// SignalR's default gives up after four attempts (about 45 seconds). A machine that sleeps, or sits in a lift,
    /// should find its way back whenever the network returns, so this keeps trying until the client is disposed: quickly
    /// at first, then every two to five seconds. The randomness keeps thousands of clients from hitting a server that
    /// has just restarted at the same instant.
    /// </summary>
    private sealed class KeepTryingRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Steps = [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            retryContext.PreviousRetryCount < Steps.Length
                ? Steps[retryContext.PreviousRetryCount]
                : TimeSpan.FromMilliseconds(Random.Shared.Next(2000, 5000));
    }

    private void EnsureConnected()
    {
        if (_connection is null || _connection.State == HubConnectionState.Disconnected)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
    }
}
