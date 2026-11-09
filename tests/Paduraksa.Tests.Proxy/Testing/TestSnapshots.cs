using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Proxy.Testing;

/// <summary>
/// Compiles test configs and binds them to stand-in endpoints, without YARP. The spec helpers take readable labels and
/// turn them into <see cref="TestIds"/>.
/// </summary>
internal static class TestSnapshots
{
    /// <summary>Public HTTPS port 8443, so redirects show which port they use.</summary>
    public static SnapshotCompilerOptions Port8443 { get; } = new() { PublicHttpsPort = 8443 };

    public static RuntimeSnapshot Compile(ProxyConfigModel model, SnapshotCompilerOptions? options = null)
    {
        CompileResult result = SnapshotCompiler.Compile(model, options);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
        return result.Snapshot!;
    }

    /// <summary>Compiles and binds with one fake endpoint per slot, named after the YARP route ID.</summary>
    public static RuntimeSnapshot Bound(ProxyConfigModel model, long generation = 1, SnapshotCompilerOptions? options = null)
    {
        RuntimeSnapshot candidate = Compile(model, options);
        Endpoint[] endpoints = new Endpoint[candidate.RouteIdBySlot.Length];
        for (int i = 0; i < endpoints.Length; i++)
        {
            endpoints[i] = new Endpoint(static _ => Task.CompletedTask, EndpointMetadataCollection.Empty, candidate.RouteIdBySlot[i].ToString());
        }

        return candidate.Bind(generation, endpoints);
    }

    /// <summary>Destinations are labelled <c>{label}-d0</c>, <c>{label}-d1</c>, ...</summary>
    public static UpstreamSpec Upstream(string label, params string[] addresses)
    {
        DestinationSpec[] destinations = new DestinationSpec[addresses.Length];
        for (int i = 0; i < addresses.Length; i++)
        {
            destinations[i] = new DestinationSpec(TestIds.Destination($"{label}-d{i}"), addresses[i]);
        }

        return new UpstreamSpec { Id = TestIds.Upstream(label), Destinations = destinations };
    }

    public static HostSpec ProxyHost(string label, string name, params RouteSpec[] routes) =>
        new() { Id = TestIds.Host(label), Names = [name], Routes = routes };

    public static RouteSpec Route(string label, string upstream, string path = "/", RouteMatchKind match = RouteMatchKind.Prefix) =>
        new() { Id = TestIds.Route(label), Upstream = TestIds.Upstream(upstream), Path = path, Match = match };
}
