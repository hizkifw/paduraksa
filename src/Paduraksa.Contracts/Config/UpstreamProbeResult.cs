using System.Text.Json.Serialization;

namespace Paduraksa.Contracts.Config;

/// <summary>What the upstream probe found.</summary>
[JsonConverter(typeof(ConfigEnumConverter<UpstreamProbeOutcome>))]
public enum UpstreamProbeOutcome
{
    /// <summary>A TCP connection opened.</summary>
    Reachable,

    /// <summary>No connection within the timeout (refused, no route, the name doesn't resolve, nothing answered).</summary>
    Unreachable,

    /// <summary>The address is not an http or https URL; nothing was tried.</summary>
    Invalid,

    /// <summary>The caller may not probe (it lacks <c>config:write</c>); nothing was tried. Audited.</summary>
    Denied,
}

/// <summary>The probe's answer.</summary>
/// <param name="Outcome">Reachable or not (or not tried).</param>
/// <param name="Message">Why it was not tried, for <see cref="UpstreamProbeOutcome.Invalid"/>; never anything the other side said.</param>
public sealed record UpstreamProbeResult(UpstreamProbeOutcome Outcome, string? Message = null)
{
    /// <summary>A connection opened.</summary>
    public bool Reachable => Outcome == UpstreamProbeOutcome.Reachable;
}
