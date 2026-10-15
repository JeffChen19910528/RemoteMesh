using RemoteDesktop.Core;

namespace RemoteDesktop.Protocol.Dtos;

public sealed class SessionDto
{
    public string SessionId { get; set; } = string.Empty;
    public string RequesterDeviceId { get; set; } = string.Empty;
    public string TargetDeviceId { get; set; } = string.Empty;
    public string? RequesterShortId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string State { get; set; } = nameof(SessionState.Created);
    public string ConnectionMode { get; set; } = nameof(Core.ConnectionMode.Unknown);
    public int Epoch { get; set; }

    // Populated when session was immediately authorized (trusted-device or OTP fast-path).
    public AuthResultDto? Auth { get; set; }

    public static SessionDto From(SessionInfo s) => new()
    {
        SessionId = s.SessionId.ToString(),
        RequesterDeviceId = s.RequesterDeviceId.ToString(),
        TargetDeviceId = s.TargetDeviceId.ToString(),
        RequesterShortId = s.RequesterShortId,
        CreatedAt = s.CreatedAt,
        ExpiresAt = s.ExpiresAt,
        State = s.State.ToString(),
        ConnectionMode = s.ConnectionMode.ToString(),
        Epoch = s.Epoch
    };

    public SessionInfo ToSessionInfo() => new()
    {
        SessionId = Core.SessionId.Parse(SessionId),
        RequesterDeviceId = Core.DeviceId.Parse(RequesterDeviceId),
        TargetDeviceId = Core.DeviceId.Parse(TargetDeviceId),
        RequesterShortId = RequesterShortId,
        CreatedAt = CreatedAt,
        ExpiresAt = ExpiresAt,
        State = Enum.Parse<SessionState>(State),
        ConnectionMode = Enum.Parse<Core.ConnectionMode>(ConnectionMode),
        Epoch = Epoch
    };
}
