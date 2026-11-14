using System.Text.Json.Serialization;

namespace Paduraksa.Contracts.Config;

/// <summary>
/// The server's own settings that the dashboard may change (docs/design/config.md "Server settings"): how long data is kept, the sign-in
/// policy and the like, as opposed to how requests are routed. Part of <see cref="GlobalSettings"/>, so a change is a revision with its audit
/// event, its diff and its undo. Every field is nullable: null is the built-in default. A value the server's environment sets
/// explicitly (<c>Paduraksa__Metrics__Retention</c>) wins over the stored one.
/// </summary>
/// <remarks>
/// Each property has exactly one declaration in <see cref="ServerSettingCatalog"/>, which gives its key, bounds, default and environment key;
/// <c>ServerSettingCatalogTests</c> fails for a property without one. New properties go at the end, so stored documents keep their key order.
/// </remarks>
public sealed record ServerSettings
{
    public TimeSpan? MetricsRetention { get; init; }

    public TimeSpan? AnalyticsRetention { get; init; }

    public TimeSpan? NotificationHistoryRetention { get; init; }

    public long? LogRetainedFiles { get; init; }

    public TimeSpan? LogMaxAge { get; init; }

    public long? LogMaxTotalSize { get; init; }

    public long? LogMaxFileSize { get; init; }

    public long? AdminMaxFailedAttempts { get; init; }

    public TimeSpan? AdminLockoutDuration { get; init; }

    public TimeSpan? AdminSessionLifetime { get; init; }

    public TimeSpan? AdminSessionIdleTimeout { get; init; }

    public TimeSpan? AdminPartialSessionLifetime { get; init; }

    public long? AdminMinPasswordLength { get; init; }

    public string? AdminTotpIssuer { get; init; }

    public long? AdminRateLimitPermitsPerIp { get; init; }

    public long? AdminRateLimitPermitsPerUser { get; init; }

    public TimeSpan? AdminRateLimitWindow { get; init; }

    public string? NotificationAdminUrl { get; init; }

    public TimeSpan? NotificationHealthHold { get; init; }

    public TimeSpan? NotificationCoalesceWindow { get; init; }

    public TimeSpan? NotificationMaxDeliveryAge { get; init; }

    public bool? AccessLogQueryString { get; init; }

    public bool? CloudflareRangeRefresh { get; init; }

    public TimeSpan? CloudflareRangeInterval { get; init; }

    public TimeSpan? AcmeDnsPropagationDelay { get; init; }

    public bool? AcmeDnsPropagationCheck { get; init; }

    public bool? McpEnabled { get; init; }

    public TimeSpan? McpIdleTimeout { get; init; }

    public TimeSpan? LimitsRequestHeadersTimeout { get; init; }

    public TimeSpan? LimitsKeepAliveTimeout { get; init; }

    public TimeSpan? LimitsTlsHandshakeTimeout { get; init; }

    public long? LimitsMinRequestBodyRate { get; init; }

    public TimeSpan? LimitsMinRequestBodyGrace { get; init; }

    public long? LimitsMinResponseRate { get; init; }

    public TimeSpan? LimitsMinResponseGrace { get; init; }

    public long? LimitsMaxConnections { get; init; }

    public long? LimitsMaxUpgradedConnections { get; init; }

    /// <summary>Whether no field is set: canonical JSON then writes no <c>server</c> key, so a stored revision keeps its bytes.</summary>
    [JsonIgnore]
    public bool SetsNothing => ServerSettingCatalog.All.All(setting => !setting.IsSet(this));
}
