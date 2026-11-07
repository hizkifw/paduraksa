using System.Globalization;

using Microsoft.Data.Sqlite;

[assembly: AssemblyFixture(typeof(Paduraksa.Tests.Integration.TestDataDirectories))]

namespace Paduraksa.Tests.Integration;

/// <summary>
/// Data directories for test hosts: each <see cref="Hosting.TestHost"/> gets its own (its config database and
/// last-known-good file), under one root per test run that is deleted when the run ends. A host's directory can't be
/// deleted when the host is disposed: the shared SQLite connection pool keeps the database open past that.
/// </summary>
public sealed class TestDataDirectories : IDisposable
{
    private static readonly Lazy<string> Root = new(() => Directory.CreateTempSubdirectory("paduraksa-tests-").FullName);
    private static int s_next;

    /// <summary>A fresh path under the run's root; not created (the host's startup creates it).</summary>
    public static string NewPath() =>
        Path.Combine(Root.Value, "host-" + Interlocked.Increment(ref s_next).ToString(CultureInfo.InvariantCulture));

    public void Dispose()
    {
        if (!Root.IsValueCreated)
        {
            return;
        }

        // Every test has finished: no host still uses its connections.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root.Value, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the system's temp cleaner gets the rest.
        }
    }
}
