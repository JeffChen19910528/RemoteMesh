namespace RemoteDesktop.Security;

/// <summary>
/// Signs on behalf of a device without handing the private key to the caller, so the key can stay in an
/// OS key store / TPM-backed provider later. Signatures are ECDSA P-256 / SHA-256 (IEEE P1363).
/// </summary>
public interface IDeviceSigner
{
    byte[] PublicKey { get; }
    byte[] Sign(byte[] data);
}

public sealed class DeviceKeyPairSigner(DeviceKeyPair keyPair) : IDeviceSigner
{
    public byte[] PublicKey => keyPair.PublicKey;
    public byte[] Sign(byte[] data) => DeviceKeyGenerator.Sign(keyPair, data);
}
