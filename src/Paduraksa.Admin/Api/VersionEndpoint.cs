using System.Reflection;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Contracts.Api;

namespace Paduraksa.Admin.Api;

/// <summary>What is running, so a client (a script, the CLI, an agent) can tell which build it is talking to.</summary>
internal static class VersionEndpoint
{
    private static readonly VersionInfo Current = Read(typeof(VersionEndpoint).Assembly);

    public static void Map(RouteGroupBuilder api) =>
        api.MapGet("/version", () => EndpointSupport.Json(Current, AdminJsonContext.Default.VersionInfo))
            .WithName("GetVersion").WithTags("Server").WithSummary("The version and build commit")
            .WithDescription("The product version and the commit it was built from. The API is versioned by its path (v1); a change that breaks it would be a v2.")
            .Produces<VersionInfo>().RequireAuthenticated();

    /// <summary>Splits the assembly's informational version, <c>0.1.0+&lt;commit&gt;</c>: the SDK appends the commit when the build is given <c>SourceRevisionId</c>.</summary>
    internal static VersionInfo Read(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        var commit = plus >= 0 && plus + 1 < informational.Length ? informational[(plus + 1)..] : null;
        return new VersionInfo(plus >= 0 ? informational[..plus] : informational, "v1", commit);
    }
}
