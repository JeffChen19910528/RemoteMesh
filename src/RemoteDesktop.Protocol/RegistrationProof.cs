using System.Text;

namespace RemoteDesktop.Protocol;

/// <summary>
/// What a device signs to prove it holds the private key for its DeviceId. The payload binds a server-chosen
/// random challenge (freshness: a captured signature is useless later), the DeviceId and this specific
/// connection (a signature cannot be replayed on another connection), under a fixed context label so it can
/// never be confused with a signature made for another purpose.
/// </summary>
public static class RegistrationProof
{
    public const int ChallengeSize = 32;
    public const int MaxSignatureBytes = 256;
    private const string Context = "RemoteMesh.DeviceRegistration.v1";

    public static byte[] BuildPayload(byte[] challenge, string deviceId, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        using var ms = new MemoryStream();
        Write(ms, Encoding.UTF8.GetBytes(Context));
        Write(ms, challenge);
        Write(ms, Encoding.UTF8.GetBytes(deviceId));
        Write(ms, Encoding.UTF8.GetBytes(connectionId));
        return ms.ToArray();
    }

    // Length-prefixed fields: no concatenation ambiguity between adjacent values.
    private static void Write(Stream s, byte[] field)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, field.Length);
        s.Write(len);
        s.Write(field);
    }
}
