using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Acme.Dns;

/// <summary>
/// <see cref="IDnsProviderFactory"/> for the built-in providers (Cloudflare, deSEC). It holds only the vault's
/// <see cref="ISecretHttpHandlerFactory"/>: a provider it builds is given a secret id and the right to ask the vault for a
/// handler, never a token. A provider is cheap and short-lived (it opens an HTTP client per operation), so
/// <see cref="Create"/> builds a new one each time.
/// </summary>
public sealed class DnsProviderFactory : IDnsProviderFactory
{
    private readonly ISecretHttpHandlerFactory _handlers;
    private readonly DnsProviderOptions _options;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _logs;

    /// <param name="handlers">The vault's source of authenticating handlers.</param>
    /// <param name="options">Tunables; the defaults when null.</param>
    /// <param name="time">The clock for rate-limit waits; the system clock when null.</param>
    /// <param name="logs">Where providers log; nowhere when null.</param>
    public DnsProviderFactory(ISecretHttpHandlerFactory handlers, DnsProviderOptions? options = null, TimeProvider? time = null, ILoggerFactory? logs = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers;
        _options = options ?? new DnsProviderOptions();
        _time = time ?? TimeProvider.System;
        _logs = logs ?? NullLoggerFactory.Instance;
    }

    /// <inheritdoc />
    public IDnsProvider Create(DnsProviderKind kind, SecretId credential) => kind switch
    {
        DnsProviderKind.Cloudflare => new CloudflareDnsProvider(_handlers, credential, _options, _time, _logs.CreateLogger<CloudflareDnsProvider>()),
        DnsProviderKind.DeSec => new DesecDnsProvider(_handlers, credential, _options, _time, _logs.CreateLogger<DesecDnsProvider>()),
        _ => throw new NotSupportedException($"There is no DNS provider implementation for {kind}."),
    };
}
