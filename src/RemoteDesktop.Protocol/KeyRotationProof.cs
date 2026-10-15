using System.Text;

namespace RemoteDesktop.Protocol;

/// <summary>
/// What the old key and the new key both sign to move a device from one key to the next. The statement names the
/// device and both keys (so neither signature can be reused for another device or another pair), under its own
/// context label (so it can never be mistaken for a registration signature).
/// </summary>
public static class KeyRotationProof
{
    public const int MaxKeyBytes = 256;
    private const string Context = "RemoteMesh.KeyRotation.v1";

    public static byte[] BuildPayload(string deviceId, byte[] oldKey, byte[] newKey)
    {
        ArgumentNullException.ThrowIfNull(oldKey);
        ArgumentNullException.ThrowIfNull(newKey);
        using var ms = new MemoryStream();
        Write(ms, Encoding.UTF8.GetBytes(Context));
        Write(ms, Encoding.UTF8.GetBytes(deviceId));
        Write(ms, oldKey);
        Write(ms, newKey);
        return ms.ToArray();
    }

    private static void Write(Stream s, byte[] field)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, field.Length);
        s.Write(len);
        s.Write(field);
    }
}
