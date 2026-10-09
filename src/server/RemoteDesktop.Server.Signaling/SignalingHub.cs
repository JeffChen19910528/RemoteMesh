using Microsoft.AspNetCore.SignalR;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol.Dtos;
using RemoteDesktop.Server.Application;

namespace RemoteDesktop.Server.Signaling;

/// <summary>
/// The SignalR face of signaling: it only maps hub methods to the business services and carries the caller's
/// connection facts. All rules (authentication, participant checks, compare-and-set transitions, rate limits,
/// uniform error messages, audit) live in <c>Server.Application</c>, which has no SignalR dependency and is the
/// place to change behavior. Refusals are thrown there as <c>SignalingException</c> and surface to clients as
/// <see cref="HubException"/> through <see cref="SignalingExceptionFilter"/>.
/// </summary>
public sealed class SignalingHub(
    DeviceRegistrationService registration,
    SessionNegotiationService negotiation,
    SessionLifecycleService lifecycle,
    KeyRotationService keyRotation,
    TrustedDeviceService trusted) : Hub
{
    private HubCallerAdapter Caller => new(Context);

    // ── Device authentication ────────────────────────────────────────────

    public string BeginRegistration() => registration.BeginRegistration(Caller);

    public Task RegisterDevice(DeviceInfoDto dto, string signatureBase64) =>
        registration.RegisterAsync(Caller, dto, signatureBase64);

    /// <summary>Moves this device to a new key; both the old and the new key sign the statement (see <c>KeyRotationProof</c>).</summary>
    public Task RotateDeviceKey(string newKeyBase64, string oldKeySignatureBase64, string newKeySignatureBase64) =>
        keyRotation.RotateAsync(Caller, newKeyBase64, oldKeySignatureBase64, newKeySignatureBase64);

    public Task<List<KeyRotationDto>> GetPeerKeyRotations(string sessionIdStr) =>
        lifecycle.GetPeerKeyRotationsAsync(Caller, sessionIdStr);

    public Task Heartbeat() => registration.HeartbeatAsync(Caller);

    /// <summary>This device's short ID (nine digits). <paramref name="preferred"/> is the number it had before, if any.</summary>
    public Task<string> ClaimShortId(string? preferred) => registration.ClaimShortIdAsync(Caller, preferred);

    // ── Session request / approval / one-time code ───────────────────────

    public Task<SessionDto> RequestSession(string targetDeviceIdStr) =>
        negotiation.RequestSessionAsync(Caller, targetDeviceIdStr);

    public Task RespondToSession(string sessionIdStr, bool accept, long grantedPermissions = (long)Permission.ViewOnly) =>
        negotiation.RespondToSessionAsync(Caller, sessionIdStr, accept, grantedPermissions);

    public Task<string> GenerateOtp() => Task.FromResult(negotiation.GenerateOtp(Caller));

    public Task<AuthResultDto> RequestSessionWithOtp(string targetDeviceIdStr, string otp) =>
        negotiation.RequestSessionWithOtpAsync(Caller, targetDeviceIdStr, otp);

    // ── Trusted devices ──────────────────────────────────────────────────

    public Task AddTrustedDevice(string trustedDeviceIdStr, string deviceName, long defaultPermissions = (long)Permission.ViewOnly) =>
        trusted.AddAsync(Caller, trustedDeviceIdStr, deviceName, defaultPermissions);

    public Task RemoveTrustedDevice(string trustedDeviceIdStr) => trusted.RemoveAsync(Caller, trustedDeviceIdStr);

    public Task<List<TrustedDeviceDto>> GetTrustedDevices() => trusted.ListAsync(Caller);

    // ── Transport / lifecycle ────────────────────────────────────────────

    public Task SendIceCandidate(string sessionIdStr, string candidateJson) =>
        lifecycle.SendIceCandidateAsync(Caller, sessionIdStr, candidateJson);

    public Task ReportConnectionMode(string sessionIdStr, string mode) =>
        lifecycle.ReportConnectionModeAsync(Caller, sessionIdStr, mode);

    public Task<AuthResultDto> RequestRelayToken(string sessionIdStr) =>
        lifecycle.RequestRelayTokenAsync(Caller, sessionIdStr);

    public Task<string> GetPeerPublicKey(string sessionIdStr) =>
        lifecycle.GetPeerPublicKeyAsync(Caller, sessionIdStr);

    public Task<DeviceInfoDto> GetPeerIdentity(string sessionIdStr) =>
        lifecycle.GetPeerIdentityAsync(Caller, sessionIdStr);

    public Task CloseSession(string sessionIdStr, string? reason = null) =>
        lifecycle.CloseSessionAsync(Caller, sessionIdStr, reason);

    public Task<SessionDto> ResumeSession(string sessionIdStr, string token) =>
        lifecycle.ResumeSessionAsync(Caller, sessionIdStr, token);

    // ── Connection lifecycle ─────────────────────────────────────────────

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await registration.DisconnectedAsync(Caller);
        await base.OnDisconnectedAsync(exception);
    }
}
