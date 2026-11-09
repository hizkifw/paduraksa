using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Paduraksa.Storage.Sqlite;

namespace Paduraksa.Data;

/// <summary>
/// Lets <c>dotnet ef</c> build a <see cref="ConfigDbContext"/> without the server's composition root; only the tool uses
/// it. <c>migrations add</c> never opens the database; commands that do take the file passed after <c>--</c>
/// (<c>dotnet ef database update -- path/to/paduraksa.db</c>), else a scratch file in the temp directory, never a
/// <c>paduraksa.db</c> in the working directory.
/// </summary>
internal sealed class ConfigDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ConfigDbContext>
{
    public ConfigDbContext CreateDbContext(string[] args)
    {
        var path = args is [{ Length: > 0 } given, ..] ? given : Path.Combine(Path.GetTempPath(), "paduraksa-design-time.db");
        var options = new DbContextOptionsBuilder<ConfigDbContext>();
        options.UsePaduraksaSqlite(PaduraksaSqlite.ConnectionString(path));
        return new ConfigDbContext(options.Options);
    }
}
