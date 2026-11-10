using System.Security.Cryptography;

using Paduraksa.Application.ProofOfWork;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Gate;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Ensures the proof-of-work HMAC key exists and opens it into the <see cref="ProofOfWorkRuntime"/>, before the public
/// listener binds (a core-host start step, after the vault is initialized). A failure is logged and leaves the key
/// unavailable: protected routes then fail closed, which is the one safe answer.
/// </summary>
internal sealed partial class ProofOfWorkKeyStartup : IHostedService
{
    private const int KeyBytes = 32;
    private const int EnsureAttempts = 3;

    private readonly ISecretStore _secrets;
    private readonly IMacKeySource _keys;
    private readonly ProofOfWorkRuntime _runtime;
    private readonly ILogger _logger;

    public ProofOfWorkKeyStartup(ISecretStore secrets, IMacKeySource keys, ProofOfWorkRuntime runtime, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _secrets = secrets;
        _keys = keys;
        _runtime = runtime;
        _logger = loggerFactory.CreateLogger<ProofOfWorkKeyStartup>();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Two processes on one vault can race the very first insert of the reserved ID; the loser's next attempt finds the
        // row and adopts it, so a lost race doesn't leave protected routes at 503 until a restart.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await EnsureKeyAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt == EnsureAttempts)
                {
                    LogFailed(ex);
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureKeyAsync(CancellationToken cancellationToken)
    {
        byte[] value = new byte[KeyBytes];
        try
        {
            RandomNumberGenerator.Fill(value);
            var metadata = await _secrets.EnsureAsync(
                ProofOfWorkKey.Id,
                SecretKind.HmacSigningKey,
                value,
                new SecretOptions(ProofOfWorkKey.Label, MacAlgorithm.HmacSha256, AllowedHosts: null),
                cancellationToken).ConfigureAwait(false);
            if (metadata.MacAlgorithm != MacAlgorithm.HmacSha256)
            {
                LogWrongAlgorithm(metadata.MacAlgorithm);
                return;
            }

            var key = await _keys.OpenAsync(ProofOfWorkKey.Id, SecretKind.HmacSigningKey, cancellationToken).ConfigureAwait(false);
            if (key is null)
            {
                LogNotOpened(ProofOfWorkKey.Id);
                return;
            }

            _runtime.UseKey(key);
            LogReady(metadata.Version);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "The proof-of-work key is ready (version {Version}); protected routes can challenge.")]
    private partial void LogReady(int version);

    [LoggerMessage(Level = LogLevel.Error, Message = "The proof-of-work secret is stored with {Algorithm}, not HMAC-SHA-256; proof-of-work-protected routes answer 503. Replace the secret with a 32-byte HMAC-SHA-256 key.")]
    private partial void LogWrongAlgorithm(MacAlgorithm? algorithm);

    [LoggerMessage(Level = LogLevel.Error, Message = "The proof-of-work secret {Secret} could not be opened as an HMAC key; proof-of-work-protected routes answer 503.")]
    private partial void LogNotOpened(SecretId secret);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't ensure the proof-of-work HMAC key; proof-of-work-protected routes answer 503.")]
    private partial void LogFailed(Exception exception);
}
