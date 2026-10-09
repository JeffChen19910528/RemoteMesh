using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteDesktop.Clipboard;

namespace RemoteDesktop.Platform.Windows;

/// <summary>
/// The Windows clipboard behind <see cref="IClipboardManager"/>. Text is supported; images are not yet
/// (<see cref="GetImageAsync"/> reports an empty clipboard and <see cref="SetImageAsync"/> throws
/// <see cref="NotSupportedException"/>, which the clipboard host turns into a refusal).
/// Sharing stays off until the user opts in: the default policy is <see cref="ClipboardPolicy.Disabled"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardManager : IClipboardManager
{
    private const int OpenAttempts = 10; // another app may hold the clipboard for a moment
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(15);

    public ClipboardPolicy Policy { get; set; } = ClipboardPolicy.Disabled;

    public async Task<string?> GetTextAsync(CancellationToken ct = default)
    {
        if (!await OpenAsync(ct)) return null;
        try
        {
            if (!Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT)) return null;
            var handle = Native.GetClipboardData(Native.CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;

            var ptr = Native.GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                // Bound the read by the allocation size so a malformed (unterminated) buffer cannot overrun.
                var maxChars = (int)Math.Min(Native.GlobalSize(handle).ToUInt64() / 2, int.MaxValue);
                var text = Marshal.PtrToStringUni(ptr, maxChars);
                var end = text.IndexOf('\0');
                return end >= 0 ? text[..end] : text;
            }
            finally { Native.GlobalUnlock(handle); }
        }
        finally { Native.CloseClipboard(); }
    }

    public async Task SetTextAsync(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!await OpenAsync(ct)) throw new IOException("The clipboard is in use by another application.");
        try
        {
            Native.EmptyClipboard();
            var bytes = (text.Length + 1) * 2; // UTF-16 plus terminator
            var mem = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)bytes);
            if (mem == IntPtr.Zero) throw new OutOfMemoryException("Could not allocate clipboard memory.");

            var ptr = Native.GlobalLock(mem);
            if (ptr == IntPtr.Zero) { Native.GlobalFree(mem); throw new IOException("Could not lock clipboard memory."); }
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * 2, 0);
            }
            finally { Native.GlobalUnlock(mem); }

            // On success the system owns the memory; on failure we still do.
            if (Native.SetClipboardData(Native.CF_UNICODETEXT, mem) == IntPtr.Zero)
            {
                Native.GlobalFree(mem);
                throw new IOException("The clipboard rejected the data.");
            }
        }
        finally { Native.CloseClipboard(); }
    }

    public Task<byte[]?> GetImageAsync(CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

    public Task SetImageAsync(byte[] imageData, CancellationToken ct = default) =>
        throw new NotSupportedException("Image clipboard is not supported yet.");

    private static async Task<bool> OpenAsync(CancellationToken ct)
    {
        for (var i = 0; i < OpenAttempts; i++)
        {
            if (Native.OpenClipboard(IntPtr.Zero)) return true;
            await Task.Delay(OpenRetryDelay, ct);
        }
        return false;
    }
}
