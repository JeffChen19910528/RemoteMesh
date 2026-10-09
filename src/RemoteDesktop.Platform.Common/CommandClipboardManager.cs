using System.Text;
using RemoteDesktop.Clipboard;

namespace RemoteDesktop.Platform;

/// <summary>
/// A text clipboard driven by the command-line tools the operating system ships (or its desktop packages). Text goes
/// to the tool on stdin and comes back on stdout, never as an argument, so clipboard content can never become a
/// command. If there is no tool the clipboard is simply unavailable (reads are empty, writes refuse) and the rest of
/// the remote session works. Sharing stays off until the user opts in (<see cref="ClipboardPolicy.Disabled"/> by
/// default). Images are not supported yet.
/// </summary>
public class CommandClipboardManager : IClipboardManager
{
    public const int MaxTextBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public sealed record Tool(string ReadFile, string[] ReadArgs, string WriteFile, string[] WriteArgs);

    private readonly ICommandRunner _runner;
    private readonly Tool? _tool;
    private readonly string _installHint;

    protected CommandClipboardManager(ICommandRunner runner, Tool? tool, string installHint)
    {
        _runner = runner;
        _tool = tool;
        _installHint = installHint;
    }

    public ClipboardPolicy Policy { get; set; } = ClipboardPolicy.Disabled;

    /// <summary>False when no clipboard tool is installed.</summary>
    public bool IsAvailable => _tool is not null;

    public async Task<string?> GetTextAsync(CancellationToken ct = default)
    {
        if (_tool is null) return null;
        var result = await _runner.RunAsync(_tool.ReadFile, _tool.ReadArgs, null, captureOutput: true, Timeout, ct);
        // An empty clipboard makes some of these tools exit non-zero: that is "nothing there", not an error.
        if (result.TimedOut || result.ExitCode != 0 || result.Output.Length == 0) return null;
        if (result.Output.Length > MaxTextBytes) return null;
        return Encoding.UTF8.GetString(result.Output);
    }

    public async Task SetTextAsync(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_tool is null) throw new NotSupportedException(_installHint);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxTextBytes) throw new IOException("The text is too large for the clipboard.");

        var result = await _runner.RunAsync(_tool.WriteFile, _tool.WriteArgs, bytes, captureOutput: false, Timeout, ct);
        if (result.TimedOut || result.ExitCode != 0) throw new IOException("The clipboard tool did not accept the text.");
    }

    public Task<byte[]?> GetImageAsync(CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

    public Task SetImageAsync(byte[] imageData, CancellationToken ct = default) =>
        throw new NotSupportedException("Image clipboard is not supported yet.");
}
