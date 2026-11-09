using System.Net.Sockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Probes;

/// <summary>
/// "Is the upstream there?" for the first-run wizard's first host: a TCP connection to the address's host and port within a
/// short timeout, closed at once. Nothing is sent and nothing is read, so the answer is only reachable or not: no banner, no status code,
/// no error text from the other side, and the probe can't be used to read anything from the network the server sits in. It needs
/// <see cref="Permission.ConfigWrite"/> (the person who could add the host anyway); a refusal is audited like the services' others.
/// </summary>
public sealed partial class UpstreamProbe
{
    /// <summary>How long the name lookup and the connection may take together.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly ConfigService _config;
    private readonly IPermissionEvaluator? _permissions;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public UpstreamProbe(ConfigService config, IPermissionEvaluator? permissions = null, TimeProvider? time = null, ILogger<UpstreamProbe>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _permissions = permissions;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>
    /// Whether a TCP connection to <paramref name="address"/>'s host and port (the scheme's default port when it names none) opens within
    /// <see cref="Timeout"/>. <paramref name="address"/> must be an absolute <c>http</c> or <c>https</c> URL without credentials, as a
    /// destination's address is.
    /// </summary>
    public async Task<UpstreamProbeResult> CheckAsync(string address, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ConfigWrite, "upstream.probe", null, cancellationToken).ConfigureAwait(false))
        {
            return new UpstreamProbeResult(UpstreamProbeOutcome.Denied);
        }

        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length > 0 || uri.IdnHost.Length == 0)
        {
            return new UpstreamProbeResult(UpstreamProbeOutcome.Invalid, "Give an http:// or https:// address, such as http://192.168.1.20:8080.");
        }

        using CancellationTokenSource timeout = new(Timeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(uri.IdnHost, uri.Port, linked.Token).ConfigureAwait(false);
            return new UpstreamProbeResult(UpstreamProbeOutcome.Reachable);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(uri.IdnHost, uri.Port, SocketError.TimedOut);
            return new UpstreamProbeResult(UpstreamProbeOutcome.Unreachable);
        }
        catch (SocketException ex)
        {
            LogUnreachable(uri.IdnHost, uri.Port, ex.SocketErrorCode);
            return new UpstreamProbeResult(UpstreamProbeOutcome.Unreachable);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Upstream probe: {Host}:{Port} is unreachable ({Reason}).")]
    private partial void LogUnreachable(string host, int port, SocketError reason);
}
