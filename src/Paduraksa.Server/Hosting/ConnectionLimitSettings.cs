using Microsoft.AspNetCore.Server.Kestrel.Core;

using Paduraksa.Application.Settings;
using Paduraksa.Contracts.Config;
using Paduraksa.Proxy.Net;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The public listeners' limits from the server settings (docs/design/config.md "Server settings", connections). Kestrel reads the timeouts and
/// data rates from its options for each new connection, so those are set on the bound options when they change; the handshake timeout and the
/// connection caps are read at bind, so they take the values in force when the public app starts, and the page marks a change to them pending.
/// </summary>
internal static class ConnectionLimitSettings
{
    /// <summary><paramref name="bootstrap"/> (the configuration's) with every limit the server settings declare taken from <paramref name="values"/>.</summary>
    public static ServerLimits Of(ServerSettings values, ServerLimits bootstrap) => bootstrap with
    {
        RequestHeadersTimeout = ServerSettingCatalog.LimitsRequestHeadersTimeout.Resolve(values),
        KeepAliveTimeout = ServerSettingCatalog.LimitsKeepAliveTimeout.Resolve(values),
        TlsHandshakeTimeout = ServerSettingCatalog.LimitsTlsHandshakeTimeout.Resolve(values),
        MinRequestBodyRate = ServerSettingCatalog.LimitsMinRequestBodyRate.Resolve(values),
        MinRequestBodyGrace = ServerSettingCatalog.LimitsMinRequestBodyGrace.Resolve(values),
        MinResponseRate = ServerSettingCatalog.LimitsMinResponseRate.Resolve(values),
        MinResponseGrace = ServerSettingCatalog.LimitsMinResponseGrace.Resolve(values),
        MaxConnections = ServerSettingCatalog.LimitsMaxConnections.Resolve(values),
        MaxUpgradedConnections = ServerSettingCatalog.LimitsMaxUpgradedConnections.Resolve(values),
    };

    /// <summary>
    /// Sets the limits on <paramref name="limits"/> once, before the listeners bind, and keeps the ones Kestrel reads per connection current from
    /// then on. Returns what was set, for the TLS handshake timeout the selector takes at bind.
    /// </summary>
    public static ServerLimits ApplyAndFollow(KestrelServerLimits limits, ServerSettingsState state, ServerLimits bootstrap)
    {
        var current = Of(state.Current, bootstrap);
        current.Apply(limits);
        state.Changed += values =>
        {
            var next = Of(values, bootstrap);
            if (next.Validate().Count > 0)
            {
                // The validator holds the stored values to the same rules; a mix it can't see (an environment value) keeps the limits in force.
                return;
            }

            // Plain property stores of what Kestrel reads when a connection or request starts; a connection in progress keeps what it read.
            // They are KestrelServerLimits' setters, not fields, so there is nothing to Volatile.Write. Each is one aligned 64-bit word (a TimeSpan)
            // or one reference (a MinDataRate), and this build is 64-bit only, so a reader sees the old value or the new, never a torn one.
            // Which connection sees the new value first is not ordered, and needn't be: each takes whichever it reads when it starts.
            limits.RequestHeadersTimeout = next.RequestHeadersTimeout;
            limits.KeepAliveTimeout = next.KeepAliveTimeout;
            limits.MinRequestBodyDataRate = next.MinRequestBodyRate == 0 ? null : new MinDataRate(next.MinRequestBodyRate, next.MinRequestBodyGrace);
            limits.MinResponseDataRate = next.MinResponseRate == 0 ? null : new MinDataRate(next.MinResponseRate, next.MinResponseGrace);
        };
        return current;
    }
}
