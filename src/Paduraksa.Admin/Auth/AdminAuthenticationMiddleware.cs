using System.Net;
using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Admin.Api;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Auth;

/// <summary>Endpoint metadata: the endpoint is reachable without credentials, and a stale or wrong credential on it is ignored instead of refused.</summary>
public sealed class AdminAnonymousMetadata
{
    public static AdminAnonymousMetadata Instance { get; } = new();
}

/// <summary>A session whose second factor is still owed: it authenticates nothing but the second-factor step and logout.</summary>
public sealed record PartialSession(SessionPrincipal Principal);

public interface IPartialSessionFeature
{
    PartialSession Session { get; }
}

internal sealed class PartialSessionFeature(PartialSession session) : IPartialSessionFeature
{
    public PartialSession Session { get; } = session;
}

/// <summary>Anonymous endpoints and the request context the identity endpoints share.</summary>
public static class AdminAuthEndpointExtensions
{
    /// <summary>Marks the endpoints reachable without credentials (sign-in, setup); see <see cref="AdminAnonymousMetadata"/>.</summary>
    public static TBuilder AllowAnonymousAdmin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(AdminAnonymousMetadata.Instance);
        return builder;
    }

    /// <summary>The partial session of the request (password given, second factor owed), or null.</summary>
    public static PartialSession? GetPartialSession(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<IPartialSessionFeature>()?.Session;
    }

    /// <summary>
    /// Where the request came from: the API channel, the request's remote address (on the internal listener the forwarded client
    /// address the proxy resolved; elsewhere the connection's own address, since forwarded headers are ignored there) and the user
    /// agent, cut short (the services clean and truncate it again).
    /// </summary>
    public static ClientContext GetClientContext(this HttpContext context) => context.GetClientContext(Channel.Api);

    /// <summary>Where the request came from, through <paramref name="channel"/> (the dashboard's sign-in forms are <see cref="Channel.Dashboard"/>).</summary>
    public static ClientContext GetClientContext(this HttpContext context, Channel channel)
    {
        ArgumentNullException.ThrowIfNull(context);
        string? userAgent = context.Request.Headers.UserAgent.ToString();
        if (userAgent.Length > MaxUserAgentInput)
        {
            userAgent = userAgent[..MaxUserAgentInput];
        }

        return new ClientContext(channel, context.GetClientAddress(), userAgent);
    }

    /// <summary>The connection's remote address, IPv4-mapped IPv6 addresses shown as IPv4.</summary>
    public static IPAddress? GetClientAddress(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IPAddress? address = context.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
    }

    private const int MaxUserAgentInput = 512;
}

