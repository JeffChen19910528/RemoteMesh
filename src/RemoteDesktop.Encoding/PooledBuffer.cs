using System.Buffers;

namespace RemoteDesktop.Encoding;

/// <summary>
/// A buffer rented from the shared pool. Frame-sized buffers (megabytes) land on the large object heap, and
/// allocating a fresh one per frame forces frequent gen-2 collections; renting avoids that.
/// </summary>
public sealed class PooledBuffer : IDisposable
{
    private byte[]? _array;

    public PooledBuffer(int minimumLength) => _array = ArrayPool<byte>.Shared.Rent(minimumLength);

    public byte[] Array => _array ?? throw new ObjectDisposedException(nameof(PooledBuffer));

    public void Dispose()
    {
        var a = Interlocked.Exchange(ref _array, null);
        if (a is not null) ArrayPool<byte>.Shared.Return(a);
    }
}
