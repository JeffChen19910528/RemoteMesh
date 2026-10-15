using RemoteDesktop.Protocol;

namespace RemoteDesktop.Streaming.Tests;

/// <summary>Maximum Frame Size / bounded memory: a sender cannot make the receiver hold unbounded data.</summary>
public sealed class FrameLimitTests
{
    private static byte[] Chunk(long frame, int index, int count, int payload) =>
        new FramePacket
        {
            FrameNumber = frame, Codec = 0, IsKeyframe = true,
            ChunkIndex = (ushort)index, ChunkCount = (ushort)count, Width = 1, Height = 1,
            Payload = new byte[payload]
        }.ToBytes();

    [Fact]
    public void FrameGrowingPastTheLimit_IsDropped_AndItsRemainingChunksIgnored()
    {
        var r = new FrameReassembler(maxFrameBytes: 1000);

        Assert.Null(r.Add(Chunk(5, 0, 4, 400)));
        Assert.Null(r.Add(Chunk(5, 1, 4, 400)));
        Assert.Null(r.Add(Chunk(5, 2, 4, 400)));   // 1200 > 1000: dropped here
        Assert.Equal(1, r.DroppedFrames);
        Assert.Equal(0, r.PendingBytes);

        Assert.Null(r.Add(Chunk(5, 3, 4, 10)));    // late chunk of the dropped frame: ignored, not re-created
        Assert.Equal(0, r.PendingBytes);
        Assert.Equal(1, r.DroppedFrames);
    }

    [Fact]
    public void FramesWithinTheLimit_StillAssemble_AfterAnOversizedOne()
    {
        var r = new FrameReassembler(maxFrameBytes: 1000);
        r.Add(Chunk(1, 0, 2, 900));
        r.Add(Chunk(1, 1, 2, 900)); // oversized

        Assert.Null(r.Add(Chunk(2, 0, 2, 100)));
        var done = r.Add(Chunk(2, 1, 2, 100));

        Assert.NotNull(done);
        Assert.Equal(200, done!.Data.Length);
        Assert.Equal(0, r.PendingBytes);
    }

    [Fact]
    public void DeclaringManyChunks_DoesNotReserveMemoryUntilBytesActuallyArrive()
    {
        var r = new FrameReassembler(maxFrameBytes: 10_000);

        r.Add(Chunk(1, 0, FramePacket.MaxChunksPerFrame, 10)); // claims 4096 chunks, sent 10 bytes

        Assert.Equal(10, r.PendingBytes);
    }

    [Fact]
    public void TotalPendingMemory_IsBounded_ByEvictingTheOldestIncompleteFrames()
    {
        var r = new FrameReassembler(maxPendingFrames: 100, maxFrameBytes: 10_000, maxPendingBytes: 5_000);

        for (var f = 0; f < 50; f++)
            r.Add(Chunk(f, 0, 2, 1_000)); // 50 incomplete frames of 1000 bytes each

        Assert.True(r.PendingBytes <= 5_000, $"{r.PendingBytes} bytes pending");
        Assert.True(r.DroppedFrames >= 45);
    }

    [Fact]
    public void PendingBytes_ReturnToZero_WhenFramesCompleteOrAreSuperseded()
    {
        var r = new FrameReassembler();
        r.Add(Chunk(1, 0, 2, 100)); // never completes
        r.Add(Chunk(2, 0, 2, 100));
        Assert.Equal(200, r.PendingBytes);

        r.Add(Chunk(2, 1, 2, 100)); // frame 2 completes; frame 1 is now stale and dropped

        Assert.Equal(0, r.PendingBytes);
        Assert.Equal(1, r.DroppedFrames);
    }

    [Fact]
    public void ManyOversizedFrameNumbers_DoNotGrowTheMemoryOfRememberedRejections()
    {
        var r = new FrameReassembler(maxFrameBytes: 10);
        for (var f = 0; f < 10_000; f++) r.Add(Chunk(f, 0, 1, 50)); // each one oversized

        Assert.Equal(10_000, r.DroppedFrames);
        Assert.Equal(0, r.PendingBytes);
    }
}
