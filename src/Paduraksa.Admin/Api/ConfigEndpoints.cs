using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// The config: the whole current document, and its objects (hosts with their routes, upstreams, profiles, the global settings). Every
/// change goes through <see cref="ConfigService.ExecuteAsync"/> as an object command, so validation, compilation, certificate rules, audit
/// and publishing are the pipeline's. Reads carry the object's version as <c>ETag</c> (the latest revision for the whole config), and a change must send it back as
/// <c>If-Match</c>: without it 428, stale 412. <c>?dryRun=true</c> runs everything but commits nothing.
/// </summary>
internal static class ConfigEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/config", GetConfigAsync)
            .WithName("GetConfig").WithTags("Config").WithSummary("The current config document")
            .WithDescription("The whole config as the latest revision has it, versions included. The ETag is the latest revision's ID.")
            .Produces<CurrentConfig>()
            .RequirePermission(Permission.ConfigRead);

        RouteGroupBuilder hosts = api.MapGroup("/hosts").WithTags("Hosts");
        hosts.MapGet("/", ListHostsAsync).WithName("ListHosts").WithSummary("List hosts")
            .Produces<IReadOnlyList<HostConfig>>().RequirePermission(Permission.ConfigRead);
        hosts.MapGet("/{id:HostId}", GetHostAsync).WithName("GetHost").WithSummary("Get a host")
            .Produces<HostConfig>().ProducesApiProblems(404).RequirePermission(Permission.ConfigRead);
        hosts.MapPost("/", CreateHostAsync).WithName("CreateHost").WithSummary("Create a host")
            .WithDescription("The body is the host with a client-chosen ID; its version is ignored (a new object is version 0). 409 when the ID exists.")
            .Accepts<HostConfig>("application/json").Produces<ConfigCommandResult>(201).ProducesApiProblems(400, 409, 415, 422).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        hosts.MapPut("/{id:HostId}", ReplaceHostAsync).WithName("ReplaceHost").WithSummary("Replace a host")
            .WithDescription("Replaces the host and its routes. Needs If-Match with the version you read (428 without it, 412 when stale). The body's ID must be the path's.")
            .Accepts<HostConfig>("application/json").Produces<ConfigCommandResult>().ProducesApiProblems(400, 404, 412, 415, 422, 428).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        hosts.MapDelete("/{id:HostId}", DeleteHostAsync).WithName("DeleteHost").WithSummary("Delete a host and its routes")
            .Produces(204).Produces<ConfigCommandResult>().ProducesApiProblems(404, 412, 428).MayFailToPublish().RequirePermission(Permission.ConfigWrite);

        hosts.MapGet("/{hostId:HostId}/routes", ListRoutesAsync).WithName("ListRoutes").WithSummary("List a host's routes")
            .Produces<IReadOnlyList<RouteConfig>>().ProducesApiProblems(404).RequirePermission(Permission.ConfigRead);
        hosts.MapGet("/{hostId:HostId}/routes/{routeId:RouteId}", GetRouteAsync).WithName("GetRoute").WithSummary("Get a route")
            .Produces<RouteConfig>().ProducesApiProblems(404).RequirePermission(Permission.ConfigRead);
        hosts.MapPut("/{hostId:HostId}/routes/{routeId:RouteId}", PutRouteAsync).WithName("PutRoute").WithSummary("Create or replace a route of a host")
            .WithDescription("A route belongs to its host: this is a change of the host, so If-Match is the host's version. A new route goes last (201 with Location); error paths are relative to the host.")
            .Accepts<RouteConfig>("application/json").Produces<ConfigCommandResult>().ProducesApiProblems(400, 404, 412, 415, 422, 428).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        hosts.MapDelete("/{hostId:HostId}/routes/{routeId:RouteId}", DeleteRouteAsync).WithName("DeleteRoute").WithSummary("Delete a route of a host")
            .Produces(204).Produces<ConfigCommandResult>().ProducesApiProblems(404, 412, 428).MayFailToPublish().RequirePermission(Permission.ConfigWrite);

        RouteGroupBuilder upstreams = api.MapGroup("/upstreams").WithTags("Upstreams");
        upstreams.MapGet("/", ListUpstreamsAsync).WithName("ListUpstreams").WithSummary("List upstreams")
            .Produces<IReadOnlyList<UpstreamConfig>>().RequirePermission(Permission.ConfigRead);
        upstreams.MapGet("/{id:UpstreamId}", GetUpstreamAsync).WithName("GetUpstream").WithSummary("Get an upstream")
            .Produces<UpstreamConfig>().ProducesApiProblems(404).RequirePermission(Permission.ConfigRead);
        upstreams.MapPost("/", CreateUpstreamAsync).WithName("CreateUpstream").WithSummary("Create an upstream")
            .Accepts<UpstreamConfig>("application/json").Produces<ConfigCommandResult>(201).ProducesApiProblems(400, 409, 415, 422).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        upstreams.MapPut("/{id:UpstreamId}", ReplaceUpstreamAsync).WithName("ReplaceUpstream").WithSummary("Replace an upstream")
            .Accepts<UpstreamConfig>("application/json").Produces<ConfigCommandResult>().ProducesApiProblems(400, 404, 412, 415, 422, 428).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        upstreams.MapDelete("/{id:UpstreamId}", DeleteUpstreamAsync).WithName("DeleteUpstream").WithSummary("Delete an upstream")
            .WithDescription("Refused (422, relatedErrors) while a route still uses it.")
            .Produces(204).Produces<ConfigCommandResult>().ProducesApiProblems(404, 412, 422, 428).MayFailToPublish().RequirePermission(Permission.ConfigWrite);

        RouteGroupBuilder profiles = api.MapGroup("/profiles").WithTags("Profiles");
        profiles.MapGet("/", ListProfilesAsync).WithName("ListProfiles").WithSummary("List profiles")
            .Produces<IReadOnlyList<ProfileConfig>>().RequirePermission(Permission.ConfigRead);
        profiles.MapGet("/{id:ProfileId}", GetProfileAsync).WithName("GetProfile").WithSummary("Get a profile")
            .Produces<ProfileConfig>().ProducesApiProblems(404).RequirePermission(Permission.ConfigRead);
        profiles.MapPost("/", CreateProfileAsync).WithName("CreateProfile").WithSummary("Create a profile")
            .Accepts<ProfileConfig>("application/json").Produces<ConfigCommandResult>(201).ProducesApiProblems(400, 409, 415, 422).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        profiles.MapPut("/{id:ProfileId}", ReplaceProfileAsync).WithName("ReplaceProfile").WithSummary("Replace a profile")
            .Accepts<ProfileConfig>("application/json").Produces<ConfigCommandResult>().ProducesApiProblems(400, 404, 412, 415, 422, 428).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        profiles.MapDelete("/{id:ProfileId}", DeleteProfileAsync).WithName("DeleteProfile").WithSummary("Delete a profile")
            .WithDescription("Refused (422, relatedErrors) while a host still uses it.")
            .Produces(204).Produces<ConfigCommandResult>().ProducesApiProblems(404, 412, 422, 428).MayFailToPublish().RequirePermission(Permission.ConfigWrite);

        api.MapGet("/settings", GetSettingsAsync).WithName("GetSettings").WithTags("Settings").WithSummary("The global settings")
            .WithDescription("The ETag is the settings' version.")
            .Produces<GlobalSettings>().RequirePermission(Permission.ConfigRead);
        api.MapPut("/settings", PutSettingsAsync).WithName("PutSettings").WithTags("Settings").WithSummary("Replace the global settings")
            .WithDescription("Needs If-Match with the settings' version (428 without it, 412 when stale).")
            .Accepts<GlobalSettings>("application/json").Produces<ConfigCommandResult>().ProducesApiProblems(400, 412, 415, 422, 428).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
    }

    private static async Task<IResult> GetConfigAsync(HttpContext http, ConfigService config, CancellationToken ct)
    {
        CurrentConfig current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
        TagWithRevision(http, current);
        return EndpointSupport.Json(current, AdminJsonContext.Default.CurrentConfig);
    }

    private static async Task<IResult> ListHostsAsync(HttpContext http, ConfigService config, CancellationToken ct)
    {
        CurrentConfig current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
        TagWithRevision(http, current);
        return EndpointSupport.Json(current.Document.Hosts, AdminJsonContext.Default.IReadOnlyListHostConfig);
    }

    private static async Task<IResult> GetHostAsync(HttpContext http, HostId id, ConfigService config, CancellationToken ct)
    {
        HostConfig? host = (await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.Hosts.FirstOrDefault(h => h.Id == id);
        if (host is null)
        {
            return NoSuchObject("host");
        }

        http.Response.Headers.ETag = Preconditions.VersionTag(host.Version);
        return EndpointSupport.Json(host, AdminJsonContext.Default.HostConfig);
    }

    private static Task<IResult> CreateHostAsync(HttpContext http, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, config, MutationKind.Create, requireVersion: false, AdminJsonContext.Default.HostConfig,
            (host, _, _) => (host with { Version = 0 }, null), (host, dry, message) => new UpsertHostCommand(host) { DryRun = dry, Message = message },
            host => EndpointSupport.Path("hosts", host.Id), ct);

    private static Task<IResult> ReplaceHostAsync(HttpContext http, HostId id, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, config, MutationKind.Change, requireVersion: true, AdminJsonContext.Default.HostConfig,
            (host, version, _) => host.Id == id ? (host with { Version = version }, null) : (host, IdMismatch()),
            (host, dry, message) => new UpsertHostCommand(host) { DryRun = dry, Message = message }, null, ct);

    private static Task<IResult> DeleteHostAsync(HttpContext http, HostId id, ConfigService config, CancellationToken ct) =>
        DeleteAsync(http, config, version => new DeleteHostCommand(id, version), ct);

    private static async Task<IResult> ListRoutesAsync(HttpContext http, HostId hostId, ConfigService config, CancellationToken ct)
    {
        CurrentConfig current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
        if (current.Document.Hosts.FirstOrDefault(h => h.Id == hostId) is not { } host)
        {
            return NoSuchObject("host");
        }

        http.Response.Headers.ETag = Preconditions.VersionTag(host.Version);
        return EndpointSupport.Json(host.Routes, AdminJsonContext.Default.IReadOnlyListRouteConfig);
    }

    private static async Task<IResult> GetRouteAsync(HttpContext http, HostId hostId, RouteId routeId, ConfigService config, CancellationToken ct)
    {
        CurrentConfig current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
        if (current.Document.Hosts.FirstOrDefault(h => h.Id == hostId) is not { } host)
        {
            return NoSuchObject("host");
        }

        if (host.Routes.FirstOrDefault(r => r.Id == routeId) is not { } route)
        {
            return NoSuchObject("route");
        }

        http.Response.Headers.ETag = Preconditions.VersionTag(host.Version);
        return EndpointSupport.Json(route, AdminJsonContext.Default.RouteConfig);
    }

    private static async Task<IResult> PutRouteAsync(HttpContext http, HostId hostId, RouteId routeId, ConfigService config, CancellationToken ct)
    {
        if (!Preconditions.TryGetDryRun(http, out bool dryRun, out IResult? problem) || !Preconditions.TryGetVersion(http, out long version, out problem))
        {
            return problem!;
        }

        BodyResult<RouteConfig> body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.RouteConfig).ConfigureAwait(false);
        if (!body.Ok)
        {
            return body.Problem!;
        }

        if (body.Value!.Id != routeId)
        {
            return IdMismatch();
        }

        if ((await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.Hosts.FirstOrDefault(h => h.Id == hostId) is not { } host)
        {
            return NoSuchObject("host");
        }

        List<RouteConfig> routes = [.. host.Routes];
        int at = routes.FindIndex(r => r.Id == routeId);
        if (at >= 0)
        {
            routes[at] = body.Value;
        }
        else
        {
            routes.Add(body.Value);
        }

        ConfigCommand command = new UpsertHostCommand(host with { Routes = routes, Version = version }) { DryRun = dryRun, Message = EndpointSupport.MessageOf(http) };
        return ConfigCommandResponses.ToResult(
            http,
            await config.ExecuteAsync(command, EndpointSupport.OriginOf(http), ct).ConfigureAwait(false),
            MutationKind.Change,
            $"/api/v1/hosts/{hostId}/routes/{routeId}",
            created: at < 0);
    }

    private static async Task<IResult> DeleteRouteAsync(HttpContext http, HostId hostId, RouteId routeId, ConfigService config, CancellationToken ct)
    {
        if (!Preconditions.TryGetDryRun(http, out bool dryRun, out IResult? problem) || !Preconditions.TryGetVersion(http, out long version, out problem))
        {
            return problem!;
        }

        if ((await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.Hosts.FirstOrDefault(h => h.Id == hostId) is not { } host)
        {
            return NoSuchObject("host");
        }

        if (host.Routes.All(r => r.Id != routeId))
        {
            return NoSuchObject("route");
        }

        ConfigCommand command = new UpsertHostCommand(host with { Routes = [.. host.Routes.Where(r => r.Id != routeId)], Version = version }) { DryRun = dryRun, Message = EndpointSupport.MessageOf(http) };
        return ConfigCommandResponses.ToResult(http, await config.ExecuteAsync(command, EndpointSupport.OriginOf(http), ct).ConfigureAwait(false), MutationKind.Change, noContent: true);
    }

    private static async Task<IResult> ListUpstreamsAsync(HttpContext http, ConfigService config, CancellationToken ct)
    {
        CurrentConfig current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
        TagWithRevision(http, current);
        return EndpointSupport.Json(current.Document.Upstreams, AdminJsonContext.Default.IReadOnlyListUpstreamConfig);
    }

    private static async Task<IResult> GetUpstreamAsync(HttpContext http, UpstreamId id, ConfigService config, CancellationToken ct)
    {
        UpstreamConfig? upstream = (await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.Upstreams.FirstOrDefault(u => u.Id == id);
        if (upstream is null)
        {
            return NoSuchObject("upstream");
        }

        http.Response.Headers.ETag = Preconditions.VersionTag(upstream.Version);
        return EndpointSupport.Json(upstream, AdminJsonContext.Default.UpstreamConfig);
    }

    private static Task<IResult> CreateUpstreamAsync(HttpContext http, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, config, MutationKind.Create, requireVersion: false, AdminJsonContext.Default.UpstreamConfig,
            (upstream, _, _) => (upstream with { Version = 0 }, null), (upstream, dry, message) => new UpsertUpstreamCommand(upstream) { DryRun = dry, Message = message },
            upstream => EndpointSupport.Path("upstreams", upstream.Id), ct);

    private static Task<IResult> ReplaceUpstreamAsync(HttpContext http, UpstreamId id, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, config, MutationKind.Change, requireVersion: true, AdminJsonContext.Default.UpstreamConfig,
            (upstream, version, _) => upstream.Id == id ? (upstream with { Version = version }, null) : (upstream, IdMismatch()),
            (upstream, dry, message) => new UpsertUpstreamCommand(upstream) { DryRun = dry, Message = message }, null, ct);

    private static Task<IResult> DeleteUpstreamAsync(HttpContext http, UpstreamId id, ConfigService config, CancellationToken ct) =>
        DeleteAsync(http, config, version => new DeleteUpstreamCommand(id, version), ct);

    private static async Task<IResult> ListProfilesAsync(HttpContext http, ConfigService config, CancellationToken ct)
    {
        CurrentConfig current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
        TagWithRevision(http, current);
        return EndpointSupport.Json(current.Document.Profiles, AdminJsonContext.Default.IReadOnlyListProfileConfig);
    }

    private static async Task<IResult> GetProfileAsync(HttpContext http, ProfileId id, ConfigService config, CancellationToken ct)
    {
        ProfileConfig? profile = (await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null)
        {
            return NoSuchObject("profile");
        }

        http.Response.Headers.ETag = Preconditions.VersionTag(profile.Version);
        return EndpointSupport.Json(profile, AdminJsonContext.Default.ProfileConfig);
    }

    private static Task<IResult> CreateProfileAsync(HttpContext http, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, config, MutationKind.Create, requireVersion: false, AdminJsonContext.Default.ProfileConfig,
            (profile, _, _) => (profile with { Version = 0 }, null), (profile, dry, message) => new UpsertProfileCommand(profile) { DryRun = dry, Message = message },
            profile => EndpointSupport.Path("profiles", profile.Id), ct);

    private static Task<IResult> ReplaceProfileAsync(HttpContext http, ProfileId id, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, config, MutationKind.Change, requireVersion: true, AdminJsonContext.Default.ProfileConfig,
            (profile, version, _) => profile.Id == id ? (profile with { Version = version }, null) : (profile, IdMismatch()),
            (profile, dry, message) => new UpsertProfileCommand(profile) { DryRun = dry, Message = message }, null, ct);

    private static Task<IResult> DeleteProfileAsync(HttpContext http, ProfileId id, ConfigService config, CancellationToken ct) =>
        DeleteAsync(http, config, version => new DeleteProfileCommand(id, version), ct);

    private static async Task<IResult> GetSettingsAsync(HttpContext http, ConfigService config, CancellationToken ct)
    {
        GlobalSettings settings = (await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.Settings;
        http.Response.Headers.ETag = Preconditions.VersionTag(settings.Version);
        return EndpointSupport.Json(settings, AdminJsonContext.Default.GlobalSettings);
    }

    private static Task<IResult> PutSettingsAsync(HttpContext http, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, config, MutationKind.Change, requireVersion: true, AdminJsonContext.Default.GlobalSettings,
            (settings, version, _) => (settings with { Version = version }, null),
            (settings, dry, message) => new UpdateSettingsCommand(settings) { DryRun = dry, Message = message }, null, ct);

    /// <summary>
    /// The shape of every upsert: dry run and <c>If-Match</c> first (cheap, and a missing precondition is the caller's to fix before anything is
    /// read), then the body, then the command.
    /// </summary>
    private static async Task<IResult> MutateAsync<T>(
        HttpContext http,
        ConfigService config,
        MutationKind kind,
        bool requireVersion,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        Func<T, long, string?, (T Value, IResult? Problem)> prepare,
        Func<T, bool, string?, ConfigCommand> command,
        Func<T, string>? location,
        CancellationToken ct)
        where T : class
    {
        if (!Preconditions.TryGetDryRun(http, out bool dryRun, out IResult? problem))
        {
            return problem!;
        }

        long version = 0;
        if (requireVersion && !Preconditions.TryGetVersion(http, out version, out problem))
        {
            return problem!;
        }

        BodyResult<T> body = await ApiBody.ReadJsonAsync(http, typeInfo).ConfigureAwait(false);
        if (!body.Ok)
        {
            return body.Problem!;
        }

        string? message = EndpointSupport.MessageOf(http);
        (T value, IResult? rejected) = prepare(body.Value!, version, message);
        if (rejected is not null)
        {
            return rejected;
        }

        ConfigCommandResult result = await config.ExecuteAsync(command(value, dryRun, message), EndpointSupport.OriginOf(http), ct).ConfigureAwait(false);
        return ConfigCommandResponses.ToResult(http, result, kind, location?.Invoke(value));
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, ConfigService config, Func<long, ConfigCommand> command, CancellationToken ct)
    {
        if (!Preconditions.TryGetDryRun(http, out bool dryRun, out IResult? problem) || !Preconditions.TryGetVersion(http, out long version, out problem))
        {
            return problem!;
        }

        ConfigCommand delete = command(version) with { DryRun = dryRun, Message = EndpointSupport.MessageOf(http) };
        ConfigCommandResult result = await config.ExecuteAsync(delete, EndpointSupport.OriginOf(http), ct).ConfigureAwait(false);
        return ConfigCommandResponses.ToResult(http, result, MutationKind.Change, noContent: true);
    }

    private static void TagWithRevision(HttpContext http, CurrentConfig current)
    {
        if (current.Revision is { } revision)
        {
            http.Response.Headers.ETag = Preconditions.RevisionTag(revision.Id);
        }
    }

    private static IResult NoSuchObject(string what) => ApiProblems.Problem(StatusCodes.Status404NotFound, $"There is no such {what}.");

    private static IResult IdMismatch() => ApiProblems.Problem(StatusCodes.Status400BadRequest, "The ID in the body is not the one in the path.");
}
