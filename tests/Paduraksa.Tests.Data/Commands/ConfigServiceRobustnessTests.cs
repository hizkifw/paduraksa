using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Commands;
using Paduraksa.Core.Model;
using Paduraksa.Storage.Sqlite;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Data.Commands.Objects;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Commands;

public sealed class ConfigServiceRobustnessTests
{
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
        // Microsoft.Data.Sqlite retries a busy statement for its own window, which the connection string derives from the busy
        // timeout. Pinned shorter than the pragma here, so the wait can only be the pragma's busy handler: without it the write
        // would be refused at this one-second window instead (and the elapsed assertion below would fail).
        await using var h = await ConfigServiceHarness.CreateAsync(sqliteRetryWindow: TimeSpan.FromSeconds(1));
        await using var locker = new SqliteConnection(PaduraksaSqlite.ConnectionString(h.Db.DatabasePath));
        await locker.OpenAsync(Ct);
        await using var transaction = locker.BeginTransaction(deferred: false); // BEGIN IMMEDIATE: holds the write lock

        var stopwatch = Stopwatch.StartNew();
        var result = await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "bad name")));

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        Assert.InRange(stopwatch.Elapsed, SqlitePragmas.BusyTimeout * 0.8, SqlitePragmas.BusyTimeout * 3);
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

    private sealed class CancelThenDeny(CancellationTokenSource cancel) : IPermissionEvaluator
    {
        public ValueTask<IReadOnlySet<Permission>> GetPermissionsAsync(Actor actor, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlySet<Permission>>(new HashSet<Permission>());

        public async ValueTask<bool> IsAllowedAsync(CommandOrigin origin, Permission permission, CancellationToken cancellationToken)
        {
            await cancel.CancelAsync();
            return false;
        }
    }
}
