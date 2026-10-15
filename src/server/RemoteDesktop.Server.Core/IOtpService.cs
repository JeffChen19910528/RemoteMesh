using RemoteDesktop.Core;

namespace RemoteDesktop.Server.Core;

public enum OtpVerifyResult { Valid, Invalid, Expired, RateLimited, NotFound }

public interface IOtpService
{
    // Target device calls this to get a fresh code to display.
    string Generate(DeviceId forDevice);

    // Verifies and consumes the OTP. Rate-limits excessive wrong attempts.
    OtpVerifyResult Verify(DeviceId targetDevice, string code);

    void Invalidate(DeviceId forDevice);
}
