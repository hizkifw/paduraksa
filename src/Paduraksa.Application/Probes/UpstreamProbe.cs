using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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

    /// <summary>How long the connection and the TLS handshake of <c>FetchCertificateAsync</c> may take together.</summary>
    public static readonly TimeSpan CertificateTimeout = TimeSpan.FromSeconds(5);

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

        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length > 0 || uri.IdnHost.Length == 0)
        {
            return new UpstreamProbeResult(UpstreamProbeOutcome.Invalid, "Give an http:// or https:// address, such as http://192.168.1.20:8080.");
        }

        using CancellationTokenSource timeout = new(Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
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

    /// <summary>
    /// The certificate an <c>https</c> destination presents, for pinning it: a TLS handshake that accepts any certificate,
    /// sends nothing after it and closes. Unlike <see cref="CheckAsync"/> it reads what the other side sends, but only its
    /// public certificate, and only for a caller who may already route traffic to the address and read its answers.
    /// </summary>
    public Task<UpstreamCertificateResult> FetchCertificateAsync(string address, CommandOrigin origin, CancellationToken cancellationToken = default) =>
        FetchCertificateAsync(address, null, origin, cancellationToken);

    /// <summary>
    /// As <see cref="FetchCertificateAsync(string, CommandOrigin, CancellationToken)"/>, asking for <paramref name="serverName"/> in SNI.
    /// The proxy sends the client's host name as SNI, and an upstream that serves several names picks its certificate by
    /// it, so a pin is only right when it is fetched with the name the proxy will send.
    /// </summary>
    public async Task<UpstreamCertificateResult> FetchCertificateAsync(string address, string? serverName, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ConfigWrite, "upstream.certificate", null, cancellationToken).ConfigureAwait(false))
        {
            return new UpstreamCertificateResult(UpstreamCertificateOutcome.Denied);
        }

        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0 || uri.IdnHost.Length == 0)
        {
            return new UpstreamCertificateResult(UpstreamCertificateOutcome.Invalid, Message: "Only an https:// destination has a certificate to fetch.");
        }

        if (serverName is not null && Uri.CheckHostName(serverName) != UriHostNameType.Dns)
        {
            return new UpstreamCertificateResult(UpstreamCertificateOutcome.Invalid, Message: "The server name must be a host name.");
        }

        using CancellationTokenSource timeout = new(CertificateTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        UpstreamCertificateInfo? presented = null;
        try
        {
            using Socket socket = new(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(uri.IdnHost, uri.Port, linked.Token).ConfigureAwait(false);
            await using SslStream tls = new(new NetworkStream(socket, ownsSocket: false), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    // SNI as asked, else as the address names it; an IP literal sends none.
                    TargetHost = serverName ?? (uri.HostNameType == UriHostNameType.Dns ? uri.IdnHost : string.Empty),
                    // Accept anything: the point is to see the certificate, not to trust it, and nothing is sent after the
                    // handshake. It is read here, while it is certainly alive.
#pragma warning disable CA5359 // Deliberate: this only reads the certificate so a person can decide to pin it.
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    {
                        presented = certificate is X509Certificate2 x509 ? Describe(x509) : null;
                        return true;
                    },
#pragma warning restore CA5359
                },
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new UpstreamCertificateResult(UpstreamCertificateOutcome.Unreachable);
        }
        catch (SocketException ex)
        {
            LogUnreachable(uri.IdnHost, uri.Port, ex.SocketErrorCode);
            return new UpstreamCertificateResult(UpstreamCertificateOutcome.Unreachable);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            return new UpstreamCertificateResult(UpstreamCertificateOutcome.NoTls);
        }

        return presented is null
            ? new UpstreamCertificateResult(UpstreamCertificateOutcome.NoTls)
            : new UpstreamCertificateResult(UpstreamCertificateOutcome.Fetched, presented);
    }

    private static UpstreamCertificateInfo Describe(X509Certificate2 certificate) => new(
        Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256)),
        certificate.Subject,
        certificate.Issuer,
        certificate.NotBefore.ToUniversalTime(),
        certificate.NotAfter.ToUniversalTime(),
        certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault()?.EnumerateDnsNames().ToArray() ?? [],
        certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData));

    [LoggerMessage(Level = LogLevel.Debug, Message = "Upstream probe: {Host}:{Port} is unreachable ({Reason}).")]
    private partial void LogUnreachable(string host, int port, SocketError reason);
}
