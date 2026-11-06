using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Ids;
using Paduraksa.Data.Ids;

namespace Paduraksa.Tests.Data;

// Sample IDs and entities standing in for the real M2 model: one of each ID kind, used as primary key,
// foreign key, nullable property, inside a column-mapped complex type and inside a JSON complex type.

[TypedId("host")]
public readonly partial record struct HostId;

[TypedId("rte")]
public readonly partial record struct RouteId;

[TypedId("chl", IdKind.UuidV4)]
public readonly partial record struct ChallengeId;

[TypedId("aud", IdKind.Int64Ordered)]
public readonly partial record struct AuditEventId;

public sealed class Host
{
    public HostId Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Nullable UuidV7 self-reference (FK).</summary>
    public HostId? ParentId { get; set; }

    /// <summary>JSON complex type holding IDs of every kind.</summary>
    public HostSettings Settings { get; set; } = new();

    public List<Route> Routes { get; } = [];
}

public sealed class HostSettings
{
    public RouteId DefaultRouteId { get; set; }

    public HostId? FallbackHostId { get; set; }

    public ChallengeId? ChallengeId { get; set; }

    public AuditEventId? LastAuditId { get; set; }

    public ChallengeSettings Challenge { get; set; } = new();

    /// <summary>Primitive collection inside the JSON document.</summary>
    public List<RouteId> AllowedRouteIds { get; set; } = [];

    /// <summary>Complex collection inside the JSON document.</summary>
    public List<HostAlias> Aliases { get; set; } = [];
}

public sealed class HostAlias
{
    public HostId TargetHostId { get; set; }

    public AuditEventId? AddedByAuditId { get; set; }
}

/// <summary>Nested complex type inside the JSON document.</summary>
public sealed class ChallengeSettings
{
    public ChallengeId? PinnedChallengeId { get; set; }

    public AuditEventId FirstAuditId { get; set; }
}

public sealed class Route
{
    public RouteId Id { get; set; }

    public HostId HostId { get; set; }

    public Host? Host { get; set; }

    public int Order { get; set; }

    public ChallengeId? ChallengeId { get; set; }

    /// <summary>Primitive collection in its own column (a JSON array).</summary>
    public List<HostId> RelatedHostIds { get; set; } = [];

    /// <summary>Column-mapped (not JSON) complex type: its IDs are ordinary columns.</summary>
    public RouteOrigin Origin { get; set; } = new();
}

public sealed class RouteOrigin
{
    public RouteId? CopiedFromRouteId { get; set; }

    public AuditEventId? CreatedByAuditId { get; set; }
}

public sealed class Challenge
{
    public ChallengeId Id { get; set; }

    public HostId HostId { get; set; }
}

public sealed class AuditEvent
{
    public AuditEventId Id { get; set; }

    public AuditEventId? PreviousId { get; set; }

    public HostId? TargetHostId { get; set; }

    public string Action { get; set; } = "";
}

public sealed class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options)
{
    public DbSet<Host> Hosts => Set<Host>();

    public DbSet<Route> Routes => Set<Route>();

    public DbSet<Challenge> Challenges => Set<Challenge>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.AddTypedIdConventions();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Host>(host =>
        {
            host.HasOne<Host>().WithMany().HasForeignKey(h => h.ParentId);
            host.ComplexProperty(h => h.Settings, settings =>
            {
                settings.ToJson();
                settings.ComplexCollection(s => s.Aliases);
            });
        });
        modelBuilder.Entity<Route>().ComplexProperty(r => r.Origin);
        modelBuilder.Entity<Challenge>().HasOne<Host>().WithMany().HasForeignKey(c => c.HostId);
        modelBuilder.Entity<AuditEvent>().HasOne<AuditEvent>().WithOne().HasForeignKey<AuditEvent>(a => a.PreviousId);
    }
}
