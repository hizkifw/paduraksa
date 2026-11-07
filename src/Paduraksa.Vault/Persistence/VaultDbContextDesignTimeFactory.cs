using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Paduraksa.Storage.Sqlite;

namespace Paduraksa.Vault.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build a <see cref="VaultDbContext"/> without the server's composition root. Only the tool
/// uses it. <c>migrations add</c> never opens the database; commands that do (<c>database update</c>,
/// <c>migrations remove</c>, <c>migrations list</c>) use the file passed after <c>--</c>
/// (<c>dotnet ef database update -- path/to/vault.db</c>), or else a scratch file in the temp directory, never
/// a <c>vault.db</c> in the working directory. The server migrates its own database at startup.
/// </summary>
internal sealed class VaultDbContextDesignTimeFactory : IDesignTimeDbContextFactory<VaultDbContext>
{
    public VaultDbContext CreateDbContext(string[] args)
    {
        var path = args is [{ Length: > 0 } given, ..] ? given : Path.Combine(Path.GetTempPath(), "paduraksa-vault-design-time.db");
        var options = new DbContextOptionsBuilder<VaultDbContext>();
        options.UsePaduraksaSqlite(PaduraksaSqlite.ConnectionString(path));
        return new VaultDbContext(options.Options);
    }
}
