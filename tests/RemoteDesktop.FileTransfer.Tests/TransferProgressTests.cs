using RemoteDesktop.FileTransfer;

namespace RemoteDesktop.FileTransfer.Tests;

public sealed class TransferProgressTests
{
    [Fact]
    public void ProgressPercent_WhenComplete_Is100()
    {
        var p = new TransferProgress
        {
            TransferId = Guid.NewGuid(),
            FileName = "test.zip",
            TotalBytes = 1000,
            TransferredBytes = 1000
        };
        Assert.Equal(100.0, p.ProgressPercent, precision: 1);
    }

    [Fact]
    public void ProgressPercent_WhenEmpty_IsZero()
    {
        var p = new TransferProgress
        {
            TransferId = Guid.NewGuid(),
            FileName = "test.zip",
            TotalBytes = 1000,
            TransferredBytes = 0
        };
        Assert.Equal(0.0, p.ProgressPercent, precision: 1);
    }

    [Fact]
    public void ProgressPercent_WhenZeroTotal_IsZero()
    {
        var p = new TransferProgress
        {
            TransferId = Guid.NewGuid(),
            FileName = "empty.txt",
            TotalBytes = 0,
            TransferredBytes = 0
        };
        Assert.Equal(0.0, p.ProgressPercent, precision: 1);
    }

    [Fact]
    public void DefaultState_IsPending()
    {
        var p = new TransferProgress
        {
            TransferId = Guid.NewGuid(),
            FileName = "file.bin",
            TotalBytes = 500
        };
        Assert.Equal(TransferState.Pending, p.State);
    }
}
