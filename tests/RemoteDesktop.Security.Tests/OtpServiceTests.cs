using RemoteDesktop.Core;
using RemoteDesktop.Server.Core;
using RemoteDesktop.Server.Security;

namespace RemoteDesktop.Security.Tests;

public sealed class OtpServiceTests
{
    private static OtpService NewService(TimeSpan? ttl = null, int maxAttempts = 5)
        => new(ttl, maxAttempts);

    [Fact]
    public void Generate_Returns6DigitCode()
    {
        var svc = NewService();
        var code = svc.Generate(DeviceId.New());

        Assert.Equal(6, code.Length);
        Assert.True(int.TryParse(code, out var n) && n is >= 100_000 and <= 999_999);
    }

    [Fact]
    public void Verify_ValidCode_ReturnsValid()
    {
        var svc = NewService();
        var device = DeviceId.New();
        var code = svc.Generate(device);

        Assert.Equal(OtpVerifyResult.Valid, svc.Verify(device, code));
    }

    [Fact]
    public void Verify_WrongCode_ReturnsInvalid()
    {
        var svc = NewService();
        var device = DeviceId.New();
        svc.Generate(device);

        Assert.Equal(OtpVerifyResult.Invalid, svc.Verify(device, "000000"));
    }

    [Fact]
    public void Verify_SingleUse_SecondVerifyReturnsNotFound()
    {
        var svc = NewService();
        var device = DeviceId.New();
        var code = svc.Generate(device);

        svc.Verify(device, code);  // first — consumes

        Assert.Equal(OtpVerifyResult.NotFound, svc.Verify(device, code));
    }

    [Fact]
    public void Verify_Expired_ReturnsExpired()
    {
        var svc = NewService(ttl: TimeSpan.FromMilliseconds(1));
        var device = DeviceId.New();
        var code = svc.Generate(device);

        System.Threading.Thread.Sleep(10);

        Assert.Equal(OtpVerifyResult.Expired, svc.Verify(device, code));
    }

    [Fact]
    public void Verify_TooManyAttempts_ReturnsRateLimited()
    {
        var svc = NewService(maxAttempts: 3);
        var device = DeviceId.New();
        svc.Generate(device);

        for (var i = 0; i < 3; i++)
            svc.Verify(device, "000000");

        Assert.Equal(OtpVerifyResult.RateLimited, svc.Verify(device, "000000"));
    }

    [Fact]
    public void Invalidate_RemovesCode()
    {
        var svc = NewService();
        var device = DeviceId.New();
        svc.Generate(device);

        svc.Invalidate(device);

        Assert.Equal(OtpVerifyResult.NotFound, svc.Verify(device, "123456"));
    }

    [Fact]
    public void Generate_ResetsAttemptCounter()
    {
        var svc = NewService(maxAttempts: 2);
        var device = DeviceId.New();
        svc.Generate(device);

        svc.Verify(device, "000000");
        svc.Verify(device, "000000");

        // Locked out before regeneration
        Assert.Equal(OtpVerifyResult.RateLimited, svc.Verify(device, "000000"));

        // Generate resets counter
        var newCode = svc.Generate(device);
        Assert.Equal(OtpVerifyResult.Valid, svc.Verify(device, newCode));
    }
}
