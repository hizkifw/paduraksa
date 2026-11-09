using MudBlazor;

using Paduraksa.Contracts.Simulation;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// The simulator's request form (PLAN §7.7): a URL and a client IP are enough; method, headers, how the request arrives and the TLS
/// context are optional and default as a browser on the LAN would.
/// </summary>
public sealed class SimulationForm
{
    /// <summary>A client on a typical home network.</summary>
    public const string DefaultClientIp = "192.168.1.20";

    public string Url { get; set; } = "https://";

    public string ClientIp { get; set; } = DefaultClientIp;

    public string Method { get; set; } = "GET";

    public List<HeaderEntry> Headers { get; } = [];

    public SimulationArrival Arrival { get; set; } = SimulationArrival.Direct;

    /// <summary>The proxy's own address, for a request through a proxy; empty: one the config trusts.</summary>
    public string PeerIp { get; set; } = string.Empty;

    public SimulationTlsVersion? TlsVersion { get; set; }

    public SimulationHttpVersion? HttpVersion { get; set; }

    /// <summary>The name in the TLS handshake; empty: the URL's host.</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>Whether the URL is HTTPS (only then does the TLS context apply).</summary>
    public bool IsHttps => Url.TrimStart().StartsWith("https:", StringComparison.OrdinalIgnoreCase);

    /// <summary>The request for the simulator. Fields that would change nothing (TLS on plain HTTP, a peer on a direct request) are left out.</summary>
    public SimulationRequest ToRequest()
    {
        bool viaProxy = Arrival != SimulationArrival.Direct;
        SimulationTls? tls = IsHttps && (TlsVersion is not null || HttpVersion is not null || !string.IsNullOrWhiteSpace(ServerName))
            ? new SimulationTls(string.IsNullOrWhiteSpace(ServerName) ? null : ServerName.Trim(), HttpVersion, TlsVersion)
            : null;
        return new SimulationRequest(
            Url.Trim(),
            ClientIp.Trim(),
            string.IsNullOrWhiteSpace(Method) ? null : Method.Trim(),
            [.. Headers.Where(h => !string.IsNullOrWhiteSpace(h.Name)).Select(h => new SimulationHeader(h.Name.Trim(), h.Value))],
            Arrival,
            viaProxy && !string.IsNullOrWhiteSpace(PeerIp) ? PeerIp.Trim() : null,
            Tls: tls);
    }

    /// <summary>The verdict's tone, icon and headline.</summary>
    public static (Tone Tone, string Icon, string Title) Describe(SimulationOutcome outcome, string? upstreamName)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        string status = outcome.StatusCode is { } code ? $" {code}" : string.Empty;
        return outcome.Kind switch
        {
            SimulationOutcomeKind.ForwardToUpstream => (Tone.Success, Icons.Material.Outlined.ArrowCircleRight, upstreamName is null ? "Forwarded to its upstream" : $"Forwarded to {upstreamName}"),
            SimulationOutcomeKind.Redirect => (Tone.Info, Icons.Material.Outlined.TurnRight, $"Redirected{status}"),
            SimulationOutcomeKind.FixedAnswer => (outcome.StatusCode is >= 400 ? Tone.Warning : Tone.Info, Icons.Material.Outlined.Reply, $"Answered{status} by the gate"),
            SimulationOutcomeKind.MisdirectedRequest => (Tone.Warning, Icons.Material.Outlined.WrongLocation, "421 Misdirected Request"),
            SimulationOutcomeKind.Drop => (Tone.Error, Icons.Material.Outlined.Block, "Dropped without an answer"),
            SimulationOutcomeKind.AcmeChallengeAnswer => (Tone.Info, Icons.Material.Outlined.VerifiedUser, "Answered an ACME challenge"),
            SimulationOutcomeKind.NoRoute => (Tone.Warning, Icons.Material.Outlined.SearchOff, $"No route for the path{status}"),
            SimulationOutcomeKind.DefaultHost => (Tone.Warning, Icons.Material.Outlined.HelpOutline, $"No host for this name{status}"),
            SimulationOutcomeKind.HandshakeRejected => (Tone.Error, Icons.Material.Outlined.LockOpen, "TLS handshake refused"),
            SimulationOutcomeKind.ConnectionRejected => (Tone.Error, Icons.Material.Outlined.LinkOff, "Connection refused by the listener"),
            SimulationOutcomeKind.RejectedByServer => (Tone.Error, Icons.Material.Outlined.ReportGmailerrorred, $"Rejected by the server{status}"),
            _ => (Tone.Default, Icons.Material.Outlined.Science, outcome.Kind.ToString()),
        };
    }

    /// <summary>A trace step's name.</summary>
    public static string StepTitle(SimulationStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        string policy = step.PolicyIndex is { } index ? $" {index + 1}" : string.Empty;
        return step.Kind switch
        {
            SimulationStepKind.Connection => "Connection",
            SimulationStepKind.TlsHandshake => "TLS handshake",
            SimulationStepKind.Request => "Request",
            SimulationStepKind.ClientAddress => "Client address",
            SimulationStepKind.AcmeChallenge => "ACME challenge",
            SimulationStepKind.HostMatch => "Host match",
            SimulationStepKind.TlsPolicy => "TLS policy",
            SimulationStepKind.ServerHeader => "Server header",
            SimulationStepKind.Hsts => "HSTS",
            SimulationStepKind.HostPolicy => $"Host policy{policy}",
            SimulationStepKind.HttpsRedirect => "HTTPS redirect",
            SimulationStepKind.FixedResponse => "Fixed answer",
            SimulationStepKind.RouteMatch => "Route match",
            SimulationStepKind.RoutePolicy => $"Route policy{policy}",
            SimulationStepKind.Upstream => "Upstream",
            _ => step.Kind.ToString(),
        };
    }

    /// <summary>A step's verdict as tone and icon; the step that decided takes the outcome's tone.</summary>
    public static (Tone Tone, string Icon, string Word) Verdict(SimulationStepVerdict verdict, Tone outcome) => verdict switch
    {
        SimulationStepVerdict.Passed => (Tone.Success, Icons.Material.Rounded.CheckCircle, "Passed"),
        SimulationStepVerdict.Stopped => (outcome == Tone.Success ? Tone.Primary : outcome, Icons.Material.Rounded.Flag, "Decided"),
        _ => (Tone.Default, Icons.Material.Rounded.RemoveCircleOutline, "Skipped"),
    };
}

/// <summary>A request header in the simulator's form.</summary>
public sealed class HeaderEntry
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}
