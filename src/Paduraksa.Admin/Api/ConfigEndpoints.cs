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
            .WithDescription("The whole config as the latest revision has it, versions included. The ETag is the latest revision's ID. It includes access lists' users with their PBKDF2 password verifiers (never a password); see the access list endpoints.")
            .Produces<CurrentConfig>()
            .RequirePermission(Permission.ConfigRead);

        var hosts = api.MapGroup("/hosts").WithTags("Hosts");
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

        var upstreams = api.MapGroup("/upstreams").WithTags("Upstreams");
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

        var rateLimits = api.MapGroup("/rateLimitPolicies").WithTags("Rate limits");
        rateLimits.MapGet("/", ListRateLimitPoliciesAsync).WithName("ListRateLimitPolicies").WithSummary("List rate-limit policies")
            .Produces<IReadOnlyList<RateLimitPolicyConfig>>().RequirePermission(Permission.ConfigRead);
        rateLimits.MapGet("/{id:RateLimitPolicyId}", GetRateLimitPolicyAsync).WithName("GetRateLimitPolicy").WithSummary("Get a rate-limit policy")
            .Produces<RateLimitPolicyConfig>().ProducesApiProblems(404).RequirePermission(Permission.ConfigRead);
        rateLimits.MapPost("/", CreateRateLimitPolicyAsync).WithName("CreateRateLimitPolicy").WithSummary("Create a rate-limit policy")
            .Accepts<RateLimitPolicyConfig>("application/json").Produces<ConfigCommandResult>(201).ProducesApiProblems(400, 409, 415, 422).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        rateLimits.MapPut("/{id:RateLimitPolicyId}", ReplaceRateLimitPolicyAsync).WithName("ReplaceRateLimitPolicy").WithSummary("Replace a rate-limit policy")
            .Accepts<RateLimitPolicyConfig>("application/json").Produces<ConfigCommandResult>().ProducesApiProblems(400, 404, 412, 415, 422, 428).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        rateLimits.MapDelete("/{id:RateLimitPolicyId}", DeleteRateLimitPolicyAsync).WithName("DeleteRateLimitPolicy").WithSummary("Delete a rate-limit policy")
            .WithDescription("Refused (422, relatedErrors) while a host or route still names it.")
            .Produces(204).Produces<ConfigCommandResult>().ProducesApiProblems(404, 412, 422, 428).MayFailToPublish().RequirePermission(Permission.ConfigWrite);

        var accessLists = api.MapGroup("/accessLists").WithTags("Access lists");
        accessLists.MapGet("/", ListAccessListsAsync).WithName("ListAccessLists").WithSummary("List access lists")
            .WithDescription("Users carry 'credentialId', the vault's handle on their password. No read returns a password or a hash.")
            .Produces<IReadOnlyList<AccessListConfig>>().RequirePermission(Permission.ConfigRead);
        accessLists.MapGet("/{id:AccessListId}", GetAccessListAsync).WithName("GetAccessList").WithSummary("Get an access list")
            .Produces<AccessListConfig>().ProducesApiProblems(404).RequirePermission(Permission.ConfigRead);
        accessLists.MapPost("/", CreateAccessListAsync).WithName("CreateAccessList").WithSummary("Create an access list")
            .WithDescription("A user's plain 'password' goes to the vault before the list is validated or stored, and is never returned; the list keeps the user's 'credentialId'.")
            .Accepts<AccessListConfig>("application/json").Produces<ConfigCommandResult>(201).ProducesApiProblems(400, 409, 415, 422).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        accessLists.MapPut("/{id:AccessListId}", ReplaceAccessListAsync).WithName("ReplaceAccessList").WithSummary("Replace an access list")
            .WithDescription("A user sent without 'password' or 'credentialId' keeps the password the list has for that username. A password change commits a revision (the list is modified) that records no password.")
            .Accepts<AccessListConfig>("application/json").Produces<ConfigCommandResult>().ProducesApiProblems(400, 404, 412, 415, 422, 428).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        accessLists.MapDelete("/{id:AccessListId}", DeleteAccessListAsync).WithName("DeleteAccessList").WithSummary("Delete an access list")
            .WithDescription("Refused (422, relatedErrors) while a host, profile, global default or route still names it.")
            .Produces(204).Produces<ConfigCommandResult>().ProducesApiProblems(404, 412, 422, 428).MayFailToPublish().RequirePermission(Permission.ConfigWrite);

        var ruleSets = api.MapGroup("/ruleSets").WithTags("Rule sets");
        ruleSets.MapGet("/", ListRuleSetsAsync).WithName("ListRuleSets").WithSummary("List rule sets")
            .WithDescription("The document's own sets. The built-in presets are at /ruleSets/presets and are named in a setting like any other set.")
            .Produces<IReadOnlyList<RuleSetConfig>>().RequirePermission(Permission.ConfigRead);
        ruleSets.MapGet("/presets", ListRuleSetPresetsAsync).WithName("ListRuleSetPresets").WithSummary("List the built-in rule-set presets")
            .WithDescription("Read-only, refreshed with each release. Copy one into a set of your own to change it.")
            .Produces<IReadOnlyList<RuleSetConfig>>().RequirePermission(Permission.ConfigRead);
        ruleSets.MapGet("/{id:RuleSetId}", GetRuleSetAsync).WithName("GetRuleSet").WithSummary("Get a rule set")
            .Produces<RuleSetConfig>().ProducesApiProblems(404).RequirePermission(Permission.ConfigRead);
        ruleSets.MapPost("/", CreateRuleSetAsync).WithName("CreateRuleSet").WithSummary("Create a rule set")
            .WithDescription("Rules are tried in order and the first match decides. A regex that can't be built, or a rule that states no condition, is refused (422).")
            .Accepts<RuleSetConfig>("application/json").Produces<ConfigCommandResult>(201).ProducesApiProblems(400, 409, 415, 422).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        ruleSets.MapPut("/{id:RuleSetId}", ReplaceRuleSetAsync).WithName("ReplaceRuleSet").WithSummary("Replace a rule set")
            .Accepts<RuleSetConfig>("application/json").Produces<ConfigCommandResult>().ProducesApiProblems(400, 404, 412, 415, 422, 428).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        ruleSets.MapDelete("/{id:RuleSetId}", DeleteRuleSetAsync).WithName("DeleteRuleSet").WithSummary("Delete a rule set")
            .WithDescription("Refused (422, relatedErrors) while a host still resolves to it (named by the host, its profile or the global defaults) or a route names it.")
            .Produces(204).Produces<ConfigCommandResult>().ProducesApiProblems(404, 412, 422, 428).MayFailToPublish().RequirePermission(Permission.ConfigWrite);

        var profiles = api.MapGroup("/profiles").WithTags("Profiles");
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
        var current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
        TagWithRevision(http, current);
        return EndpointSupport.Json(current, AdminJsonContext.Default.CurrentConfig);
    }

    private static async Task<IResult> ListHostsAsync(HttpContext http, ConfigService config, CancellationToken ct)
    {
        var current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
        TagWithRevision(http, current);
        return EndpointSupport.Json(current.Document.Hosts, AdminJsonContext.Default.IReadOnlyListHostConfig);
    }

    private static async Task<IResult> GetHostAsync(HttpContext http, HostId id, ConfigService config, CancellationToken ct)
    {
        var host = (await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.Hosts.FirstOrDefault(h => h.Id == id);
        if (host is null)
        {
            return NoSuchObject("host");
        }

        http.Response.Headers.ETag = Preconditions.VersionTag(host.Version);
        return EndpointSupport.Json(host, AdminJsonContext.Default.HostConfig);
    }

    private static Task<IResult> CreateHostAsync(HttpContext http, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, caller, config, MutationKind.Create, requireVersion: false, AdminJsonContext.Default.HostConfig,
            (host, _, _) => (host with { Version = 0 }, null), (host, dry, message) => new UpsertHostCommand(host) { DryRun = dry, Message = message },
            host => EndpointSupport.Path("hosts", host.Id), ct);

    private static Task<IResult> ReplaceHostAsync(HttpContext http, HostId id, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, caller, config, MutationKind.Change, requireVersion: true, AdminJsonContext.Default.HostConfig,
            (host, version, _) => host.Id == id ? (host with { Version = version }, null) : (host, IdMismatch()),
            (host, dry, message) => new UpsertHostCommand(host) { DryRun = dry, Message = message }, null, ct);

    private static Task<IResult> DeleteHostAsync(HttpContext http, HostId id, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        DeleteAsync(http, caller, config, version => new DeleteHostCommand(id, version), ct);

    private static async Task<IResult> ListRoutesAsync(HttpContext http, HostId hostId, ConfigService config, CancellationToken ct)
    {
        var current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
        if (current.Document.Hosts.FirstOrDefault(h => h.Id == hostId) is not { } host)
        {
            return NoSuchObject("host");
        }

        http.Response.Headers.ETag = Preconditions.VersionTag(host.Version);
        return EndpointSupport.Json(host.Routes, AdminJsonContext.Default.IReadOnlyListRouteConfig);
    }

    private static async Task<IResult> GetRouteAsync(HttpContext http, HostId hostId, RouteId routeId, ConfigService config, CancellationToken ct)
    {
        var current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
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

    private static async Task<IResult> PutRouteAsync(HttpContext http, AdminCaller caller, HostId hostId, RouteId routeId, ConfigService config, CancellationToken ct)
    {
        if (!Preconditions.TryGetDryRun(http, out var dryRun, out var problem)
            || !Preconditions.TryGetForce(http, out var force, out problem)
            || !Preconditions.TryGetVersion(http, out var version, out problem))
        {
            return problem!;
        }

        var body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.RouteConfig).ConfigureAwait(false);
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
        var at = routes.FindIndex(r => r.Id == routeId);
        if (at >= 0)
        {
            routes[at] = body.Value;
        }
        else
        {
            routes.Add(body.Value);
        }

        ConfigCommand command = new UpsertHostCommand(host with { Routes = routes, Version = version }) { DryRun = dryRun, Message = EndpointSupport.MessageOf(http), Force = force };
        return ConfigCommandResponses.ToResult(
            http,
            await config.ExecuteAsync(command, caller.Origin, ct).ConfigureAwait(false),
            MutationKind.Change,
            $"/api/v1/hosts/{hostId}/routes/{routeId}",
            created: at < 0);
    }

    private static async Task<IResult> DeleteRouteAsync(HttpContext http, AdminCaller caller, HostId hostId, RouteId routeId, ConfigService config, CancellationToken ct)
    {
        if (!Preconditions.TryGetDryRun(http, out var dryRun, out var problem)
            || !Preconditions.TryGetForce(http, out var force, out problem)
            || !Preconditions.TryGetVersion(http, out var version, out problem))
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

        ConfigCommand command = new UpsertHostCommand(host with { Routes = [.. host.Routes.Where(r => r.Id != routeId)], Version = version }) { DryRun = dryRun, Message = EndpointSupport.MessageOf(http), Force = force };
        return ConfigCommandResponses.ToResult(http, await config.ExecuteAsync(command, caller.Origin, ct).ConfigureAwait(false), MutationKind.Change, noContent: true);
    }

    private static async Task<IResult> ListUpstreamsAsync(HttpContext http, ConfigService config, CancellationToken ct)
    {
        var current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
        TagWithRevision(http, current);
        return EndpointSupport.Json(current.Document.Upstreams, AdminJsonContext.Default.IReadOnlyListUpstreamConfig);
    }

    private static async Task<IResult> GetUpstreamAsync(HttpContext http, UpstreamId id, ConfigService config, CancellationToken ct)
    {
        var upstream = (await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.Upstreams.FirstOrDefault(u => u.Id == id);
        if (upstream is null)
        {
            return NoSuchObject("upstream");
        }

        http.Response.Headers.ETag = Preconditions.VersionTag(upstream.Version);
        return EndpointSupport.Json(upstream, AdminJsonContext.Default.UpstreamConfig);
    }

    private static Task<IResult> CreateUpstreamAsync(HttpContext http, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, caller, config, MutationKind.Create, requireVersion: false, AdminJsonContext.Default.UpstreamConfig,
            (upstream, _, _) => (upstream with { Version = 0 }, null), (upstream, dry, message) => new UpsertUpstreamCommand(upstream) { DryRun = dry, Message = message },
            upstream => EndpointSupport.Path("upstreams", upstream.Id), ct);

    private static Task<IResult> ReplaceUpstreamAsync(HttpContext http, UpstreamId id, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, caller, config, MutationKind.Change, requireVersion: true, AdminJsonContext.Default.UpstreamConfig,
            (upstream, version, _) => upstream.Id == id ? (upstream with { Version = version }, null) : (upstream, IdMismatch()),
            (upstream, dry, message) => new UpsertUpstreamCommand(upstream) { DryRun = dry, Message = message }, null, ct);

    private static Task<IResult> DeleteUpstreamAsync(HttpContext http, UpstreamId id, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        DeleteAsync(http, caller, config, version => new DeleteUpstreamCommand(id, version), ct);

    private static async Task<IResult> ListRateLimitPoliciesAsync(HttpContext http, ConfigService config, CancellationToken ct)
    {
        var current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
        TagWithRevision(http, current);
        return EndpointSupport.Json(current.Document.RateLimitPolicies, AdminJsonContext.Default.IReadOnlyListRateLimitPolicyConfig);
    }

    private static async Task<IResult> GetRateLimitPolicyAsync(HttpContext http, RateLimitPolicyId id, ConfigService config, CancellationToken ct)
    {
        var policy = (await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.RateLimitPolicies.FirstOrDefault(p => p.Id == id);
        if (policy is null)
        {
            return NoSuchObject("rate-limit policy");
        }

        http.Response.Headers.ETag = Preconditions.VersionTag(policy.Version);
        return EndpointSupport.Json(policy, AdminJsonContext.Default.RateLimitPolicyConfig);
    }

    private static Task<IResult> CreateRateLimitPolicyAsync(HttpContext http, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, caller, config, MutationKind.Create, requireVersion: false, AdminJsonContext.Default.RateLimitPolicyConfig,
            (policy, _, _) => (policy with { Version = 0 }, null), (policy, dry, message) => new UpsertRateLimitPolicyCommand(policy) { DryRun = dry, Message = message },
            policy => EndpointSupport.Path("rateLimitPolicies", policy.Id), ct);

    private static Task<IResult> ReplaceRateLimitPolicyAsync(HttpContext http, RateLimitPolicyId id, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, caller, config, MutationKind.Change, requireVersion: true, AdminJsonContext.Default.RateLimitPolicyConfig,
            (policy, version, _) => policy.Id == id ? (policy with { Version = version }, null) : (policy, IdMismatch()),
            (policy, dry, message) => new UpsertRateLimitPolicyCommand(policy) { DryRun = dry, Message = message }, null, ct);

    private static Task<IResult> DeleteRateLimitPolicyAsync(HttpContext http, RateLimitPolicyId id, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        DeleteAsync(http, caller, config, version => new DeleteRateLimitPolicyCommand(id, version), ct);

    private static async Task<IResult> ListAccessListsAsync(HttpContext http, ConfigService config, CancellationToken ct)
    {
        var current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
        TagWithRevision(http, current);
        return EndpointSupport.Json(current.Document.AccessLists, AdminJsonContext.Default.IReadOnlyListAccessListConfig);
    }

    private static async Task<IResult> GetAccessListAsync(HttpContext http, AccessListId id, ConfigService config, CancellationToken ct)
    {
        var list = (await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.AccessLists.FirstOrDefault(l => l.Id == id);
        if (list is null)
        {
            return NoSuchObject("access list");
        }

        http.Response.Headers.ETag = Preconditions.VersionTag(list.Version);
        return EndpointSupport.Json(list, AdminJsonContext.Default.AccessListConfig);
    }

    private static Task<IResult> CreateAccessListAsync(HttpContext http, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, caller, config, MutationKind.Create, requireVersion: false, AdminJsonContext.Default.AccessListConfig,
            (list, _, _) => (list with { Version = 0 }, null), (list, dry, message) => new UpsertAccessListCommand(list) { DryRun = dry, Message = message },
            list => EndpointSupport.Path("accessLists", list.Id), ct);

    private static Task<IResult> ReplaceAccessListAsync(HttpContext http, AccessListId id, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, caller, config, MutationKind.Change, requireVersion: true, AdminJsonContext.Default.AccessListConfig,
            (list, version, _) => list.Id == id ? (list with { Version = version }, null) : (list, IdMismatch()),
            (list, dry, message) => new UpsertAccessListCommand(list) { DryRun = dry, Message = message }, null, ct);

    private static Task<IResult> DeleteAccessListAsync(HttpContext http, AccessListId id, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        DeleteAsync(http, caller, config, version => new DeleteAccessListCommand(id, version), ct);

    private static async Task<IResult> ListRuleSetsAsync(HttpContext http, ConfigService config, CancellationToken ct)
    {
        var current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
        TagWithRevision(http, current);
        return EndpointSupport.Json(current.Document.RuleSets, AdminJsonContext.Default.IReadOnlyListRuleSetConfig);
    }

    private static IResult ListRuleSetPresetsAsync() =>
        EndpointSupport.Json(RuleSetPresets.All, AdminJsonContext.Default.IReadOnlyListRuleSetConfig);

    private static async Task<IResult> GetRuleSetAsync(HttpContext http, RuleSetId id, ConfigService config, CancellationToken ct)
    {
        if (RuleSetPresets.TryGet(id, out var preset))
        {
            return EndpointSupport.Json(preset, AdminJsonContext.Default.RuleSetConfig);
        }

        var set = (await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.RuleSets.FirstOrDefault(r => r.Id == id);
        if (set is null)
        {
            return NoSuchObject("rule set");
        }

        http.Response.Headers.ETag = Preconditions.VersionTag(set.Version);
        return EndpointSupport.Json(set, AdminJsonContext.Default.RuleSetConfig);
    }

    private static Task<IResult> CreateRuleSetAsync(HttpContext http, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, caller, config, MutationKind.Create, requireVersion: false, AdminJsonContext.Default.RuleSetConfig,
            (set, _, _) => (set with { Version = 0 }, null), (set, dry, message) => new UpsertRuleSetCommand(set) { DryRun = dry, Message = message },
            set => EndpointSupport.Path("ruleSets", set.Id), ct);

    private static Task<IResult> ReplaceRuleSetAsync(HttpContext http, RuleSetId id, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, caller, config, MutationKind.Change, requireVersion: true, AdminJsonContext.Default.RuleSetConfig,
            (set, version, _) => set.Id == id ? (set with { Version = version }, null) : (set, IdMismatch()),
            (set, dry, message) => new UpsertRuleSetCommand(set) { DryRun = dry, Message = message }, null, ct);

    private static Task<IResult> DeleteRuleSetAsync(HttpContext http, RuleSetId id, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        DeleteAsync(http, caller, config, version => new DeleteRuleSetCommand(id, version), ct);

    private static async Task<IResult> ListProfilesAsync(HttpContext http, ConfigService config, CancellationToken ct)
    {
        var current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
        TagWithRevision(http, current);
        return EndpointSupport.Json(current.Document.Profiles, AdminJsonContext.Default.IReadOnlyListProfileConfig);
    }

    private static async Task<IResult> GetProfileAsync(HttpContext http, ProfileId id, ConfigService config, CancellationToken ct)
    {
        var profile = (await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null)
        {
            return NoSuchObject("profile");
        }

        http.Response.Headers.ETag = Preconditions.VersionTag(profile.Version);
        return EndpointSupport.Json(profile, AdminJsonContext.Default.ProfileConfig);
    }

    private static Task<IResult> CreateProfileAsync(HttpContext http, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, caller, config, MutationKind.Create, requireVersion: false, AdminJsonContext.Default.ProfileConfig,
            (profile, _, _) => (profile with { Version = 0 }, null), (profile, dry, message) => new UpsertProfileCommand(profile) { DryRun = dry, Message = message },
            profile => EndpointSupport.Path("profiles", profile.Id), ct);

    private static Task<IResult> ReplaceProfileAsync(HttpContext http, ProfileId id, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, caller, config, MutationKind.Change, requireVersion: true, AdminJsonContext.Default.ProfileConfig,
            (profile, version, _) => profile.Id == id ? (profile with { Version = version }, null) : (profile, IdMismatch()),
            (profile, dry, message) => new UpsertProfileCommand(profile) { DryRun = dry, Message = message }, null, ct);

    private static Task<IResult> DeleteProfileAsync(HttpContext http, ProfileId id, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        DeleteAsync(http, caller, config, version => new DeleteProfileCommand(id, version), ct);

    private static async Task<IResult> GetSettingsAsync(HttpContext http, ConfigService config, CancellationToken ct)
    {
        var settings = (await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.Settings;
        http.Response.Headers.ETag = Preconditions.VersionTag(settings.Version);
        return EndpointSupport.Json(settings, AdminJsonContext.Default.GlobalSettings);
    }

    private static Task<IResult> PutSettingsAsync(HttpContext http, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, caller, config, MutationKind.Change, requireVersion: true, AdminJsonContext.Default.GlobalSettings,
            (settings, version, _) => (settings with { Version = version }, null),
            (settings, dry, message) => new UpdateSettingsCommand(settings) { DryRun = dry, Message = message }, null, ct);

    /// <summary>
    /// The shape of every upsert: dry run and <c>If-Match</c> first (cheap, and a missing precondition is the caller's to fix before anything is
    /// read), then the body, then the command.
    /// </summary>
    private static async Task<IResult> MutateAsync<T>(
        HttpContext http,
        AdminCaller caller,
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
        if (!Preconditions.TryGetDryRun(http, out var dryRun, out var problem)
            || !Preconditions.TryGetForce(http, out var force, out problem))
        {
            return problem!;
        }

        long version = 0;
        if (requireVersion && !Preconditions.TryGetVersion(http, out version, out problem))
        {
            return problem!;
        }

        var body = await ApiBody.ReadJsonAsync(http, typeInfo).ConfigureAwait(false);
        if (!body.Ok)
        {
            return body.Problem!;
        }

        var message = EndpointSupport.MessageOf(http);
        var (value, rejected) = prepare(body.Value!, version, message);
        if (rejected is not null)
        {
            return rejected;
        }

        var result = await config.ExecuteAsync(command(value, dryRun, message) with { Force = force }, caller.Origin, ct).ConfigureAwait(false);
        return ConfigCommandResponses.ToResult(http, result, kind, location?.Invoke(value));
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, AdminCaller caller, ConfigService config, Func<long, ConfigCommand> command, CancellationToken ct)
    {
        if (!Preconditions.TryGetDryRun(http, out var dryRun, out var problem)
            || !Preconditions.TryGetForce(http, out var force, out problem)
            || !Preconditions.TryGetVersion(http, out var version, out problem))
        {
            return problem!;
        }

        var delete = command(version) with { DryRun = dryRun, Message = EndpointSupport.MessageOf(http), Force = force };
        var result = await config.ExecuteAsync(delete, caller.Origin, ct).ConfigureAwait(false);
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
