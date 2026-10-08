using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;
using RemoteDesktop.Security;

namespace RemoteDesktop.Transport.Secure;

/// <summary>
/// End-to-end encryption for one channel. The relay (or any on-path party) only ever sees
/// <c>[seq:8][nonce][tag][ciphertext]</c>. The sequence number, channel type and direction are bound into the
/// AEAD associated data, so: replayed frames are dropped, a frame cannot be moved to another channel, and a
/// frame cannot be reflected back to its sender. Frames that fail authentication are dropped and counted;
/// they never reach the application.
/// </summary>
public sealed class SecureChannel : IChannel
{
    private const int SeqSize = 8;
    private const int MinFrame = SeqSize + 12 + 16; // seq + nonce + tag (empty plaintext)

    private readonly IChannel _inner;
    private readonly ISessionCrypto _crypto;
    private readonly SessionId _session;
    private readonly byte _sendDirection;
    private readonly byte _recvDirection;
    private readonly ReplayWindow _window = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private long _sendSeq;
    private long _dropped;
    private long _replayed;

    /// <param name="isInitiator">The side that requested the session. The two sides must disagree on this.</param>
    public SecureChannel(IChannel inner, ISessionCrypto crypto, SessionId session, bool isInitiator)
    {
        _inner = inner;
        _crypto = crypto;
        _session = session;
        _sendDirection = (byte)(isInitiator ? 0 : 1);
        _recvDirection = (byte)(isInitiator ? 1 : 0);
    }

    public Guid ChannelId => _inner.ChannelId;
    public ChannelType Type => _inner.Type;
    public bool IsOpen => _inner.IsOpen;

    /// <summary>Frames discarded for any reason (too short, replay, authentication failure).</summary>
    public long DroppedFrames => Interlocked.Read(ref _dropped);
    public long ReplayedFrames => Interlocked.Read(ref _replayed);

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        // Serialized so frames hit the wire in sequence order.
        await _sendLock.WaitAsync(ct);
        try
        {
            var seq = ++_sendSeq;
            // One allocation: the wire frame, encrypted in place after the sequence number.
            var frame = new byte[SeqSize + data.Length + _crypto.Overhead];
            BinaryPrimitives.WriteInt64BigEndian(frame, seq);
            _crypto.EncryptTo(_session, data.Span, Aad(Type, _sendDirection, seq), frame.AsSpan(SeqSize));
            await _inner.SendAsync(frame, ct);
        }
        finally { _sendLock.Release(); }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var raw in _inner.ReceiveAsync(ct))
        {
            if (raw.Length < MinFrame) { Drop(); continue; }

            var seq = BinaryPrimitives.ReadInt64BigEndian(raw.Span);
            if (!_window.CanAccept(seq))
            {
                Interlocked.Increment(ref _replayed);
                Drop();
                continue;
            }

            byte[] plaintext;
            try
            {
                plaintext = await _crypto.DecryptAsync(_session, raw[SeqSize..], Aad(Type, _recvDirection, seq), ct);
            }
            catch (CryptographicException) { Drop(); continue; }
            catch (ArgumentException) { Drop(); continue; }

            // Only an authenticated frame may consume its sequence number.
            if (!_window.Commit(seq))
            {
                Interlocked.Increment(ref _replayed);
                Drop();
                continue;
            }
            yield return plaintext;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        _sendLock.Dispose();
    }

    private void Drop() => Interlocked.Increment(ref _dropped);

    private static byte[] Aad(ChannelType type, byte direction, long seq)
    {
        var aad = new byte[10];
        aad[0] = (byte)type;
        aad[1] = direction;
        BinaryPrimitives.WriteInt64BigEndian(aad.AsSpan(2), seq);
        return aad;
    }
}
