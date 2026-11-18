using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.TypedIds;

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
            .WithDescription("The whole config as the latest revision has it, with versions. The ETag is the latest revision's ID. Access list users appear with their credential IDs (the vault's handle on their passwords). No read returns a password, a hash or a verifier; see the access list endpoints.")
            .Produces<CurrentConfig>()
            .RequirePermission(Permission.ConfigRead);

        var hosts = api.MapGroup("/hosts").WithTags("Hosts");
        hosts.MapGet("/", ListHostsAsync).WithName("ListHosts").WithSummary("List hosts")
            .WithPage().Produces<Page<HostConfig>>().ProducesApiProblems(400).RequirePermission(Permission.ConfigRead);
        hosts.MapGet("/{id:HostId}", GetHostAsync).WithName("GetHost").WithSummary("Get a host")
            .Produces<HostConfig>().ProducesApiProblems(404).RequirePermission(Permission.ConfigRead);
        hosts.MapPost("/", CreateHostAsync).WithName("CreateHost").WithSummary("Create a host")
            .WithDescription("The body is the host with a client-chosen ID. Its version is ignored (a new object is version 0). Returns 409 if the ID exists.")
            .Accepts<HostConfig>("application/json").Produces<ConfigCommandResult>(201).ProducesApiProblems(400, 409, 415, 422).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        hosts.MapPut("/{id:HostId}", ReplaceHostAsync).WithName("ReplaceHost").WithSummary("Replace a host")
            .WithDescription("Replaces the host and its routes. Requires If-Match with the version you read (428 without it, 412 if stale). The body's ID must match the path's.")
            .Accepts<HostConfig>("application/json").Produces<ConfigCommandResult>().ProducesApiProblems(400, 404, 412, 415, 422, 428).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        hosts.MapDelete("/{id:HostId}", DeleteHostAsync).WithName("DeleteHost").WithSummary("Delete a host and its routes")
            .Produces(204).Produces<ConfigCommandResult>().ProducesApiProblems(400, 404, 412, 428).MayFailToPublish().RequirePermission(Permission.ConfigWrite);

        hosts.MapGet("/{hostId:HostId}/routes", ListRoutesAsync).WithName("ListRoutes").WithSummary("List a host's routes")
            .WithPage().Produces<Page<RouteConfig>>().ProducesApiProblems(400, 404).RequirePermission(Permission.ConfigRead);
        hosts.MapGet("/{hostId:HostId}/routes/{routeId:RouteId}", GetRouteAsync).WithName("GetRoute").WithSummary("Get a route")
            .Produces<RouteConfig>().ProducesApiProblems(404).RequirePermission(Permission.ConfigRead);
        hosts.MapPut("/{hostId:HostId}/routes/{routeId:RouteId}", PutRouteAsync).WithName("PutRoute").WithSummary("Create or replace a route of a host")
            .WithDescription("A route belongs to its host, so changing a route changes the host and If-Match takes the host's version. A new route goes last (201 with Location). Error paths are relative to the host.")
            .Accepts<RouteConfig>("application/json").Produces<ConfigCommandResult>().Produces<ConfigCommandResult>(201).ProducesApiProblems(400, 404, 412, 415, 422, 428).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        hosts.MapDelete("/{hostId:HostId}/routes/{routeId:RouteId}", DeleteRouteAsync).WithName("DeleteRoute").WithSummary("Delete a route of a host")
            .Produces(204).Produces<ConfigCommandResult>().ProducesApiProblems(400, 404, 412, 428).MayFailToPublish().RequirePermission(Permission.ConfigWrite);

        MapResource(api, new Resource<UpstreamConfig, UpstreamId>(
            "upstreams", "Upstreams", "upstream", "upstreams", d => d.Upstreams, u => u.Id, u => u.Version, (u, version) => u with { Version = version },
            AdminJsonContext.Default.UpstreamConfig, AdminJsonContext.Default.PageUpstreamConfig,
            upstream => new UpsertUpstreamCommand(upstream), (id, version) => new DeleteUpstreamCommand(id, version)),
            deleteDescription: "Refused (422, relatedErrors) while a route still uses it.");

        MapResource(api, new Resource<RateLimitPolicyConfig, RateLimitPolicyId>(
            "rate-limit-policies", "Rate limits", "rate-limit policy", "rate-limit policies", d => d.RateLimitPolicies, p => p.Id, p => p.Version, (p, version) => p with { Version = version },
            AdminJsonContext.Default.RateLimitPolicyConfig, AdminJsonContext.Default.PageRateLimitPolicyConfig,
            policy => new UpsertRateLimitPolicyCommand(policy), (id, version) => new DeleteRateLimitPolicyCommand(id, version)),
            deleteDescription: "Refused (422, relatedErrors) while a host or route still names it.");

        MapResource(api, new Resource<AccessListConfig, AccessListId>(
            "access-lists", "Access lists", "access list", "access lists", d => d.AccessLists, l => l.Id, l => l.Version, (l, version) => l with { Version = version },
            AdminJsonContext.Default.AccessListConfig, AdminJsonContext.Default.PageAccessListConfig,
            list => new UpsertAccessListCommand(list), (id, version) => new DeleteAccessListCommand(id, version)),
            listDescription: "Users carry 'credentialId', the vault's handle on their password. No read returns a password or a hash.",
            createDescription: "The vault receives a user's plain 'password' before the list is validated or stored, and the API never returns it. The list keeps the user's 'credentialId'.",
            replaceDescription: "A user sent without 'password' or 'credentialId' keeps the password the list has for that username. A password change commits a revision that modifies the list and records no password.",
            deleteDescription: "Refused (422, relatedErrors) while a host, profile, global default or route still names it.");

        MapResource(api, new Resource<RuleSetConfig, RuleSetId>(
            "rule-sets", "Rule sets", "rule set", "rule sets", d => d.RuleSets, r => r.Id, r => r.Version, (r, version) => r with { Version = version },
            AdminJsonContext.Default.RuleSetConfig, AdminJsonContext.Default.PageRuleSetConfig,
            set => new UpsertRuleSetCommand(set), (id, version) => new DeleteRuleSetCommand(id, version))
        {
            BuiltIn = id => RuleSetPresets.TryGet(id, out var preset) ? preset : null,
            AfterList = static group => group.MapGet("/presets", ListRuleSetPresetsAsync).WithName("ListRuleSetPresets").WithSummary("List the built-in rule-set presets")
                .WithDescription("Read-only; each release refreshes them. To change one, copy it into a set of your own.")
                .WithPage().Produces<Page<RuleSetConfig>>().ProducesApiProblems(400).RequirePermission(Permission.ConfigRead),
        },
            listDescription: "The document's own sets. The built-in presets are at /rule-sets/presets, and a setting names a preset like any other set.",
            createDescription: "Rules are tried in order and the first match decides. If a regex can't be built or a rule states no condition, the request is refused (422).",
            deleteDescription: "Refused (422, relatedErrors) while a host still resolves to it (named by the host, its profile or the global defaults) or a route names it.");

        MapResource(api, new Resource<AuthProviderConfig, AuthProviderId>(
            "auth-providers", "Auth providers", "auth provider", "auth providers", d => d.AuthProviders, p => p.Id, p => p.Version, (p, version) => p with { Version = version },
            AdminJsonContext.Default.AuthProviderConfig, AdminJsonContext.Default.PageAuthProviderConfig,
            provider => new UpsertAuthProviderCommand(provider), (id, version) => new DeleteAuthProviderCommand(id, version)),
            createDescription: "A forward-auth endpoint (authentik, Authelia, oauth2-proxy or any other). The proxy checks every request to a host or route that names the provider against it first. "
                + "The preset (authentik, authelia, oauth2Proxy) fills in the endpoint's path when the URL is a bare address, the identity headers its answer carries, whether a remembered allow covers one path or the whole host, "
                + "and oauth2-proxy's sign-in redirect; a field that is set wins. The API refuses (422) a URL with credentials, a header the proxy owns, and a sign-in URL that isn't a path on the host or an absolute http(s) URL.",
            replaceDescription: "If the change affects what the endpoint is asked or how its answer is read, the provider starts with an empty decision cache. A rename or new notes keep the cache.",
            deleteDescription: "Refused (422, relatedErrors) while a host still resolves to it (named by the host, its profile or the global defaults) or a route names it.");

        MapResource(api, new Resource<ProfileConfig, ProfileId>(
            "profiles", "Profiles", "profile", "profiles", d => d.Profiles, p => p.Id, p => p.Version, (p, version) => p with { Version = version },
            AdminJsonContext.Default.ProfileConfig, AdminJsonContext.Default.PageProfileConfig,
            profile => new UpsertProfileCommand(profile), (id, version) => new DeleteProfileCommand(id, version)),
            deleteDescription: "Refused (422, relatedErrors) while a host still uses it.");

        api.MapGet("/settings", GetSettingsAsync).WithName("GetSettings").WithTags("Settings").WithSummary("The global settings")
            .WithDescription("The ETag is the settings' version.")
            .Produces<GlobalSettings>().RequirePermission(Permission.ConfigRead);
        api.MapPut("/settings", PutSettingsAsync).WithName("PutSettings").WithTags("Settings").WithSummary("Replace the global settings")
            .WithDescription("Requires If-Match with the settings' version (428 without it, 412 if stale).")
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
        return Pagination.Respond(http, current.Document.Hosts, h => h.Id, AdminJsonContext.Default.PageHostConfig);
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
            (host, _) => (host with { Version = 0 }, null), host => new UpsertHostCommand(host),
            host => EndpointSupport.Path("hosts", host.Id), ct);

    private static Task<IResult> ReplaceHostAsync(HttpContext http, HostId id, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, caller, config, MutationKind.Change, requireVersion: true, AdminJsonContext.Default.HostConfig,
            (host, version) => host.Id == id ? (host with { Version = version }, null) : (host, IdMismatch()),
            host => new UpsertHostCommand(host), null, ct);

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
        return Pagination.Respond(http, host.Routes, r => r.Id, AdminJsonContext.Default.PageRouteConfig);
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
        if (!Preconditions.TryGetFlags(http, out var dryRun, out var force, out var problem)
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
        if (!Preconditions.TryGetFlags(http, out var dryRun, out var force, out var problem)
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

    private static IResult ListRuleSetPresetsAsync(HttpContext http) =>
        Pagination.Respond(http, RuleSetPresets.All, r => r.Id, AdminJsonContext.Default.PageRuleSetConfig);

    /// <summary>What differs between the config collections that share the list, get, create, replace and delete endpoints.</summary>
    private sealed record Resource<T, TId>(
        string Path,
        string Tag,
        string Singular,
        string Plural,
        Func<ConfigDocument, IReadOnlyList<T>> Items,
        Func<T, TId> IdOf,
        Func<T, long> VersionOf,
        Func<T, long, T> WithVersion,
        JsonTypeInfo<T> Json,
        JsonTypeInfo<Page<T>> PageJson,
        Func<T, ConfigCommand> Upsert,
        Func<TId, long, ConfigCommand> Delete)
        where T : class
        where TId : struct, ITypedId<TId>
    {
        /// <summary>Objects that aren't in the document but answer a get (the rule-set presets).</summary>
        public Func<TId, T?>? BuiltIn { get; init; }

        /// <summary>Endpoints that must be described right after the list (the OpenAPI document keeps mapping order).</summary>
        public Action<RouteGroupBuilder>? AfterList { get; init; }
    }

    private static void MapResource<T, TId>(
        RouteGroupBuilder api,
        Resource<T, TId> r,
        string? listDescription = null,
        string? createDescription = null,
        string? replaceDescription = null,
        string? deleteDescription = null)
        where T : class
        where TId : struct, ITypedId<TId>
    {
        var group = api.MapGroup("/" + r.Path).WithTags(r.Tag);
        var singular = Pascal(r.Singular);
        var article = "aeiou".Contains(r.Singular[0], StringComparison.Ordinal) ? "an" : "a";
        var idPath = $"/{{id:{typeof(TId).Name}}}";

        group.MapGet("/", async (HttpContext http, ConfigService config, CancellationToken ct) =>
            {
                var current = await config.GetCurrentAsync(ct).ConfigureAwait(false);
                TagWithRevision(http, current);
                return Pagination.Respond(http, r.Items(current.Document), r.IdOf, r.PageJson);
            })
            .WithName("List" + Pascal(r.Plural)).WithSummary("List " + r.Plural).Described(listDescription)
            .WithPage().Produces<Page<T>>().ProducesApiProblems(400).RequirePermission(Permission.ConfigRead);
        r.AfterList?.Invoke(group);
        group.MapGet(idPath, async (HttpContext http, TId id, ConfigService config, CancellationToken ct) =>
            {
                if (r.BuiltIn?.Invoke(id) is { } builtIn)
                {
                    return EndpointSupport.Json(builtIn, r.Json);
                }

                var item = r.Items((await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document).FirstOrDefault(i => r.IdOf(i).Equals(id));
                if (item is null)
                {
                    return NoSuchObject(r.Singular);
                }

                http.Response.Headers.ETag = Preconditions.VersionTag(r.VersionOf(item));
                return EndpointSupport.Json(item, r.Json);
            })
            .WithName("Get" + singular).WithSummary($"Get {article} {r.Singular}")
            .Produces<T>().ProducesApiProblems(404).RequirePermission(Permission.ConfigRead);
        group.MapPost("/", (HttpContext http, AdminCaller caller, ConfigService config, CancellationToken ct) =>
                MutateAsync(http, caller, config, MutationKind.Create, requireVersion: false, r.Json,
                    (item, _) => (r.WithVersion(item, 0), null), r.Upsert, item => EndpointSupport.Path(r.Path, r.IdOf(item)), ct))
            .WithName("Create" + singular).WithSummary($"Create {article} {r.Singular}").Described(createDescription)
            .Accepts<T>("application/json").Produces<ConfigCommandResult>(201).ProducesApiProblems(400, 409, 415, 422).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        group.MapPut(idPath, (HttpContext http, TId id, AdminCaller caller, ConfigService config, CancellationToken ct) =>
                MutateAsync(http, caller, config, MutationKind.Change, requireVersion: true, r.Json,
                    (item, version) => r.IdOf(item).Equals(id) ? (r.WithVersion(item, version), null) : (item, IdMismatch()), r.Upsert, null, ct))
            .WithName("Replace" + singular).WithSummary($"Replace {article} {r.Singular}").Described(replaceDescription)
            .Accepts<T>("application/json").Produces<ConfigCommandResult>().ProducesApiProblems(400, 404, 412, 415, 422, 428).MayFailToPublish()
            .RequirePermission(Permission.ConfigWrite);
        group.MapDelete(idPath, (HttpContext http, TId id, AdminCaller caller, ConfigService config, CancellationToken ct) =>
                DeleteAsync(http, caller, config, version => r.Delete(id, version), ct))
            .WithName("Delete" + singular).WithSummary($"Delete {article} {r.Singular}").Described(deleteDescription)
            .Produces(204).Produces<ConfigCommandResult>().ProducesApiProblems(400, 404, 412, 422, 428).MayFailToPublish().RequirePermission(Permission.ConfigWrite);
    }

    private static RouteHandlerBuilder Described(this RouteHandlerBuilder builder, string? description) =>
        description is null ? builder : builder.WithDescription(description);

    /// <summary>"rate-limit policy" as <c>RateLimitPolicy</c>, for operation IDs.</summary>
    private static string Pascal(string words) =>
        string.Concat(words.Split(' ', '-').Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    private static async Task<IResult> GetSettingsAsync(HttpContext http, ConfigService config, CancellationToken ct)
    {
        var settings = (await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.Settings;
        http.Response.Headers.ETag = Preconditions.VersionTag(settings.Version);
        return EndpointSupport.Json(settings, AdminJsonContext.Default.GlobalSettings);
    }

    private static Task<IResult> PutSettingsAsync(HttpContext http, AdminCaller caller, ConfigService config, CancellationToken ct) =>
        MutateAsync(http, caller, config, MutationKind.Change, requireVersion: true, AdminJsonContext.Default.GlobalSettings,
            (settings, version) => (settings with { Version = version }, null),
            settings => new UpdateSettingsCommand(settings), null, ct);

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
        JsonTypeInfo<T> typeInfo,
        Func<T, long, (T Value, IResult? Problem)> prepare,
        Func<T, ConfigCommand> command,
        Func<T, string>? location,
        CancellationToken ct)
        where T : class
    {
        if (!Preconditions.TryGetFlags(http, out var dryRun, out var force, out var problem))
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
        var (value, rejected) = prepare(body.Value!, version);
        if (rejected is not null)
        {
            return rejected;
        }

        var result = await config.ExecuteAsync(command(value) with { DryRun = dryRun, Message = message, Force = force }, caller.Origin, ct).ConfigureAwait(false);
        return ConfigCommandResponses.ToResult(http, result, kind, location?.Invoke(value));
    }

    private static async Task<IResult> DeleteAsync(HttpContext http, AdminCaller caller, ConfigService config, Func<long, ConfigCommand> command, CancellationToken ct)
    {
        if (!Preconditions.TryGetFlags(http, out var dryRun, out var force, out var problem)
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
