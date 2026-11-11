using System.Net;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

using Paduraksa.Admin.Api;
using Paduraksa.Application.Commands;
using Paduraksa.Core.Hosting;
using Paduraksa.Core.Text;

namespace Paduraksa.Admin.Auth;

/// <summary>
/// Marks a request that arrived on the admin app's <b>internal</b> listener: the public proxy forwarded it, the hop secret
/// checked out, so its client address, scheme and host were taken from the <c>X-Forwarded-*</c> headers the proxy wrote.
/// </summary>
internal interface IInternalAdminRequestFeature
{
}

internal sealed class InternalAdminRequestFeature : IInternalAdminRequestFeature
{
    public static InternalAdminRequestFeature Instance { get; } = new();

    private InternalAdminRequestFeature()
    {
    }
}

/// <summary>Whether a request came through the internal listener (see <see cref="IInternalAdminRequestFeature"/>).</summary>
public static class AdminInternalRequestExtensions
{
    /// <summary>
    /// True when the connection was accepted by the admin app's internal listener. The marker is set by a connection middleware
    /// on that <c>ListenOptions</c> only (not by a peer address or a port), so it also works before the fallback's port is known.
    /// </summary>
    internal static bool IsOnInternalListener(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<IConnectionItemsFeature>()?.Items.ContainsKey(AdminInternalConnectionMarker.Key) == true;
    }

    /// <summary>True: the request came through the proxy's internal admin listener, so its forwarded headers were trusted.</summary>
    public static bool IsInternalAdminRequest(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<IInternalAdminRequestFeature>() is not null;
    }

    /// <summary>
    /// The caller's request facts the config lock-out guard needs, or null on any other listener. Taken after the forwarded
    /// headers were applied: the public host, its scheme and the resolved client address.
    /// </summary>
    public static AdminRequestFacts? GetAdminRequestFacts(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsInternalAdminRequest())
        {
            return null;
        }

        var host = context.Request.Host.Value ?? string.Empty;
        return new AdminRequestFacts(
            host,
            context.Request.IsHttps,
            context.Connection.RemoteIpAddress is { IsIPv4MappedToIPv6: true } mapped ? mapped.MapToIPv4() : context.Connection.RemoteIpAddress,
            context.Request.Headers.UserAgent.Count == 0 ? null : context.Request.Headers.UserAgent.ToString());
    }
}

/// <summary>
/// Trusts <c>X-Forwarded-For</c>, <c>-Proto</c> and <c>-Host</c> <b>only</b> on the admin app's internal listener, and
/// nowhere else: on the main admin port and the loopback plain-HTTP fallback listener those headers are ignored.
/// </summary>
/// <remarks>
/// <b>Authenticated hop.</b> The listener can be reached by local processes (and the TCP fallback by anything that can
/// reach loopback), so the forwarded headers are trusted only after <see cref="AdminInternalListener.HeaderName"/> carries
/// this process's secret. The proxy stamps it on admin routes for the same process; a missing or wrong value is a 403 and
/// nothing is rewritten. The secret is compared in fixed time.
///
/// The public proxy always overwrites the forwarded headers before forwarding, so the internal listener takes a single
/// value and never a chain: a comma, a repeated header line, an unparsable address or a scheme other than
/// <c>http</c>/<c>https</c> is ignored rather than guessed. With no usable <c>X-Forwarded-For</c> the client address
/// becomes unknown (never loopback), so a restricted <c>Paduraksa:Listen:AdminAllowedSources</c> still refuses it and the
/// sign-in throttle does not lump every client into a loopback bucket. <b>The allowlist on this listener</b> is applied per
/// request to the forwarded client address, the only place it can run there (the socket's peer is always the process
/// itself), and it is a second gate: the public host's own policies are the primary one. The main admin port keeps its
/// per-connection, before-TLS check.
/// </remarks>
internal sealed class AdminForwardedHeadersMiddleware(RequestDelegate next, AdminInternalListener listener, AdminSourcePolicy sources)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsOnInternalListener())
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!listener.IsValidSecret(context.Request.Headers[AdminInternalListener.HeaderName].ToString()))
        {
            await AdminProblems.Forbidden(
                "Internal admin hop refused",
                "The request isn't from this process's proxy: the internal hop header is missing or invalid.",
                ApiCodes.Admin.InternalHop).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        context.Request.Headers.Remove(AdminInternalListener.HeaderName);
        context.Features.Set<IInternalAdminRequestFeature>(InternalAdminRequestFeature.Instance);
        ApplyClient(context);
        ApplyScheme(context);
        ApplyHost(context);

        if (!sources.IsAllowed(context.Connection.RemoteIpAddress))
        {
            await AdminProblems.Forbidden(
                "Source not allowed",
                "The forwarded client address isn't in Paduraksa:Listen:AdminAllowedSources.",
                ApiCodes.Admin.SourceDenied).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>Sets the connection's client address from a single <c>X-Forwarded-For</c> value; unknown (null) otherwise.</summary>
    private static void ApplyClient(HttpContext context)
    {
        var value = context.Request.Headers["X-Forwarded-For"].ToString().Trim();
        IPAddress? address = null;
        if (value.Length > 0 && !value.Contains(',', StringComparison.Ordinal) && IPAddress.TryParse(value, out var parsed))
        {
            address = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        }

        context.Connection.RemoteIpAddress = address;
    }

    /// <summary>Sets the request scheme from <c>X-Forwarded-Proto</c> (exactly <c>http</c> or <c>https</c>).</summary>
    private static void ApplyScheme(HttpContext context)
    {
        var value = context.Request.Headers["X-Forwarded-Proto"].ToString().Trim();
        if (value.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Scheme = "https";
        }
        else if (value.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.Scheme = "http";
        }
    }

    /// <summary>Sets the request host from <c>X-Forwarded-Host</c> (the public name and port the client used).</summary>
    private static void ApplyHost(HttpContext context)
    {
        var value = context.Request.Headers["X-Forwarded-Host"].ToString().Trim();
        if (value.Length == 0 || value.AsSpan().ContainsAnyExcept(AsciiChars.Visible))
        {
            return;
        }

        try
        {
            context.Request.Host = new HostString(value);
        }
        catch (ArgumentException)
        {
            // Not a host this app can represent: keep the listener's own (which the proxy set to the symbolic upstream).
        }
    }
}
