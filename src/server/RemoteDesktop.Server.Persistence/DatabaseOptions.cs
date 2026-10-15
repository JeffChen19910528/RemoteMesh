using Npgsql;

namespace RemoteDesktop.Server.Persistence;

/// <summary>
/// <c>RemoteMesh:Database</c>. With no connection string the server keeps everything in memory (one instance,
/// forgets on restart). The connection string carries the password: give it through the environment
/// (<c>RemoteMesh__Database__ConnectionString</c>), never a committed file.
/// </summary>
public sealed class DatabaseOptions
{
    public string? ConnectionString { get; set; }

    /// <summary>Audit entries older than this are deleted (daily). 0 keeps them forever.</summary>
    public int AuditRetentionDays { get; set; } = 90;

    public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);

    public void Validate()
    {
        if (AuditRetentionDays is < 0 or > 3650)
            throw new InvalidOperationException("RemoteMesh:Database:AuditRetentionDays must be between 0 and 3650.");
        if (!Enabled) return;
        try { _ = new NpgsqlConnectionStringBuilder(ConnectionString); }
        catch (Exception) // the exception text would echo the value, which holds the password
        {
            throw new InvalidOperationException("RemoteMesh:Database:ConnectionString is not a valid PostgreSQL connection string.");
        }
    }
}
