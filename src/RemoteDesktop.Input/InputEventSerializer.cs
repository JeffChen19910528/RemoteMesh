using System.Buffers.Binary;

namespace RemoteDesktop.Input;

/// <summary>Fixed-size binary wire format for <see cref="InputEvent"/>. Input is untrusted: parse never throws.</summary>
public static class InputEventSerializer
{
    public const int Size = 16 + 8 + 1 + 4 * 6;

    public static byte[] Serialize(InputEvent e)
    {
        var b = new byte[Size];
        e.EventId.TryWriteBytes(b);
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(16), e.Timestamp.UtcTicks);
        b[24] = (byte)e.EventType;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(25), e.X);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(29), e.Y);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(33), e.Button);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(37), e.Delta);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(41), e.KeyCode);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(45), e.Modifiers);
        return b;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out InputEvent? e)
    {
        e = null;
        if (data.Length != Size) return false;
        if (!Enum.IsDefined((InputEventType)data[24])) return false;

        var ticks = BinaryPrimitives.ReadInt64BigEndian(data[16..]);
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return false;

        e = new InputEvent
        {
            EventId = new Guid(data[..16]),
            Timestamp = new DateTimeOffset(ticks, TimeSpan.Zero),
            EventType = (InputEventType)data[24],
            X = BinaryPrimitives.ReadInt32BigEndian(data[25..]),
            Y = BinaryPrimitives.ReadInt32BigEndian(data[29..]),
            Button = BinaryPrimitives.ReadInt32BigEndian(data[33..]),
            Delta = BinaryPrimitives.ReadInt32BigEndian(data[37..]),
            KeyCode = BinaryPrimitives.ReadInt32BigEndian(data[41..]),
            Modifiers = BinaryPrimitives.ReadInt32BigEndian(data[45..])
        };
        return true;
    }
}
