using Microsoft.Extensions.Logging;

using Paduraksa.Core.Vault;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// What the proof-of-work gate needs at request time from outside the immutable snapshot: the vault's HMAC key, the
/// clock and the process counters. One instance per process; the snapshot compiler passes it to every policy, and the
/// public app registers the same instance for the verify endpoint.
/// </summary>
/// <remarks>
/// The key is opened once at startup, after the vault is ready and before the public listener binds
/// (<c>ProofOfWorkKeyStartup</c>). Until then, and whenever it can't be opened, <see cref="Key"/> is null and a protected
/// route fails closed: the policy answers 503 rather than let a request through unchallenged.
/// </remarks>
public sealed partial class ProofOfWorkRuntime
{
    private readonly ILogger? _logger;
    private IMacKey? _key;
    private int _reportedUnavailable;

    public ProofOfWorkRuntime()
        : this(TimeProvider.System, null)
    {
    }

    public ProofOfWorkRuntime(TimeProvider time, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        Time = time;
        _logger = logger;
    }

    /// <summary>The shared keyless runtime the compiler uses when no runtime is configured: every protected route fails closed.</summary>
    public static ProofOfWorkRuntime Unavailable { get; } = new();

    /// <summary>The process counters, shared by every policy and the verify endpoint.</summary>
    public ProofOfWorkStats Stats { get; } = new();

    public TimeProvider Time { get; }

    /// <summary>The HMAC key handle, or null when it couldn't be opened. Read once per protected request.</summary>
    public IMacKey? Key => Volatile.Read(ref _key);

    /// <summary>Installs the handle the startup step opened; called once, never disposed (a handle is a vault reference).</summary>
    public void UseKey(IMacKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Volatile.Write(ref _key, key);
    }

    /// <summary>
    /// Reports the missing key once per process at Error: a protected route is answering 503, which is a configuration or
    /// vault failure an operator must see, but not one line per request.
    /// </summary>
    internal void ReportUnavailable()
    {
        if (_logger is not null && Interlocked.Exchange(ref _reportedUnavailable, 1) == 0)
        {
            LogUnavailable(_logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The proof-of-work HMAC key is unavailable, so every proof-of-work-protected route answers 503. Check the vault and the startup log.")]
    private static partial void LogUnavailable(ILogger logger);
}
