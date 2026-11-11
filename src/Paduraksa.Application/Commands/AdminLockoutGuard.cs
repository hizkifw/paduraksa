using System.Net;
using System.Security.Authentication;

using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Application.Commands;

/// <summary>
/// The lock-out guard: puts the caller's own request through the real gate over the config as it is and as it would be, so a
/// command that arrives through the proxy can't cut its own caller off from the admin app without saying so (<c>force</c> is the
/// explicit override).
/// </summary>
internal static class AdminLockoutGuard
{
    /// <summary>
    /// Whether the caller's own request would still reach the admin app under <paramref name="candidate"/>. The
    /// current and the compiled candidate are run through the real gate; only a request that currently forwards to the admin route
    /// and would stop doing so is a lock-out, so a condition that already exists is not blamed on this change.
    /// </summary>
    internal static ConfigError? AdminAccessLost(
        ConfigDocument current,
        ConfigDocument candidate,
        RuntimeSnapshot before,
        RuntimeSnapshot after,
        AdminRequestFacts request,
        IReadOnlyList<ConfigWarning> warnings)
    {
        var admin = current.Hosts.FirstOrDefault(static h => h is { Kind: HostKind.Admin, Enabled: true });
        var arrival = ArrivalOf(before, request);
        if (admin is null || !ForwardsToAdmin(before, request, arrival))
        {
            return null;
        }

        var path = AdminHostPath(current, candidate, admin);
        if (!SameTrustedProxies(current.Settings.TrustedProxies, candidate.Settings.TrustedProxies))
        {
            return Lockout("/settings/trustedProxies",
                "changing the trusted proxies while the command arrives through the proxy");
        }

        var adminAfter = candidate.Hosts.FirstOrDefault(static h => h.Kind == HostKind.Admin);
        if (adminAfter is not null
            && CommandErrors.PathOf(candidate, "hosts", adminAfter.Id.ToString()) is { } afterPath
            && warnings.Any(w =>
                w.Code == ConfigWarningCodes.CertificateSpecificNoCoverage && IsAtOrBelow(w.Path, afterPath)))
        {
            return Lockout(path, "the admin host's certificate doesn't cover its names");
        }

        if (ForwardsToAdmin(after, request, arrival))
        {
            return null;
        }

        var simulation = GateSimulator.Run(after, ProbeOf(request, arrival));
        var why = simulation.Outcome.Action switch
        {
            GateOutcomeAction.Forward => "the host would forward somewhere other than the admin app",
            GateOutcomeAction.Respond =>
                $"the host would answer {simulation.Outcome.StatusCode} instead of forwarding to the admin app",
            _ => "the request would be rejected before reaching the admin app",
        };
        return Lockout(path, why);
    }

    /// <summary>
    /// How the caller's request reached the public listener, as far as the live config says. A host locked down to
    /// Cloudflare only ever answers a trusted peer, so a request that gets through it came the way Cloudflare does; without
    /// the lockdown the probe models a direct client, which a later lockdown would then refuse, and the guard says so.
    /// </summary>
    private static ProbeArrival ArrivalOf(RuntimeSnapshot before, AdminRequestFacts request)
    {
        if (before.Hosts.Find(request.Host) is not { OriginLockdown: not OriginLockdownMode.Off })
        {
            return ProbeArrival.Direct;
        }

        return before.TrustedProxies.Mode switch
        {
            TrustedProxyMode.CloudflareProxy => ProbeArrival.CloudflareProxy,
            TrustedProxyMode.CloudflareTunnel => ProbeArrival.CloudflareTunnel,
            _ => ProbeArrival.Direct,
        };
    }

    private static bool ForwardsToAdmin(RuntimeSnapshot snapshot, AdminRequestFacts request, ProbeArrival arrival)
    {
        var simulation = GateSimulator.Run(snapshot, ProbeOf(request, arrival));
        return simulation.Outcome.Action == GateOutcomeAction.Forward && simulation.Upstream is { Admin: true };
    }

    private static GateProbe ProbeOf(AdminRequestFacts request, ProbeArrival arrival) => new()
    {
        Arrival = arrival,
        Https = request.Https,
        Host = request.Host,
        Path = "/",
        Method = "GET",
        ClientAddress = request.ClientIp ?? IPAddress.None,
        HttpVersion = ProbeHttpVersion.Http11,
        TlsVersion = request.Https ? SslProtocols.Tls13 : SslProtocols.Tls12,
    };

    private static ConfigError Lockout(string path, string why) => new(
        path,
        ConfigErrorCodes.HostAdminLockout,
        $"The request that sent this command would no longer reach the admin app: {why}. "
        + "Repeat it with force=true if that is what you mean, or make it on the admin port.");

    /// <summary>The admin host's pointer in the candidate when it exists, otherwise where it was in the current document.</summary>
    private static string AdminHostPath(ConfigDocument current, ConfigDocument candidate, HostConfig admin) =>
        CommandErrors.PathOf(candidate, "hosts", admin.Id.ToString())
        ?? CommandErrors.PathOf(current, "hosts", admin.Id.ToString())
        ?? "/hosts";

    private static bool SameTrustedProxies(TrustedProxySettings left, TrustedProxySettings right) =>
        left.Mode == right.Mode
        && string.Equals(left.Header, right.Header, StringComparison.Ordinal)
        && left.UseTrueClientIp == right.UseTrueClientIp
        && left.ForwardedForFallback == right.ForwardedForFallback
        && left.TrustForwardedProto == right.TrustForwardedProto
        && (left.Proxies ?? []).SequenceEqual(right.Proxies ?? [], StringComparer.Ordinal);

    private static bool IsAtOrBelow(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal) &&
        (path.Length == prefix.Length || path[prefix.Length] == '/');
}
