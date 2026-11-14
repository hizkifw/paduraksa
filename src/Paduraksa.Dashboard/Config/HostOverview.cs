using System.Globalization;

using Paduraksa.Application.Compilation;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>A semantic tone, as the theme's <c>pdk-tone-*</c> classes and MudBlazor's colours name them.</summary>
public enum Tone
{
    Default,
    Primary,
    Success,
    Info,
    Warning,
    Error,
}

/// <summary>How a host's names are served over HTTPS, in a word and a tone.</summary>
public sealed record CertificateState(string Label, Tone Tone, string Detail);

/// <summary>One line of where a host's requests go, as the hosts list shows it: a route to an upstream and its address, or a redirect.</summary>
/// <param name="Path">The route's path (<c>/api</c>, <c>= /exact</c>); null for a host without routes.</param>
/// <param name="Label">The upstream's name, the redirect target, the fixed status.</param>
/// <param name="Detail">The upstream's address (the first destination, and how many more), in monospace.</param>
/// <param name="Missing">The route's upstream isn't in the config.</param>
public sealed record HostDestination(string? Path, string Label, string? Detail, bool Missing = false);

/// <summary>One row of the hosts list.</summary>
/// <param name="Host">The host as stored.</param>
/// <param name="Kind">The kind, in words.</param>
/// <param name="Certificate">Its certificate state; null when the user may not read the inventory.</param>
/// <param name="Https">How it answers plain HTTP and HTTPS.</param>
/// <param name="Target">Where requests go: the routes and their upstreams, the redirect target or the fixed status.</param>
/// <param name="Health">The status dot.</param>
/// <param name="Problems">What the live config warns about this host.</param>
/// <param name="Destinations">Where requests go, line by line.</param>
/// <param name="InMaintenance">Maintenance mode resolves to on for the host (its own layer, its profile or the global defaults) and the host serves it:
/// an admin host never does.</param>
public sealed record HostRow(
    HostConfig Host, string Kind, CertificateState? Certificate, string Https, string Target, Tone Health, IReadOnlyList<string> Problems, IReadOnlyList<HostDestination> Destinations,
    bool InMaintenance = false)
{
    /// <summary>Whether the row matches a search: any name, tag, upstream or address, kind or target containing <paramref name="term"/>.</summary>
    public bool Matches(string? term) =>
        string.IsNullOrWhiteSpace(term)
        || Host.Names.Any(n => n.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase))
        || Host.Tags.Any(t => t.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase))
        || Kind.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase)
        || Target.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase)
        || Destinations.Any(d => d.Label.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase) || d.Detail?.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase) == true);
}

/// <summary>Builds the hosts list: per host its kind, certificate state, HTTPS behaviour, routes and a status dot.</summary>
public static class HostOverview
{
    /// <summary>A certificate that ends within this many days is shown as expiring.</summary>
    public const int ExpiringDays = 14;

    /// <summary>
    /// The rows of <paramref name="document"/>'s hosts. <paramref name="certificates"/> is null when the user may not read the inventory;
    /// <paramref name="liveWarnings"/> index the live document, which is <paramref name="document"/> unless a publish is still pending.
    /// </summary>
    public static IReadOnlyList<HostRow> Rows(
        ConfigDocument document, IReadOnlyList<CertificateSummary>? certificates, IReadOnlyList<ConfigWarning> liveWarnings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(liveWarnings);
        List<HostRow> rows = [];
        for (var i = 0; i < document.Hosts.Count; i++)
        {
            var host = document.Hosts[i];
            var prefix = $"/hosts/{i.ToString(CultureInfo.InvariantCulture)}";
            string[] problems =
            [
                .. liveWarnings
                    .Where(w => w.Path == prefix || w.Path.StartsWith(prefix + "/", StringComparison.Ordinal))
                    .Select(w => ReviewNotices.Sentence(w.Message)),
            ];
            var certificate = certificates is null ? null : CertificateOf(host, certificates, now);
            var health = !host.Enabled ? Tone.Default
                : problems.Length > 0 || certificate?.Tone is Tone.Warning or Tone.Error ? Tone.Warning
                : Tone.Success;
            var profile = host.ProfileId is { } profileId ? document.Profiles.FirstOrDefault(candidate => candidate.Id == profileId)?.Settings : null;
            var inMaintenance = host.Kind != HostKind.Admin && HostSettingsLayers.MergeAll(document.Settings.Defaults, profile, host.Settings).Maintenance?.Enabled == true;
            rows.Add(new HostRow(host, KindLabel(host.Kind), certificate, HttpsOf(host, document), TargetOf(host, document), health, problems, DestinationsOf(host, document), inMaintenance));
        }

        return [.. rows.OrderBy(r => r.Host.Names.Count > 0 ? r.Host.Names[0] : string.Empty, StringComparer.Ordinal)];
    }

