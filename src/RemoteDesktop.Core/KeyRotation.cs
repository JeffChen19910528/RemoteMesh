namespace RemoteDesktop.Core;

/// <summary>
/// One step of a device's key history: it moved from <see cref="OldKey"/> to <see cref="NewKey"/>. Both keys signed
/// the same statement, so anyone who trusts the old key can check, without trusting the server, that the new key
/// belongs to the same device.
/// </summary>
public sealed class KeyRotation
{
    /// <summary>A device cannot rotate without limit; the history stays small enough to hand out.</summary>
    public const int MaxPerDevice = 100;

    public required DeviceId DeviceId { get; init; }
    /// <summary>0 for the first rotation, then 1, 2, ...</summary>
    public required int Sequence { get; init; }
    public required byte[] OldKey { get; init; }
    public required byte[] NewKey { get; init; }
    public required byte[] OldKeySignature { get; init; }
    public required byte[] NewKeySignature { get; init; }
    public DateTimeOffset RotatedAt { get; init; } = DateTimeOffset.UtcNow;
}
