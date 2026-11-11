using Paduraksa.Application.Audit;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Commands;

/// <summary>What each command does to a document, with no database: the planner is pure.</summary>
public sealed class ConfigCommandPlannerTests
{
    private static ConfigDocument Current() => ConfigVersions.Stamp(Valid(), Valid(), ConfigDiff.Compare(new ConfigDocument(), Valid()), next: 4);

    private static readonly RevisionRef Head = new(ConfigRevisionId.New(), 3);

    [Fact]
    public void ACreateOfAnObjectThatExistsAlreadyIsAConflictAndAnEditOfOneThatDoesNotIsNotFound()
    {
        var host = Current().Hosts[0];

        var existing = ConfigCommandPlanner.Upsert(Head, host with { Version = 0 }, "hosts", Current().Hosts, h => h.Id.ToString(), h => h.Version, l => Current() with { Hosts = l });
        var missing = ConfigCommandPlanner.Upsert(Head, host with { Id = HostId.New(), Version = 5 }, "hosts", Current().Hosts, h => h.Id.ToString(), h => h.Version, l => Current() with { Hosts = l });

        Assert.Equal(ConfigCommandStatus.Conflict, existing.Early!.Status);
        Assert.Equal(host.Version, existing.Early.Conflict!.CurrentVersion);
        Assert.Equal(AuditOutcome.Conflict, existing.EarlyOutcome);
        Assert.Equal(ConfigCommandStatus.NotFound, missing.Early!.Status);
    }

    [Fact]
    public void AnEditAtTheCurrentVersionArrangesTheSentObjectLastSoAClashIsReportedOnIt()
    {
        var current = Current();
        var sent = current.Hosts[0] with { Notes = "edited" };

        var plan = ConfigCommandPlanner.Upsert(Head, sent, "hosts", current.Hosts, h => h.Id.ToString(), h => h.Version, l => current with { Hosts = l });

        Assert.Null(plan.Early);
        Assert.Equal(sent, plan.Arranged!.Hosts[^1]);
        Assert.Equal(current.Hosts.Count, plan.Arranged.Hosts.Count);
        Assert.Equal(new ConfigCommandPlanner.ObjectKey("hosts", sent.Id.ToString()), plan.Own);
    }

    [Fact]
    public void ADeleteNeedsTheCurrentVersionAndRemovesTheObject()
    {
        var current = Current();
        var host = current.Hosts[0];

        var stale = ConfigCommandPlanner.Delete(Head, host.Id.ToString(), host.Version + 1, current.Hosts, h => h.Id.ToString(), h => h.Version, l => current with { Hosts = l });
        var plan = ConfigCommandPlanner.Delete(Head, host.Id.ToString(), host.Version, current.Hosts, h => h.Id.ToString(), h => h.Version, l => current with { Hosts = l });

        Assert.Equal(ConfigCommandStatus.Conflict, stale.Early!.Status);
        Assert.Null(plan.Early);
        Assert.DoesNotContain(plan.Arranged!.Hosts, h => h.Id == host.Id);
    }

    [Fact]
    public void ASettingsUpdateNamesTheVersionItEdited()
    {
        var current = Current();

        Assert.Null(ConfigCommandPlanner.UpdateSettings(current, Head, current.Settings).Early);
        Assert.Equal(ConfigCommandStatus.Conflict, ConfigCommandPlanner.UpdateSettings(current, Head, current.Settings with { Version = current.Settings.Version + 1 }).Early!.Status);
    }

    [Fact]
    public void AnAuditedActionNamesTheCreateOrTheUpdateByTheVersionSent()
    {
        var host = Current().Hosts[0];

        Assert.Equal("config.host.create", ConfigCommandPlanner.Describe(new UpsertHostCommand(host with { Version = 0 })).Action);
        Assert.Equal("config.host.update", ConfigCommandPlanner.Describe(new UpsertHostCommand(host with { Version = 2 })).Action);
        Assert.Equal(("config.host.delete", host.Id.ToString()), (ConfigCommandPlanner.Describe(new DeleteHostCommand(host.Id, 1)).Action, ConfigCommandPlanner.Describe(new DeleteHostCommand(host.Id, 1)).Target));
    }

    [Fact]
    public void AMessageMustBeValidText()
    {
        Assert.Null(ConfigCommandPlanner.MessageProblem(null));
        Assert.Null(ConfigCommandPlanner.MessageProblem("fine"));
        Assert.NotNull(ConfigCommandPlanner.MessageProblem("lone surrogate " + '\ud800'));
    }

    [Fact]
    public void AMessageHasALengthLimit() =>
        Assert.NotNull(ConfigCommandPlanner.MessageProblem(new string('m', 2000)));
}
