namespace RemoteDesktop.Server.Core;

public sealed class SessionRecoveryOptions
{
    /// <summary>How long a Paused session stays resumable after a peer drops.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often expired Paused sessions are swept.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Per session+device limit on resume attempts per minute.</summary>
    public int MaxResumeAttemptsPerMinute { get; init; } = 10;

    /// <summary>Throws with every problem found, so a bad configuration fails at startup instead of misbehaving later.</summary>
    public void Validate()
    {
        var errors = new List<string>();
        if (Window <= TimeSpan.Zero) errors.Add("Recovery:Window must be positive.");
        if (SweepInterval <= TimeSpan.Zero) errors.Add("Recovery:SweepInterval must be positive.");
        if (MaxResumeAttemptsPerMinute < 1) errors.Add("Recovery:MaxResumeAttemptsPerMinute must be at least 1.");
        OptionsValidation.ThrowIfAny(errors);
    }
}
