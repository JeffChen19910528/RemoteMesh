namespace RemoteDesktop.Server.Core;

/// <summary>Limits that keep one device from abusing the signaling server or another device's user.</summary>
public sealed class SessionSecurityOptions
{
    /// <summary>How long a pending request may wait for the target user to approve it.</summary>
    public TimeSpan ApprovalTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public int MaxPendingRequestsPerTarget { get; init; } = 5;
    public int MaxActiveSessionsPerDevice { get; init; } = 8;

    /// <summary>Maximum Sessions: server-wide cap on live sessions (memory and relay capacity).</summary>
    public int MaxTotalActiveSessions { get; init; } = 10_000;

    /// <summary>Finished sessions are dropped from memory after this long.</summary>
    public TimeSpan FinishedSessionRetention { get; init; } = TimeSpan.FromHours(1);
    public int SessionRequestsPerMinute { get; init; } = 20;

    /// <summary>Lookups of unknown device IDs allowed per requester per 10 minutes (anti device-ID enumeration).</summary>
    public int UnknownDeviceLookupsPer10Min { get; init; } = 10;

    public int RegistrationFailuresPerIpPer10Min { get; init; } = 10;
    public int RegistrationFailuresPerConnection { get; init; } = 3;

    /// <summary>Relay tokens are used right after issue; a short life limits what a leaked one is worth.</summary>
    public TimeSpan RelayTokenLifetime { get; init; } = TimeSpan.FromMinutes(2);

    public int RelayTokenRequestsPerMinute { get; init; } = 10;
    public int MaxCandidateJsonBytes { get; init; } = 4096;
    public int MaxDeviceNameLength { get; init; } = 64;
    public int MaxTrustedDevicesPerOwner { get; init; } = 200;

    /// <summary>Throws with every problem found, so a bad configuration fails at startup instead of misbehaving later.</summary>
    public void Validate()
    {
        var errors = new List<string>();
        void Positive(string name, int value) { if (value < 1) errors.Add($"Session:{name} must be at least 1."); }
        void PositiveTime(string name, TimeSpan value) { if (value <= TimeSpan.Zero) errors.Add($"Session:{name} must be positive."); }

        PositiveTime(nameof(ApprovalTimeout), ApprovalTimeout);
        PositiveTime(nameof(RelayTokenLifetime), RelayTokenLifetime);
        PositiveTime(nameof(FinishedSessionRetention), FinishedSessionRetention);
        Positive(nameof(MaxPendingRequestsPerTarget), MaxPendingRequestsPerTarget);
        Positive(nameof(MaxActiveSessionsPerDevice), MaxActiveSessionsPerDevice);
        Positive(nameof(MaxTotalActiveSessions), MaxTotalActiveSessions);
        Positive(nameof(SessionRequestsPerMinute), SessionRequestsPerMinute);
        Positive(nameof(UnknownDeviceLookupsPer10Min), UnknownDeviceLookupsPer10Min);
        Positive(nameof(RegistrationFailuresPerIpPer10Min), RegistrationFailuresPerIpPer10Min);
        Positive(nameof(RegistrationFailuresPerConnection), RegistrationFailuresPerConnection);
        Positive(nameof(RelayTokenRequestsPerMinute), RelayTokenRequestsPerMinute);
        Positive(nameof(MaxCandidateJsonBytes), MaxCandidateJsonBytes);
        Positive(nameof(MaxDeviceNameLength), MaxDeviceNameLength);
        Positive(nameof(MaxTrustedDevicesPerOwner), MaxTrustedDevicesPerOwner);
        // A relay token that outlives the approval window would make a stale approval usable.
        if (RelayTokenLifetime > TimeSpan.FromHours(1)) errors.Add("Session:RelayTokenLifetime must not exceed 1 hour (tokens must be short-lived).");
        OptionsValidation.ThrowIfAny(errors);
    }
}
