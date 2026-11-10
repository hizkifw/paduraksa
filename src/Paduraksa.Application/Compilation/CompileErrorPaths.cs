using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;

namespace Paduraksa.Application.Compilation;

/// <summary>
/// Turns a <see cref="CompileErrorSource"/> into the JSON pointer of that object in the document <i>as given</i> (the
/// compiler reads its canonical form, but objects keep their IDs): <c>/hosts/{i}</c>, <c>/hosts/{i}/routes/{j}</c>,
/// <c>/upstreams/{k}</c>, <c>/upstreams/{k}/destinations/{m}</c>, <c>/settings</c>,
/// <c>/settings/trustedProxies</c>. A duplicated ID points at its last occurrence: the compiler reports the duplicate
/// at the later object, and errors can't tell occurrences apart (the validator names each). An object that can't be
/// found points at its parent, else at the whole document (<c>""</c>).
/// </summary>
internal sealed class CompileErrorPaths
{
    private readonly Dictionary<HostId, int> _hosts = [];
    private readonly Dictionary<(HostId, RouteId), (int Host, int Route)> _routes = [];
    private readonly Dictionary<UpstreamId, int> _upstreams = [];
    private readonly Dictionary<(UpstreamId, DestinationId), (int Upstream, int Destination)> _destinations = [];

    public CompileErrorPaths(ConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var hosts = document.Hosts ?? [];
        for (var i = 0; i < hosts.Count; i++)
        {
            if (hosts[i] is not { } host)
            {
                continue;
            }

            _hosts[host.Id] = i;
            var routes = host.Routes ?? [];
            for (var j = 0; j < routes.Count; j++)
            {
                if (routes[j] is { } route)
                {
                    _routes[(host.Id, route.Id)] = (i, j);
                }
            }
        }

        var upstreams = document.Upstreams ?? [];
        for (var k = 0; k < upstreams.Count; k++)
        {
            if (upstreams[k] is not { } upstream)
            {
                continue;
            }

            _upstreams[upstream.Id] = k;
            var destinations = upstream.Destinations ?? [];
            for (var m = 0; m < destinations.Count; m++)
            {
                if (destinations[m] is { } destination)
                {
                    _destinations[(upstream.Id, destination.Id)] = (k, m);
                }
            }
        }
    }

    public string Of(CompileErrorSource source) => source switch
    {
        DefaultsErrorSource => "/settings",
        TrustedProxiesErrorSource => "/settings/trustedProxies",
        HostErrorSource host => Host(host.HostId),
        RouteErrorSource route => _routes.TryGetValue((route.HostId, route.RouteId), out var at)
            ? $"/hosts/{at.Host}/routes/{at.Route}"
            : Host(route.HostId),
        UpstreamErrorSource upstream => Upstream(upstream.UpstreamId),
        DestinationErrorSource destination => _destinations.TryGetValue((destination.UpstreamId, destination.DestinationId), out var at)
            ? $"/upstreams/{at.Upstream}/destinations/{at.Destination}"
            : Upstream(destination.UpstreamId),
        _ => string.Empty,
    };

    private string Host(HostId id) => _hosts.TryGetValue(id, out var i) ? $"/hosts/{i}" : string.Empty;

    private string Upstream(UpstreamId id) => _upstreams.TryGetValue(id, out var k) ? $"/upstreams/{k}" : string.Empty;
}
