using System.Diagnostics;

namespace RemoteDesktop.Platform;

/// <summary>Runs a helper program with arguments (never through a shell). Replaceable so the logic is testable without one.</summary>
public interface ICommandRunner
{
    /// <summary>Runs <paramref name="file"/>; feeds <paramref name="input"/> to its stdin when not null; returns its exit code and (if asked) stdout.</summary>
    Task<CommandResult> RunAsync(string file, IReadOnlyList<string> arguments, byte[]? input, bool captureOutput, TimeSpan timeout, CancellationToken ct);

    /// <summary>True if <paramref name="file"/> can be found on the PATH.</summary>
    bool Exists(string file);
}

public readonly record struct CommandResult(int ExitCode, byte[] Output, bool TimedOut);

/// <summary>Starts real programs. Arguments are passed as a list (no shell, so clipboard text can never become a command).</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    public bool Exists(string file)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(dir, file))) return true; }
            catch (ArgumentException) { /* a bad PATH entry */ }
        }
        return false;
    }

    public async Task<CommandResult> RunAsync(string file, IReadOnlyList<string> arguments, byte[]? input, bool captureOutput, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardInput = input is not null,
            // A writer such as xclip forks and keeps serving the clipboard: if its output were piped to us, reading it
            // would never end. Writers inherit ours; readers are piped.
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        // pbcopy/pbpaste (and others) read the locale to decide the text encoding: text is UTF-8 here, whatever the session says.
        info.Environment["LC_CTYPE"] = "UTF-8";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LANG"))) info.Environment["LANG"] = "en_US.UTF-8";

        using var process = Process.Start(info) ?? throw new IOException($"Could not start {file}.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(input, limit.Token);
                process.StandardInput.Close();
            }
            Task<byte[]>? read = null;
            if (captureOutput)
            {
                read = ReadCappedAsync(process.StandardOutput.BaseStream, CommandClipboardManager.MaxTextBytes + 1, limit.Token);
                _ = process.StandardError.ReadToEndAsync(limit.Token); // drained so it can never fill up and block the tool
            }
            await process.WaitForExitAsync(limit.Token);
            return new CommandResult(process.ExitCode, read is null ? [] : await read, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new CommandResult(-1, [], true);
        }
    }

    private static async Task<byte[]> ReadCappedAsync(Stream s, int max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while (ms.Length < max && (n = await s.ReadAsync(buffer, ct)) > 0) ms.Write(buffer, 0, n);
        return ms.ToArray();
    }
}
