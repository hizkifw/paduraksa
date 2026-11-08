using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// The upstream form's state (PLAN §5.1): name, destinations, load balancing and health. Destinations keep their IDs, so YARP's health
/// state survives the edit (PLAN §6.3); a new destination gets a fresh one.
/// </summary>
public sealed class UpstreamEditModel
{
    private readonly UpstreamConfig? _original;

    private UpstreamEditModel(UpstreamConfig? original, UpstreamId id)
    {
        _original = original;
        Id = id;
    }

    public UpstreamId Id { get; }

    public bool IsNew => _original is null;

    /// <summary>The version the form was opened with (0 for a new upstream).</summary>
    public long Version => _original?.Version ?? 0;

    public string Name { get; set; } = string.Empty;

    public List<DestinationEntry> Destinations { get; } = [];

    /// <summary>Null: YARP's default policy.</summary>
    public LoadBalancingPolicy? LoadBalancing { get; set; }

    public bool SessionAffinity { get; set; }

    public bool PassiveHealth { get; set; }

    /// <summary>How long a destination with failures stays out, in seconds; null: YARP's default.</summary>
    public int? ReactivationSeconds { get; set; }

    public bool ActiveHealth { get; set; }

    public string HealthPath { get; set; } = "/";

    public int HealthIntervalSeconds { get; set; } = (int)ActiveHealthConfig.DefaultInterval.TotalSeconds;

    public int HealthTimeoutSeconds { get; set; } = (int)ActiveHealthConfig.DefaultTimeout.TotalSeconds;

    public int? MaxConnectionsPerServer { get; set; }

    public string? Notes { get; set; }

    /// <summary>A form for a new upstream with one empty destination.</summary>
    public static UpstreamEditModel New()
    {
        UpstreamEditModel model = new(null, UpstreamId.New());
        model.Destinations.Add(new DestinationEntry(DestinationId.New()) { Address = "http://" });
        return model;
    }

    /// <summary>A form for <paramref name="upstream"/> as stored.</summary>
    public static UpstreamEditModel From(UpstreamConfig upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        UpstreamEditModel model = new(upstream, upstream.Id)
        {
            Name = upstream.Name,
            LoadBalancing = upstream.LoadBalancing,
            SessionAffinity = upstream.SessionAffinity,
            PassiveHealth = upstream.PassiveHealth is not null,
            ReactivationSeconds = upstream.PassiveHealth?.ReactivationPeriod is { } period ? (int)period.TotalSeconds : null,
            ActiveHealth = upstream.ActiveHealth is not null,
            HealthPath = upstream.ActiveHealth?.Path ?? "/",
            HealthIntervalSeconds = (int)(upstream.ActiveHealth?.Interval ?? ActiveHealthConfig.DefaultInterval).TotalSeconds,
            HealthTimeoutSeconds = (int)(upstream.ActiveHealth?.Timeout ?? ActiveHealthConfig.DefaultTimeout).TotalSeconds,
            MaxConnectionsPerServer = upstream.MaxConnectionsPerServer,
            Notes = upstream.Notes,
        };
        model.Destinations.AddRange(upstream.Destinations.Select(d => new DestinationEntry(d.Id) { Address = d.Address, Enabled = d.Enabled }));
        return model;
    }

    public void AddDestination() => Destinations.Add(new DestinationEntry(DestinationId.New()) { Address = "http://" });

    /// <summary>The upstream as the command sends it.</summary>
    public UpstreamConfig ToConfig() => new(
        Id,
        Name.Trim(),
        Version,
        [.. Destinations.Select(d => new DestinationConfig(d.Id, d.Address.Trim(), d.Enabled))],
        LoadBalancing,
        SessionAffinity,
        PassiveHealth ? new PassiveHealthConfig { ReactivationPeriod = ReactivationSeconds is > 0 and { } seconds ? TimeSpan.FromSeconds(seconds) : null } : null,
        ActiveHealth
            ? new ActiveHealthConfig(
                string.IsNullOrWhiteSpace(HealthPath) ? "/" : HealthPath.Trim(),
                TimeSpan.FromSeconds(HealthIntervalSeconds),
                TimeSpan.FromSeconds(HealthTimeoutSeconds))
            : null,
        MaxConnectionsPerServer,
        string.IsNullOrWhiteSpace(Notes) ? null : Notes);
}

/// <summary>A destination in the upstream form.</summary>
public sealed class DestinationEntry(DestinationId id)
{
    public DestinationId Id { get; } = id;

    public string Address { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
