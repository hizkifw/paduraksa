using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

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
/// <param name="Principal">The user behind the session.</param>
public sealed record PartialSession(SessionPrincipal Principal);

/// <summary>The request feature carrying the <see cref="PartialSession"/>.</summary>
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
    /// Where the request came from: the API channel, the connection's remote address (the admin listener trusts no forwarded header) and
    /// the user agent, cut short (the services clean and truncate it again).
    /// </summary>
    public static ClientContext GetClientContext(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string? userAgent = context.Request.Headers.UserAgent.ToString();
        if (userAgent.Length > MaxUserAgentInput)
        {
            userAgent = userAgent[..MaxUserAgentInput];
        }

        return new ClientContext(Channel.Api, context.GetClientAddress(), userAgent);
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
/// Authenticates <c>/api/</c> requests (PLAN §10.1): a session cookie or a <c>Bearer pdk_…</c> token, checked through
/// <see cref="SessionService"/> and <see cref="ApiTokenService"/> on every request, so a revoked token or a revoked session stops
/// working at once. On success it sets the <see cref="AdminCaller"/>.
/// </summary>
/// <remarks>
/// <para>It only ever <i>sets</i> a caller: a request that already has one (set ahead of the pipeline) is left as it is.</para>
/// <para><b>Never anonymous by accident.</b> A credential that is presented and wrong is a 401, not an anonymous request: a bad token,
/// an unknown or expired session cookie, an <c>Authorization</c> header of any scheme but Bearer, two <c>Authorization</c> headers, the
/// session cookie twice, or <i>both</i> a cookie and a bearer token (which one is meant is not guessed). The one exception is an
/// endpoint marked with <see cref="AdminAnonymousMetadata"/> (sign-in, setup): a browser with a stale cookie must still be able to
/// sign in, so a cookie that does not validate is ignored there (a bad bearer token still is not).</para>
/// <para>A session that still owes its second factor sets no caller; it is available as <see cref="PartialSession"/> to the endpoints that
/// accept it, and every other endpoint sees an anonymous request.</para>
/// </remarks>
internal sealed class AdminAuthenticationMiddleware(RequestDelegate next, SessionService sessions, ApiTokenService tokens)
{
    private const string BearerScheme = "Bearer";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || context.GetCaller() is not null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        bool anonymousAllowed = context.GetEndpoint()?.Metadata.GetMetadata<AdminAnonymousMetadata>() is not null;
        (bool cookiePresent, bool cookieAmbiguous, string? cookie) = AdminSessionCookie.Read(context.Request);
        StringValues authorization = context.Request.Headers.Authorization;

        if (cookieAmbiguous || authorization.Count > 1)
        {
            await RefuseAsync(context, "Send exactly one credential: one session cookie or one bearer token.", "ambiguous_credentials", "invalid_request").ConfigureAwait(false);
            return;
        }

        if (cookiePresent && authorization.Count > 0)
        {
            await RefuseAsync(context, "Send exactly one credential: a session cookie or a bearer token, not both.", "ambiguous_credentials", "invalid_request").ConfigureAwait(false);
            return;
        }

        if (authorization.Count == 1)
        {
            if (!TryReadBearer(authorization[0], out string? token))
            {
                await RefuseAsync(context, "The Authorization header must be 'Bearer <token>'.", "invalid_authorization", "invalid_request").ConfigureAwait(false);
                return;
            }

            ApiTokenVerification verified = await tokens.VerifyAsync(token, context.RequestAborted).ConfigureAwait(false);
            if (verified.Principal is not { } principal)
            {
                await RefuseAsync(context, "The API token is not valid.", "invalid_token", "invalid_token").ConfigureAwait(false);
                return;
            }

            CommandOrigin origin = new(Actor.ForToken(principal.TokenId), Channel.Api, context.GetClientAddress());
            context.SetCaller(new AdminCaller(origin, Token: principal.TokenId) { User = principal.UserId });
        }
        else if (cookiePresent)
        {
            SessionValidation validation = await sessions.ValidateAsync(cookie, context.RequestAborted).ConfigureAwait(false);
            switch (validation)
            {
                case { Status: SessionStatus.Valid, Principal: { } principal }:
                    CommandOrigin origin = new(Actor.ForUser(principal.UserId), Channel.Api, context.GetClientAddress());
                    context.SetCaller(new AdminCaller(origin, Session: principal.SessionId) { User = principal.UserId });
                    break;
                case { Status: SessionStatus.SecondFactorRequired, Principal: { } partial }:
                    context.Features.Set<IPartialSessionFeature>(new PartialSessionFeature(new PartialSession(partial)));
                    break;
                default:
                    if (!anonymousAllowed)
                    {
                        AdminSessionCookie.Clear(context.Response);
                        await RefuseAsync(context, "The session is not valid or has ended. Sign in again.", "invalid_session", null).ConfigureAwait(false);
                        return;
                    }

                    break;
            }
        }

        await next(context).ConfigureAwait(false);
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
