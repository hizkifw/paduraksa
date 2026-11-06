using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Tests.Proxy.Testing;

/// <summary>Compiles test configs and binds them to stand-in endpoints, without YARP.</summary>
internal static class TestSnapshots
{
    public static RuntimeSnapshot Compile(ProxyConfigModel model)
    {
        CompileResult result = SnapshotCompiler.Compile(model);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        return result.Snapshot!;
    }

    /// <summary>Compiles and binds with one fake endpoint per slot, named after the YARP route ID.</summary>
    public static RuntimeSnapshot Bound(ProxyConfigModel model, long generation = 1)
    {
        RuntimeSnapshot candidate = Compile(model);
        Endpoint[] endpoints = new Endpoint[candidate.RouteIdBySlot.Length];
        for (int i = 0; i < endpoints.Length; i++)
        {
            endpoints[i] = new Endpoint(static _ => Task.CompletedTask, EndpointMetadataCollection.Empty, candidate.RouteIdBySlot[i]);
        }

        return candidate.Bind(generation, endpoints);
    }

    public static UpstreamSpec Upstream(string key, params string[] addresses)
    {
        DestinationSpec[] destinations = new DestinationSpec[addresses.Length];
        for (int i = 0; i < addresses.Length; i++)
        {
            destinations[i] = new DestinationSpec($"{key}-d{i}", addresses[i]);
        }

        return new UpstreamSpec { Key = key, Destinations = destinations };
    }

    public static HostSpec ProxyHost(string key, string name, params RouteSpec[] routes) =>
        new() { Key = key, Names = [name], Routes = routes };

    public static RouteSpec Route(string key, string upstream, string path = "/", RouteMatchKind match = RouteMatchKind.Prefix) =>
        new() { Key = key, Upstream = upstream, Path = path, Match = match };
}
