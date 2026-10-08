namespace RemoteDesktop.Security;

public sealed class DeviceKeyPair
{
    public required byte[] PublicKey { get; init; }

    // Private key is never serialized or logged
    public required byte[] PrivateKey { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
