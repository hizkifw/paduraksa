using Microsoft.Data.Sqlite;
using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Commands;
using Paduraksa.Core.Model;
using Paduraksa.Storage.Sqlite;
using static Paduraksa.Tests.Data.Commands.Objects;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Commands;

/// <summary>What the pipeline does when the world misbehaves: another writer on the file, a broken audit table, a
/// cancelled caller.</summary>
public sealed class ConfigServiceRobustnessTests
{
    [Fact]
    public async Task WhenAnotherWriterCommitsBeforeTheCommitTheCommandIsPreparedAgainOnTheNewHead()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        using var otherProcess = new ConfigService(h.Db.Factory, new ConfigServiceHarness.FakeCompiler(), h.Publisher);
        var moves = 1;
        h.Compiler.OnCompile = _ =>
        {
            if (moves-- > 0)
            {
                Commit(otherProcess, "other.example.com");
            }
        };

        var result = await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")));

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        Assert.Equal(2, result.Revision!.Seq);
        Assert.Equal(2, h.Compiler.Compiled.Count); // planned, validated and compiled again
        Assert.Equal(2, (await h.CurrentAsync()).Hosts.Count);
        await h.AssertProjectionMatchesAsync();
        await h.AssertChainIntactAsync();
    }

    [Fact]
    public async Task WhenTheHeadMovesTwiceItIsAConflictNamingTheRealHead()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        using var otherProcess = new ConfigService(h.Db.Factory, new ConfigServiceHarness.FakeCompiler(), h.Publisher);
        var id = HostId.New();
        await h.RunAsync(new UpsertHostCommand(RespondHost(id, "a.example.com")));
        var moves = 0;
        h.Compiler.OnCompile = _ => Commit(otherProcess, $"other{++moves}.example.com");

        var result = await h.RunAsync(new UpsertHostCommand(RespondHost(id, "a.example.com") with { Version = 1, Notes = "edit" }));

        Assert.Equal(ConfigCommandStatus.Conflict, result.Status);
        Assert.Equal(3, result.Conflict!.CurrentRevision!.Seq);
        Assert.Equal(1, result.Conflict.CurrentVersion);
        Assert.Equal(3, await h.CountAsync("ConfigRevisions"));
        Assert.Null((await h.CurrentAsync()).Hosts.Single(x => x.Id == id).Notes);
        var audit = (await h.AuditAsync())[^1];
        Assert.Equal(("config.host.update", AuditOutcome.Conflict), (audit.Action, audit.Outcome));
        await h.AssertChainIntactAsync();
    }

    [Fact]
    public async Task ARejectionIsReportedEvenWhenItsAuditEventCantBeWritten()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        await h.Db.ExecuteAsync("DROP TABLE AuditEvents");

        var invalid = await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "bad name")));
        var notFound = await h.RunAsync(new DeleteHostCommand(HostId.New(), 3));

        Assert.Equal(ConfigCommandStatus.Invalid, invalid.Status);
        Assert.Equal(ConfigCommandStatus.NotFound, notFound.Status);
    }

    [Fact]
    public async Task ARejectionIsReportedWhenTheDatabaseIsLockedPastTheBusyTimeout()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        await using var locker = new SqliteConnection(PaduraksaSqlite.ConnectionString(h.Db.DatabasePath));
        await locker.OpenAsync(Ct);
        await using var transaction = locker.BeginTransaction(deferred: false); // BEGIN IMMEDIATE: holds the write lock

        var result = await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "bad name")));

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ARejectionIsAuditedAndReportedEvenIfTheCallerCancelsAfterTheDecision()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var h = await ConfigServiceHarness.CreateAsync(new CancelThenDeny(cancel));

        var result = await h.Service.ExecuteAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")), ConfigServiceHarness.Admin, cancel.Token);

        Assert.Equal(ConfigCommandStatus.Denied, result.Status);
        Assert.Equal(AuditOutcome.Denied, Assert.Single(await h.AuditAsync()).Outcome);
    }

    [Fact]
    public async Task ADisposedServiceTakesNoNewCommandsButFinishesOnesInFlight()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.Compiler.OnCompile = _ =>
        {
            entered.TrySetResult();
            release.Task.Wait(Ct);
        };

        var inFlight = Task.Run(() => h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com"))), Ct);
        await entered.Task.WaitAsync(Ct);
        h.Service.Dispose();
        release.SetResult();

        Assert.Equal(ConfigCommandStatus.Committed, (await inFlight).Status);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "b.example.com"))));
    }

    private static void Commit(ConfigService service, string name)
    {
        // The other process's command, run while this one is between its load and its commit (compile is synchronous).
        var result = service.ExecuteAsync(new UpsertHostCommand(RespondHost(HostId.New(), name)), CommandOrigin.Cli, Ct).GetAwaiter().GetResult();
        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
    }

    private sealed class CancelThenDeny(CancellationTokenSource cancel) : IConfigCommandAuthorizer
    {
        public async ValueTask<bool> IsAllowedAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken)
        {
            await cancel.CancelAsync();
            return false;
        }
    }
}
