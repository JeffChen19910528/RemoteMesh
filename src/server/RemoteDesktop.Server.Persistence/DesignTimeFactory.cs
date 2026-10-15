using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RemoteDesktop.Server.Persistence;

/// <summary>
/// Only for <c>dotnet ef migrations add</c>: the model is all that matters, nothing connects. The server itself
/// never uses this.
/// </summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<RemoteMeshDbContext>
{
    public RemoteMeshDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<RemoteMeshDbContext>().UseNpgsql("Host=localhost;Database=design-time-only").Options);
}
