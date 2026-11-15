using System.Reflection;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Contracts.Api;
using Paduraksa.Core.Hosting;

namespace Paduraksa.Admin.Api;

/// <summary>What is running, so a client (a script, the CLI, an agent) can tell which build it is talking to.</summary>
internal static class VersionEndpoint
{
    private static readonly VersionInfo Current = Read(typeof(VersionEndpoint).Assembly);

    public static void Map(RouteGroupBuilder api) =>
        api.MapGet("/version", () => EndpointSupport.Json(Current, AdminJsonContext.Default.VersionInfo))
            .WithName("GetVersion").WithTags("Server").WithSummary("The version and build commit")
            .WithDescription("The product version and the commit it was built from. The API is versioned by its path (v1); a breaking change would be v2.")
            .Produces<VersionInfo>().RequireAuthenticated();

    internal static VersionInfo Read(Assembly assembly)
    {
        var build = BuildInfo.Read(assembly);
        return new VersionInfo(build.Version, "v1", build.Commit);
    }
}
