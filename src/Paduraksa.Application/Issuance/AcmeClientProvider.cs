using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using Paduraksa.Acme;
using Paduraksa.Core.Vault;

namespace Paduraksa.Application.Issuance;

/// <summary>The ACME client for a CA's directory. An <see cref="AcmeClient"/> talks to one CA, and accounts name theirs.</summary>
public interface IAcmeClientProvider
{
    /// <summary>The client for <paramref name="directoryUrl"/>: the same instance for the same URL, so its directory and nonce caches are shared.</summary>
    AcmeClient GetClient(Uri directoryUrl);
}

/// <summary>The default <see cref="IAcmeClientProvider"/>: one <see cref="AcmeClient"/> per directory URL, all
/// signing through the vault's <see cref="IJwsSigner"/> and <see cref="IMacKeySource"/>.</summary>
public sealed class AcmeClientProvider : IAcmeClientProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<AcmeClient>> _clients = new(StringComparer.Ordinal);
    private readonly IJwsSigner _signer;
    private readonly IMacKeySource? _macKeys;
    private readonly AcmeClientOptions _options;
    private readonly Func<HttpMessageHandler> _handlerFactory;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggers;
    private bool _disposed;

    /// <param name="signer">Signs account-key JWS and reports the public key (the vault).</param>
    /// <param name="macKeys">Opens EAB MAC keys; <see langword="null"/> if no account uses external account binding.</param>
    /// <param name="options">The client's timeouts, polling bounds and allowed origins.</param>
    /// <param name="handlerFactory">The transport a client runs on (see <see cref="AcmeClient"/>). It is not disposed here.</param>
    /// <param name="time">The clock for timeouts and polling delays.</param>
    /// <param name="loggers">Where the clients log.</param>
    public AcmeClientProvider(
        IJwsSigner signer,
        IMacKeySource? macKeys,
        AcmeClientOptions options,
        Func<HttpMessageHandler> handlerFactory,
        TimeProvider time,
        ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(handlerFactory);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(loggers);
        _signer = signer;
        _macKeys = macKeys;
        _options = options;
        _handlerFactory = handlerFactory;
        _time = time;
        _loggers = loggers;
    }

    /// <inheritdoc />
    public AcmeClient GetClient(Uri directoryUrl)
    {
        ArgumentNullException.ThrowIfNull(directoryUrl);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _clients.GetOrAdd(directoryUrl.AbsoluteUri, _ => new Lazy<AcmeClient>(() => Create(directoryUrl))).Value;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        foreach (var client in _clients.Values)
        {
            if (client.IsValueCreated)
            {
                client.Value.Dispose();
            }
        }

        _clients.Clear();
    }

    private AcmeClient Create(Uri directoryUrl) =>
        new(directoryUrl, _signer, _macKeys, _options, _handlerFactory(), _time, _loggers.CreateLogger<AcmeClient>());
}
