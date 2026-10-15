using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RemoteDesktop.Clipboard;

namespace RemoteDesktop.Platform.Windows;

/// <summary>
/// The Windows clipboard behind <see cref="IClipboardManager"/>. Text (CF_UNICODETEXT) and PNG images
/// (the "PNG" registered format that Chrome, Firefox, and most Windows apps understand) are supported.
/// Sharing stays off until the user opts in: the default policy is <see cref="ClipboardPolicy.Disabled"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardManager : IClipboardManager
{
    private const int OpenAttempts = 10; // another app may hold the clipboard for a moment
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(15);

    // "PNG" is a de-facto standard registered clipboard format used by Chrome, Firefox, and most Win32 apps.
    // RegisterClipboardFormatW is idempotent: repeated calls with the same name return the same ID.
    private static readonly uint CF_PNG = Native.RegisterClipboardFormatW("PNG");

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

    public async Task<byte[]?> GetImageAsync(CancellationToken ct = default)
    {
        if (!await OpenAsync(ct)) return null;
        try
        {
            if (!Native.IsClipboardFormatAvailable(CF_PNG)) return null;
            var handle = Native.GetClipboardData(CF_PNG);
            if (handle == IntPtr.Zero) return null;

            var ptr = Native.GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                var size = (long)Native.GlobalSize(handle).ToUInt64();
                if (size <= 0 || size > ClipboardLimits.MaxImageBytes) return null;
                var result = new byte[(int)size];
                Marshal.Copy(ptr, result, 0, (int)size);
                return result;
            }
            finally { Native.GlobalUnlock(handle); }
        }
        finally { Native.CloseClipboard(); }
    }

    public async Task SetImageAsync(byte[] imageData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        if (imageData.Length > ClipboardLimits.MaxImageBytes) throw new IOException("The image is too large for the clipboard.");
        if (!await OpenAsync(ct)) throw new IOException("The clipboard is in use by another application.");
        try
        {
            Native.EmptyClipboard();
            var mem = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)imageData.Length);
            if (mem == IntPtr.Zero) throw new OutOfMemoryException("Could not allocate clipboard memory.");

            var ptr = Native.GlobalLock(mem);
            if (ptr == IntPtr.Zero) { Native.GlobalFree(mem); throw new IOException("Could not lock clipboard memory."); }
            try { Marshal.Copy(imageData, 0, ptr, imageData.Length); }
            finally { Native.GlobalUnlock(mem); }

            if (Native.SetClipboardData(CF_PNG, mem) == IntPtr.Zero)
            {
                Native.GlobalFree(mem);
                throw new IOException("The clipboard rejected the image.");
            }
        }
        finally { Native.CloseClipboard(); }
    }

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
