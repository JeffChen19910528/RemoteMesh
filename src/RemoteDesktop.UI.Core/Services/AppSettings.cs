using System.IO;
using System.Text.Json;

namespace RemoteDesktop.UI.Services;

/// <summary>What the user sets once. Nothing secret lives here: keys are in the device key store.</summary>
public sealed class AppSettings
{
    public string ServerUrl { get; set; } = string.Empty;
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>Let a connected viewer use this computer's clipboard (also needs the right granted per session).</summary>
    public bool AllowClipboard { get; set; }

    /// <summary>Folder remote file transfers can reach. Empty = file transfer off.</summary>
    public string FileRoot { get; set; } = string.Empty;

    /// <summary>
    /// The most bandwidth the picture of this computer may use while someone watches it (kilobits per second).
    /// Lower it on a slow upload; the picture gets softer instead of stalling. Edit in settings.json.
    /// </summary>
    public int VideoBudgetKbps { get; set; } = 6000;

    /// <summary>The problem with these settings, or null when they are usable.</summary>
    public string? Validate()
    {
        if (!Uri.TryCreate(ServerUrl?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "伺服器網址必須是完整的 http(s) 網址,例如 https://relay.example.com";
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
            return "非本機的伺服器必須使用 https(連線資料需要受到保護)";
        if (string.IsNullOrWhiteSpace(DeviceName) || DeviceName.Length > 64)
            return "裝置名稱必須是 1 到 64 個字元";
        if (!string.IsNullOrWhiteSpace(FileRoot) && !Path.IsPathRooted(FileRoot))
            return "檔案資料夾必須是完整路徑";
        if (VideoBudgetKbps is < 500 or > 100_000)
            return "畫面頻寬上限必須介於 500 到 100000 kbps";
        return null;
    }

    public Uri ServerUri => new(ServerUrl.Trim().TrimEnd('/') + "/");
}

/// <summary>Reads and writes <see cref="AppSettings"/> as JSON in the user's profile.</summary>
public sealed class SettingsStore(string? directory = null)
{
    private readonly string _path = Path.Combine(
        directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteMesh"),
        "settings.json");

    public string DataDirectory => Path.GetDirectoryName(_path)!;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged settings file must not stop the app from starting: fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        // Write to a temp file first so a crash mid-write cannot leave a half-written settings file.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }
}
