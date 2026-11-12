using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Drafts;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Server.Hosting;
using Paduraksa.Storage.Sqlite;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Config;

/// <summary>A draft lives in the config database: it survives the process, is applied after the restart as one revision, and goes after 30 days untouched.</summary>
public sealed class DraftLifetimeEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<CommandOrigin> OwnerAsync(PaduraksaHost host)
    {
        var token = (await File.ReadAllTextAsync(Path.Combine(host.Options.DataDirectory, SetupService.TokenFileName), Ct)).Trim();
        var owner = (await host.Shared.Setup.CompleteSetupAsync(token, "root", "correct horse battery staple", null, new ClientContext(Channel.Api, IPAddress.Loopback, "test"), Ct)).Owner!;
        return new CommandOrigin(Actor.ForUser(owner.Id), Channel.Api, IPAddress.Loopback);
    }

    [Fact]
    public async Task ADraftSurvivesARestartAndIsAppliedAfterItAsOneRevision()
    {
        var data = TestDataDirectories.NewPath();
        var upstream = ConfigTestSupport.Upstream("restart", "http://10.0.0.20:80");
        var host = ConfigTestSupport.ProxyHost("restart", "restart.test", "restart");
        CommandOrigin owner;
        DraftId draft;
        await using (var first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(Ct);
            owner = await OwnerAsync(first);
            var opened = await first.Shared.Drafts.CreateAsync(owner, Ct);
            draft = opened.Draft!.Id;
            var added = await first.Shared.Drafts.AddAsync(owner, draft, [new UpsertUpstreamCommand(upstream), new UpsertHostCommand(host)], Ct);
            Assert.Equal(DraftStatus.Succeeded, added.Status);
            await first.StopAsync(Ct);
        }

        await using var second = TestHost.Create(dataDirectory: data);
        await second.StartAsync(Ct);
        var seen = (await second.Shared.Drafts.GetAsync(owner, Ct)).Draft!;
        Assert.Equal(draft, seen.Id);
        Assert.Equal(2, seen.Entries.Count);
        Assert.Empty((await second.Shared.Config.GetCurrentAsync(Ct)).Document.Hosts);

        var token = (await second.Shared.Drafts.ReviewAsync(owner, draft, cancellationToken: Ct)).ReviewToken;
        var applied = await second.Shared.Drafts.ApplyAsync(owner, draft, token, cancellationToken: Ct);

        Assert.Equal(ConfigCommandStatus.Committed, applied.Command!.Status);
        Assert.Equal(1, applied.Command.Revision!.Seq);
        Assert.Equal([host.Id], (await second.Shared.Config.GetCurrentAsync(Ct)).Document.Hosts.Select(h => h.Id));
        Assert.Equal(DraftStatus.NoDraft, (await second.Shared.Drafts.GetAsync(owner, Ct)).Status);
    }

    [Fact]
    public async Task ADraftUntouchedFor30DaysIsDroppedAfterTheRestartAndItsOwnerIsToldOnce()
    {
        var data = TestDataDirectories.NewPath();
        CommandOrigin owner;
        await using (var first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(Ct);
            owner = await OwnerAsync(first);
            var draft = (await first.Shared.Drafts.CreateAsync(owner, Ct)).Draft!;
            await first.Shared.Drafts.AddAsync(owner, draft.Id, [new UpsertUpstreamCommand(ConfigTestSupport.Upstream("lapse", "http://10.0.0.21:80"))], Ct);
            await first.StopAsync(Ct);
        }

        await using var second = TestHost.Create(dataDirectory: data);
        await second.StartAsync(Ct);
        StepClock clock = new(second.Shared.TimeProvider.GetUtcNow());
        var factory = new ServiceCollection().AddPaduraksaDbContextFactory<ConfigDbContext>(Path.Combine(data, ConfigDatabase.FileName)).BuildServiceProvider()
            .GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        DraftService drafts = new(factory, second.Shared.Config, second.Shared.PasswordCredentials, second.Shared.Permissions, clock, NullLoggerFactory.Instance);

        clock.Advance(TimeSpan.FromDays(29) + TimeSpan.FromHours(23));
        Assert.Equal(DraftStatus.Succeeded, (await drafts.GetAsync(owner, Ct)).Status);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await drafts.DropExpiredAsync(Ct));

        Assert.Equal(DraftStatus.NoDraft, (await drafts.GetAsync(owner, Ct)).Status);
        var notice = Assert.Single(await drafts.TakeNoticesAsync(owner, Ct));
        Assert.Equal(1, notice.Entries);
        Assert.Empty(await drafts.TakeNoticesAsync(owner, Ct));
    }

    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