    public static string KindLabel(HostKind kind) => kind switch
    {
        HostKind.Proxy => "Proxy",
        HostKind.Redirect => "Redirect",
        HostKind.Respond => "Fixed answer",
        HostKind.Admin => "Admin",
        _ => kind.ToString(),
    };

    /// <summary>The routes (and their upstreams) that send requests to <paramref name="upstream"/>, by host.</summary>
    public static IReadOnlyList<(HostConfig Host, RouteConfig Route)> UsesOf(ConfigDocument document, UpstreamId upstream)
    {
        ArgumentNullException.ThrowIfNull(document);
        return
        [
            .. document.Hosts
                .Where(h => h.Kind == HostKind.Proxy)
                .SelectMany(h => h.Routes.Where(r => r.UpstreamId == upstream).Select(r => (h, r))),
        ];
    }

    /// <summary>Whether <paramref name="name"/> is covered by a certificate name (exact, or a wildcard of its parent).</summary>
    public static bool Covers(string certificateName, string name)
    {
        ArgumentNullException.ThrowIfNull(certificateName);
        ArgumentNullException.ThrowIfNull(name);
        if (string.Equals(certificateName, name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return certificateName.StartsWith("*.", StringComparison.Ordinal)
            && dot > 0
            && !name.StartsWith("*.", StringComparison.Ordinal)
            && string.Equals(certificateName[1..], name[dot..], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a certificate can be served now: active, with a chain, inside its validity period.</summary>
    public static bool IsUsable(CertificateSummary certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.Status == CertificateStatus.Active && certificate.Validity == CertificateValidity.Valid && certificate.Source != CertificateSource.LocalCaRoot;
    }

    /// <summary>A certificate's name in a picker: its label, else its first name, with when it ends.</summary>
    public static string CertificateTitle(CertificateSummary certificate, DisplayFormat? format = null) =>
        $"{CertificateName(certificate)} · until {(format ?? DisplayFormat.Invariant).Date(certificate.NotAfter)}";

    /// <summary>A certificate's name: its label, else its first name, else its subject.</summary>
    public static string CertificateName(CertificateSummary certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.Label ?? (certificate.Names.Count > 0 ? certificate.Names[0] : certificate.Subject);
    }

    private static CertificateState CertificateOf(HostConfig host, IReadOnlyList<CertificateSummary> certificates, DateTimeOffset now)
    {
        switch (host.Certificate.Mode)
        {
            case CertificateBindingMode.None:
                return new CertificateState("No HTTPS", Tone.Default, "The host is bound to no certificate: it answers plain HTTP only.");
            case CertificateBindingMode.Specific:
                var bound = certificates.FirstOrDefault(c => c.Id == host.Certificate.CertificateId);
                if (bound is null)
                {
                    return new CertificateState("Missing", Tone.Error, "The certificate this host is bound to isn't in the inventory; the default one is served.");
                }

                return Judge(bound, now, $"Bound to {CertificateTitle(bound)}.");
            default:
                if (host.Kind == HostKind.Admin && host.Names.Count == 0)
                {
                    return new CertificateState("Admin", Tone.Info, "Served with the admin certificate.");
                }

                CertificateSummary[] usable = [.. certificates.Where(IsUsable)];
                CertificateSummary?[] covering =
                [
                    .. host.Names.Select(name => usable.Where(c => c.Names.Any(n => Covers(n, name))).OrderByDescending(c => c.NotAfter).FirstOrDefault()),
                ];
                var covered = covering.Count(c => c is not null);
                if (covered == 0)
                {
                    return new CertificateState("Default", Tone.Warning, "No certificate covers these names: browsers see the default certificate and warn.");
                }

                if (covered < host.Names.Count)
                {
                    return new CertificateState(
                        $"{covered} of {host.Names.Count} covered",
                        Tone.Warning,
                        $"No certificate covers {string.Join(", ", host.Names.Where((_, i) => covering[i] is null))}.");
                }

                var soonest = covering.OrderBy(c => c!.NotAfter).First()!;
                return Judge(soonest, now, $"Covered by {CertificateTitle(soonest)}.");
        }
    }

    private static CertificateState Judge(CertificateSummary certificate, DateTimeOffset now, string detail)
    {
        if (certificate.Status == CertificateStatus.Pending)
        {
            return new CertificateState("Pending", Tone.Info, "The certificate is being issued. " + detail);
        }

        if (certificate.Status == CertificateStatus.Failed)
        {
            return new CertificateState("Failed", Tone.Error, (certificate.StatusMessage ?? "Issuance failed.") + " " + detail);
        }

        var left = certificate.NotAfter - now;
        if (certificate.Validity == CertificateValidity.Expired || left <= TimeSpan.Zero)
        {
            return new CertificateState("Expired", Tone.Error, detail);
        }

        if (certificate.Validity == CertificateValidity.NotYetValid)
        {
            return new CertificateState("Not yet valid", Tone.Warning, detail);
        }

        if (left <= TimeSpan.FromDays(ExpiringDays))
        {
            var days = Math.Max(1, (int)left.TotalDays);
            return new CertificateState(days == 1 ? "Expires tomorrow" : $"Expires in {days} days", Tone.Warning, detail);
        }

        return new CertificateState("Valid", Tone.Success, detail);
    }

    private static string HttpsOf(HostConfig host, ConfigDocument document)
    {
        if (host.Kind == HostKind.Admin)
        {
            return "HTTPS only";
        }

        if (host.Certificate.Mode == CertificateBindingMode.None)
        {
            return "HTTP only";
        }

        var profile = host.ProfileId is { } id ? document.Profiles.FirstOrDefault(p => p.Id == id)?.Settings : null;
        var redirect = HostSettingsLayers.MergeAll(HostSettingsLayers.BuiltIn, document.Settings.Defaults, profile, host.Settings).ForceHttps == true;
        return redirect ? "HTTPS, redirects HTTP" : "HTTP and HTTPS";
    }

    /// <summary>Where <paramref name="host"/>'s requests go, a line per route in evaluation order (its path, the
    /// upstream, the upstream's address).</summary>
    public static IReadOnlyList<HostDestination> DestinationsOf(HostConfig host, ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(document);
        switch (host.Kind)
        {
            case HostKind.Proxy:
                if (host.Routes.Count == 0)
                {
                    return [new HostDestination(null, "No routes", null, Missing: true)];
                }

                return
                [
                    .. host.Routes
                        .Select((route, listed) => (Route: route, Key: RouteOrderKey.For(route.Match, route.Path, route.Order, listed)))
                        .OrderBy(entry => entry.Key)
                        .Select(entry =>
                        {
                            var route = entry.Route;
                            var path = route.Match == RouteMatchKind.Exact ? $"= {route.Path}" : route.Path;
                            return document.Upstreams.FirstOrDefault(u => u.Id == route.UpstreamId) is { } upstream
                                ? new HostDestination(path, upstream.Name, AddressOf(upstream))
                                : new HostDestination(path, "missing upstream", null, Missing: true);
                        }),
                ];
            case HostKind.Redirect:
                return host.Redirect is { } redirect
                    ? [new HostDestination(null, $"Redirect {redirect.StatusCode.ToString(CultureInfo.InvariantCulture)}", redirect.Target)]
                    : [new HostDestination(null, "Redirect", null)];
            case HostKind.Respond:
                return [new HostDestination(null, host.Respond is { } respond ? $"Answers {respond.StatusCode.ToString(CultureInfo.InvariantCulture)}" : "Fixed answer", host.Respond?.ContentType)];
            default:
                return [new HostDestination(null, "This admin", "through the proxy")];
        }
    }

    /// <summary>An upstream's address in a word: the first destination, and how many more.</summary>
    public static string AddressOf(UpstreamConfig upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        return upstream.Destinations.Count switch
        {
            0 => "no destinations",
            1 => upstream.Destinations[0].Address,
            _ => $"{upstream.Destinations[0].Address} +{(upstream.Destinations.Count - 1).ToString(CultureInfo.InvariantCulture)}",
        };
    }

    private static string TargetOf(HostConfig host, ConfigDocument document)
    {
        switch (host.Kind)
        {
            case HostKind.Proxy:
                string[] upstreams =
                [
                    .. host.Routes.Select(r => document.Upstreams.FirstOrDefault(u => u.Id == r.UpstreamId)?.Name ?? "missing upstream").Distinct(StringComparer.Ordinal),
                ];
                return host.Routes.Count switch
                {
                    0 => "No routes",
                    1 when host.Routes[0].Path == "/" => $"→ {upstreams[0]}",
                    1 => $"{host.Routes[0].Path} → {upstreams[0]}",
                    _ => $"{host.Routes.Count} routes → {string.Join(", ", upstreams)}",
                };
            case HostKind.Redirect:
                return host.Redirect is { } redirect ? $"→ {redirect.Target} ({redirect.StatusCode.ToString(CultureInfo.InvariantCulture)})" : "Redirect";
            case HostKind.Respond:
                return host.Respond is { } respond ? $"Answers {respond.StatusCode.ToString(CultureInfo.InvariantCulture)}" : "Fixed answer";
            default:
                return "This admin, through the proxy";
        }
    }
}
