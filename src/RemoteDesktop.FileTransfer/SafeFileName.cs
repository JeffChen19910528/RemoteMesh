namespace RemoteDesktop.FileTransfer;

/// <summary>
/// Remote-supplied names must be a single plain file name inside the transfer root:
/// no separators, drive letters, traversal, device names, hidden/partial-area names or trailing dots/spaces.
/// </summary>
public static class SafeFileName
{
    private const int MaxLength = 255;
    private static readonly char[] ExtraInvalid = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool IsValid(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength) return false;
        if (name[0] == '.') return false; // also covers "." and ".." and the .partial area
        if (name[^1] is '.' or ' ') return false;
        if (name.Any(c => c < 32 || ExtraInvalid.Contains(c) || Path.GetInvalidFileNameChars().Contains(c)))
            return false;

        var stem = name.Split('.')[0].TrimEnd(' ');
        return !Reserved.Contains(stem);
    }
}
