using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace Paduraksa.ProxyProtocol.AspNetCore;

internal sealed class ProxyProtocolMiddleware
{
    private static readonly Action<ILogger, Exception> OutcomeCallbackThrew = LoggerMessage.Define(
        LogLevel.Debug, new EventId(1, nameof(OutcomeCallbackThrew)), "The ProxyProtocolOptions.OnOutcome callback threw; the connection goes on.");

    private readonly ConnectionDelegate _next;
    private readonly IPNetwork[] _trusted;
    private readonly TimeSpan _timeout;
    private readonly int _maxV2Length;
    private readonly bool _required;
    private readonly Action<ProxyProtocolOutcome>? _onOutcome;
    private readonly ILogger? _logger;

    public ProxyProtocolMiddleware(ConnectionDelegate next, ProxyProtocolOptions options, ILogger? logger = null)
    {
        _logger = logger;
        _next = next;
        _trusted = [.. options.TrustedProxies];
        _timeout = options.HeaderTimeout;
        _maxV2Length = options.MaxV2Length;
        _required = options.Required;
        _onOutcome = options.OnOutcome;
    }

    public Task OnConnectionAsync(ConnectionContext connection)
    {
        var peer = connection.RemoteEndPoint;
        if (!IsTrusted(peer))
        {
            Report(ProxyProtocolOutcome.UntrustedSource);
            if (!_required)
            {
                return _next(connection);
            }

            // Exceptions are allocated per abort (a cold path): a shared instance would carry one connection's stack trace into every other connection's logs.
            connection.Abort(new ConnectionAbortedException("PROXY protocol: peer is not a trusted proxy."));
            return Task.CompletedTask;
        }

        return ReadHeaderThenContinueAsync(connection, peer);
    }

    private async Task ReadHeaderThenContinueAsync(ConnectionContext connection, EndPoint? peer)
    {
        var result = await ProxyProtocolReader.ReadAsync(connection.Transport.Input, _timeout, _maxV2Length, headerOptional: !_required)
            .ConfigureAwait(false);

        switch (result.Status)
        {
            case ProxyProtocolReadStatus.Success:
                var header = result.Header;
                Report(header.HasAddresses ? ProxyProtocolOutcome.Accepted : ProxyProtocolOutcome.AcceptedWithoutAddress);
                connection.Features.Set<IProxyProtocolFeature>(new ProxyProtocolFeature(header, peer));
                if (header.HasAddresses)
                {
                    connection.RemoteEndPoint = header.SourceEndPoint;
                    connection.LocalEndPoint = header.DestinationEndPoint;
                }

                break;

            case ProxyProtocolReadStatus.Absent:
                Report(ProxyProtocolOutcome.NoHeader);
                break;

            default:
                var (outcome, reason) = result.Status switch
                {
                    ProxyProtocolReadStatus.Invalid => (ProxyProtocolOutcome.Invalid, "PROXY protocol: invalid header."),
                    ProxyProtocolReadStatus.TimedOut => (ProxyProtocolOutcome.TimedOut, "PROXY protocol: header timed out."),
                    _ => (ProxyProtocolOutcome.Closed, "PROXY protocol: closed before the header."),
                };
                Report(outcome);
                connection.Abort(new ConnectionAbortedException(reason));
                return;
        }

        await _next(connection).ConfigureAwait(false);
    }

    // A counter is not worth a connection: whatever the callback throws, the decision it reports stands.
    private void Report(ProxyProtocolOutcome outcome)
    {
        if (_onOutcome is null)
        {
            return;
        }

        try
        {
            _onOutcome(outcome);
        }
#pragma warning disable CA1031 // Any exception: the callback is the caller's code.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            if (_logger is { } logger)
            {
                OutcomeCallbackThrew(logger, exception);
            }
        }
    }

    private bool IsTrusted(EndPoint? peer)
    {
        if (peer is not IPEndPoint endPoint)
        {
            return false;
        }

        // A dual-mode [::] listener reports an IPv4 peer as ::ffff:a.b.c.d, which an IPv4 network doesn't contain.
        var address = endPoint.Address.IsIPv4MappedToIPv6 ? endPoint.Address.MapToIPv4() : endPoint.Address;
        foreach (var network in _trusted)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class ProxyProtocolFeature(ProxyProtocolHeader header, EndPoint? proxyEndPoint) : IProxyProtocolFeature
    {
        public ProxyProtocolHeader Header { get; } = header;

        public EndPoint? ProxyEndPoint { get; } = proxyEndPoint;
    }
}
