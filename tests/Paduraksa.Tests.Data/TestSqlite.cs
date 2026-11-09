using Paduraksa.Storage.Sqlite;

[assembly: AssemblyFixture(typeof(Paduraksa.Tests.Data.TestSqlite))]

namespace Paduraksa.Tests.Data;

/// <summary>
/// The SQLite settings for a test run. The busy timeout is the one a test can wait out, so it is a short one: the two tests that
/// prove the wait (<c>SqliteSetupTests.BusyWriterFailsAfterTheBusyTimeoutNotMicrosoftDataSqlites30Seconds</c> and
/// <c>ConfigServiceRobustnessTests.ARejectionIsReportedWhenTheDatabaseIsLockedPastTheBusyTimeout</c>) still prove that the busy
/// handler is what fails the write, because each pins Microsoft.Data.Sqlite's own retry window to a shorter one second: the wait
/// then measured is the pragma's, and without the pragma the write would be refused at the shorter window. Nothing here changes
/// what production uses.
/// </summary>
public sealed class TestSqlite : IDisposable
{
    private readonly TimeSpan _original = SqlitePragmas.BusyTimeout;

    public TestSqlite() => SqlitePragmas.BusyTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Puts the timeout back when that is still allowed. The seam refuses a change once a connection has opened, which every
    /// run does, so in practice this process ends with the short timeout; a run that never opened one gets the old value back.
    /// </summary>
    public void Dispose()
    {
        if (SqlitePragmas.BusyTimeoutIsSettable)
        {
            SqlitePragmas.BusyTimeout = _original;
        }
    }
}