/// <summary>
/// Authenticates <c>/api/</c> requests: a session cookie or a <c>Bearer pdk_...</c> token, checked through
/// <see cref="SessionService"/> and <see cref="ApiTokenService"/> on every request, so a revoked token or a revoked session stops
/// working at once. On success it sets the <see cref="AdminCaller"/>.
/// </summary>
/// <remarks>
/// It only ever <i>sets</i> a caller: a request that already has one (set ahead of the pipeline) is left as it is.
/// <b>Never anonymous by accident:</b> a credential that is presented and wrong is a 401, not an anonymous request (a bad
/// token, an unknown or expired session cookie, an <c>Authorization</c> header of any scheme but Bearer, two of them, the
/// session cookie twice, or <i>both</i> a cookie and a bearer token, since which one is meant is not guessed). The one
/// exception is an endpoint marked with <see cref="AdminAnonymousMetadata"/> (sign-in, setup), where a stale cookie is
/// ignored so that a browser can still sign in (a bad bearer token still is not). A session that still owes its second
/// factor sets no caller; it is
/// available as <see cref="PartialSession"/>. Outside <c>/api</c> only the session cookie counts and it is lenient,
/// except under <see cref="AdminPipelineHooks.AuthenticatedPathPrefixes"/> (a bare 401) and
/// <see cref="AdminPipelineHooks.StaticAssetPathPrefixes"/>, where the cookie is not looked at at all because a static
/// file needs no identity and a page load pulls dozens of them. A signed-in session also becomes the request's
/// <see cref="HttpContext.User"/>, which is what the dashboard's authorization reads.
/// </remarks>
internal sealed class AdminAuthenticationMiddleware(RequestDelegate next, SessionService sessions, ApiTokenService tokens, AdminPipelineHooks hooks)
{
    private const string BearerScheme = "Bearer";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetCaller() is not null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await AuthenticateBrowserAsync(context).ConfigureAwait(false);
            return;
        }

        bool anonymousAllowed = context.GetEndpoint()?.Metadata.GetMetadata<AdminAnonymousMetadata>() is not null;
        (bool cookiePresent, bool cookieAmbiguous, string? cookie) = AdminSessionCookie.Read(context.Request);
        StringValues authorization = context.Request.Headers.Authorization;

        if (cookieAmbiguous || authorization.Count > 1)
        {
            await RefuseAsync(context, "Send exactly one credential: one session cookie or one bearer token.", ApiCodes.Auth.AmbiguousCredentials, "invalid_request").ConfigureAwait(false);
            return;
        }

        if (cookiePresent && authorization.Count > 0)
        {
            await RefuseAsync(context, "Send exactly one credential: a session cookie or a bearer token, not both.", ApiCodes.Auth.AmbiguousCredentials, "invalid_request").ConfigureAwait(false);
            return;
        }

        if (authorization.Count == 1)
        {
            if (!TryReadBearer(authorization[0], out string? token))
            {
                await RefuseAsync(context, "The Authorization header must be 'Bearer <token>'.", ApiCodes.Auth.InvalidAuthorization, "invalid_request").ConfigureAwait(false);
                return;
            }

            ApiTokenVerification verified = await tokens.VerifyAsync(token, context.RequestAborted).ConfigureAwait(false);
            if (verified.Principal is not { } principal)
            {
                await RefuseAsync(context, "The API token is not valid.", ApiCodes.Auth.InvalidToken, "invalid_token").ConfigureAwait(false);
                return;
            }

            CommandOrigin origin = new(Actor.ForToken(principal.TokenId), Channel.Api, context.GetClientAddress(), context.GetAdminRequestFacts());
            context.SetCaller(new AdminCaller(origin, Token: principal.TokenId) { User = principal.UserId });
        }
        else if (cookiePresent)
        {
            SessionValidation validation = await sessions.ValidateAsync(cookie, context.RequestAborted).ConfigureAwait(false);
            switch (validation)
            {
                case { Status: SessionStatus.Valid, Principal: { } principal }:
                    SignIn(context, principal, Channel.Api);
                    break;
                case { Status: SessionStatus.SecondFactorRequired, Principal: { } partial }:
                    context.Features.Set<IPartialSessionFeature>(new PartialSessionFeature(new PartialSession(partial)));
                    break;
                default:
                    if (!anonymousAllowed)
                    {
                        AdminSessionCookie.Clear(context.Response);
                        await RefuseAsync(context, "The session is not valid or has ended. Sign in again.", ApiCodes.Auth.InvalidSession, null).ConfigureAwait(false);
                        return;
                    }

                    break;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>The dashboard side: the session cookie only, never refused here (see the remarks).</summary>
    private async Task AuthenticateBrowserAsync(HttpContext context)
    {
        // A static file needs no identity: no session lookup, whatever cookie the browser sends with it.
        if (IsStaticAsset(context.Request.Path))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        (bool cookiePresent, bool cookieAmbiguous, string? cookie) = AdminSessionCookie.Read(context.Request);
        if (cookiePresent && !cookieAmbiguous)
        {
            SessionValidation validation = await sessions.ValidateAsync(cookie, context.RequestAborted).ConfigureAwait(false);
            switch (validation)
            {
                case { Status: SessionStatus.Valid, Principal: { } principal }:
                    SignIn(context, principal, Channel.Dashboard);
                    break;
                case { Status: SessionStatus.SecondFactorRequired, Principal: { } partial }:
                    context.Features.Set<IPartialSessionFeature>(new PartialSessionFeature(new PartialSession(partial)));
                    break;
            }
        }

        if (context.GetCaller() is null && MatchesAny(context.Request.Path, hooks.AuthenticatedPathPrefixes))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the path is a static file: under <see cref="AdminPipelineHooks.StaticAssetPathPrefixes"/> and not under
    /// <see cref="AdminPipelineHooks.AuthenticatedPathPrefixes"/>, so a prefix named in both lists stays protected.
    /// </summary>
    private bool IsStaticAsset(PathString path) =>
        MatchesAny(path, hooks.StaticAssetPathPrefixes) && !MatchesAny(path, hooks.AuthenticatedPathPrefixes);

    private static bool MatchesAny(PathString path, IEnumerable<string> prefixes) =>
        prefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sets the caller of a valid full session, and the request's user to its <see cref="SessionIdentity"/>.</summary>
    private static void SignIn(HttpContext context, SessionPrincipal principal, Channel channel)
    {
        SessionIdentity identity = new(principal, context.GetClientAddress(), context.GetAdminRequestFacts());
        context.SetCaller(new AdminCaller(identity.OriginFor(channel), Session: principal.SessionId) { User = principal.UserId });
        context.User = new ClaimsPrincipal(identity);
    }

    private static bool TryReadBearer(string? header, out string? token)
    {
        token = null;
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        int space = header.IndexOf(' ', StringComparison.Ordinal);
        if (space <= 0 || !header.AsSpan(0, space).Equals(BearerScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string value = header[(space + 1)..].Trim();
        if (value.Length == 0 || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        token = value;
        return true;
    }

    private static Task RefuseAsync(HttpContext context, string detail, string code, string? bearerError) =>
        AdminProblems.Unauthorized(context, detail, code, bearerError).ExecuteAsync(context);
}
