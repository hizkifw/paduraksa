using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Vault;

/// <summary>
/// The vault's shared state and startup (<see cref="IVaultLifecycle"/>): the KEK, the database and the one gate that keeps
/// a KEK rotation from interleaving with anything that seals or unseals. Every capability goes through
/// <see cref="EnsureReadyAsync"/> first, so none can run before the KEK is resolved and verified.
/// </summary>
/// <remarks>
/// <b>The gate.</b> One <see cref="SemaphoreSlim"/> serialises the operations that handle sealed values (put, replace,
/// delete, unseal, rotate). The workload is admin edits and a DNS call per ACME challenge, and the MAC cache refreshes at
/// most every <see cref="VaultPolicy.MacCacheMaxAge"/>, so contention is nil, and in exchange nothing ever unseals a row under a
/// KEK that a rotation has just replaced.
/// </remarks>
internal sealed partial class VaultRuntime(
    IDbContextFactory<VaultDbContext> contexts, VaultOptions options, KekProvider keks, ILogger<VaultRuntime> logger)
    : IVaultLifecycle, IDisposable
{
    private readonly SemaphoreSlim _initialization = new(1, 1);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile Started? _started;

    /// <summary>Whether <see cref="InitializeAsync"/> has completed: the KEK is resolved and the database is current.</summary>
    public bool IsStarted => _started is not null;

    public TimeProvider Time => options.Time;

    public VaultPolicy Policy => options.Policy;

    public string DatabasePath => Path.Combine(options.DataDirectory, VaultDatabase.FileName);

    /// <summary>The terminal handler factory (the transport seam).</summary>
    public Func<SecretHttpTransportOptions, HttpMessageHandler> CreateTerminalHandler => options.CreateTerminalHandler;

    /// <summary>The KEK in force. Only valid after <see cref="EnsureReadyAsync"/>; read it inside the gate.</summary>
    public Kek Kek => (_started ?? throw NotStarted()).Kek;

    public VaultDbContext CreateContext() => contexts.CreateDbContext();

    public ValueTask<VaultDbContext> CreateContextAsync(CancellationToken cancellationToken) =>
        new(contexts.CreateDbContextAsync(cancellationToken));

    public async ValueTask<VaultStatus> InitializeAsync(CancellationToken cancellationToken)
    {
        if (_started is { } existing)
        {
            return existing.Status;
        }

        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started is { } raced)
            {
                return raced.Status;
            }

            Directory.CreateDirectory(options.DataDirectory);
            var resolved = keks.Resolve(options.GetVariable, options.DataDirectory, DatabasePath);
            try
            {
                var initialized = await VaultDatabase.InitializeAsync(contexts, DatabasePath, resolved.Kek, options.Time, cancellationToken)
                    .ConfigureAwait(false);
                var status = new VaultStatus(resolved.Source, resolved.BesideDatabase);
                await PurgeExpiredPreviousAsync(cancellationToken).ConfigureAwait(false);
                _started = new Started(resolved.Kek, status);
                LogStarted(status.KekSource, DatabasePath, initialized.AppliedMigrations.Count);
                if (status.KekBesideDatabase)
                {
                    LogKekBesideDatabase();
                }

                return status;
            }
            catch (InvalidOperationException ex) when (resolved.Source == KekSourceKind.GeneratedFile && ex.Data[VaultDatabase.StoredCheckKey] is byte[] stored)
            {
                resolved.Kek.Dispose();
                var backup = KekProvider.FindMatchingBackup(options.DataDirectory, stored);
                throw backup is null
                    ? ex
                    : new InvalidOperationException(
                        $"{ex.Message} {backup} in the data directory holds the key this database was written under: if a rotation was interrupted, put it back as {KekProvider.GeneratedFileName}.", ex);
            }
            catch
            {
                resolved.Kek.Dispose();
                throw;
            }
        }
        finally
        {
            _initialization.Release();
        }
    }

    /// <summary>
    /// Starts the transaction a seal happens in and checks, inside it, that the database still belongs to the KEK this process
    /// holds. A <c>vault rotate-kek</c> run from another process while this one serves would otherwise leave it sealing new
    /// values under the old key into a database that now checks against the new one. Inside a transaction the check can't be
    /// overtaken either: a rotation that commits after it makes this transaction's write fail instead. Call inside the gate.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database is under another KEK: this process must be restarted with the current one.</exception>
    public async ValueTask<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginSealingAsync(VaultDbContext context, CancellationToken cancellationToken)
    {
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var meta = await context.Meta.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
            if (!Kek.Matches(meta.KekCheck))
            {
                throw new InvalidOperationException(
                    "The master key changed under this process (a rotation ran elsewhere): restart it with the current key. Nothing was written.");
            }

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs <see cref="InitializeAsync"/> if nothing has (it is a no-op once done).</summary>
    public async ValueTask EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_started is null)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<GateLease> EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new GateLease(_gate);
    }

    /// <summary>The synchronous gate, for the MAC cache's refresh on a (rare) stale read.</summary>
    public GateLease Enter()
    {
        _gate.Wait();
        return new GateLease(_gate);
    }

    /// <summary>Takes the gate only if it is free right now (false means a rotation, or another operation, holds it).</summary>
    public bool TryEnter(out GateLease lease)
    {
        if (_gate.Wait(0))
        {
            lease = new GateLease(_gate);
            return true;
        }

        lease = default;
        return false;
    }

    /// <summary>
    /// Puts <paramref name="newKek"/> in force after a rotation has committed. Call inside the gate. The old KEK is
    /// zeroed, so nothing can wrap or unwrap under it again.
    /// </summary>
    public void ReplaceKek(Kek newKek)
    {
        var current = _started ?? throw NotStarted();
        _started = current with { Kek = newKek };
        current.Kek.Dispose();
    }

    /// <summary>Deletes the previous value of every MAC secret whose grace window has ended.</summary>
    public async Task PurgeExpiredPreviousAsync(CancellationToken cancellationToken)
    {
        var cutoff = options.Time.GetUtcNow() - options.Policy.MacGraceWindow;
        var context = await CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.Secrets
                .Where(s => s.PreviousRetiredAt != null && s.PreviousRetiredAt <= cutoff)
                .ExecuteUpdateAsync(
                    u => u.SetProperty(s => s.PreviousSealed, (byte[]?)null)
                        .SetProperty(s => s.PreviousVersion, (int?)null)
                        .SetProperty(s => s.PreviousRetiredAt, (DateTimeOffset?)null),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>The synchronous twin of <see cref="PurgeExpiredPreviousAsync"/>, for the MAC cache. A failure is logged and ignored: it must not fail a MAC operation, and the next refresh tries again.</summary>
    public void PurgeExpiredPrevious()
    {
        try
        {
            var cutoff = options.Time.GetUtcNow() - options.Policy.MacGraceWindow;
            using var context = CreateContext();
            context.Secrets
                .Where(s => s.PreviousRetiredAt != null && s.PreviousRetiredAt <= cutoff)
                .ExecuteUpdate(
                    u => u.SetProperty(s => s.PreviousSealed, (byte[]?)null)
                        .SetProperty(s => s.PreviousVersion, (int?)null)
                        .SetProperty(s => s.PreviousRetiredAt, (DateTimeOffset?)null));
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            LogPurgeFailed(ex);
        }
    }

    public void Dispose()
    {
        _started?.Kek.Dispose();
        _initialization.Dispose();
        _gate.Dispose();
    }

    private static InvalidOperationException NotStarted() =>
        new("The vault is not initialized: call IVaultLifecycle.InitializeAsync (every capability does it for you on first use).");

    [LoggerMessage(Level = LogLevel.Information, Message = "Vault ready: master key from {Source}, database {Path}, {Migrations} migration(s) applied.")]
    private partial void LogStarted(KekSourceKind source, string path, int migrations);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The master key file sits next to vault.db, so a copy of the data directory is a copy of both. Move it elsewhere (PADURAKSA_MASTER_KEY_FILE) or supply it through PADURAKSA_MASTER_KEY.")]
    private partial void LogKekBesideDatabase();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete a MAC key's expired previous value; the next refresh tries again.")]
    private partial void LogPurgeFailed(Exception exception);

    private sealed record Started(Kek Kek, VaultStatus Status);
}

/// <summary>Releases <see cref="VaultRuntime"/>'s gate when disposed.</summary>
internal readonly struct GateLease(SemaphoreSlim gate) : IDisposable
{
    public void Dispose() => gate.Release();
}
