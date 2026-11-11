using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Vault.Passwords;

/// <summary>
/// <see cref="IPasswordCredentials"/>: each credential's verifier sealed in <c>vault.db</c> like any other secret and kept, parsed,
/// in the <see cref="PasswordRegistry"/> that answers <see cref="IPasswordVerifier"/>. The registry is loaded when first needed;
/// nothing outside the vault can reach a hash or a cache entry.
/// </summary>
/// <remarks>
/// <para><b>One gate.</b> Writes take the vault's gate (so a KEK rotation can't interleave) and seal inside the KEK-check transaction,
/// then replace the slot's state; a reader of the registry takes no lock.</para>
/// <para><b>Taking a change back.</b> Every <see cref="SetAsync"/> keeps, in memory until it is kept or reverted, what it replaced: the
/// previous sealed row as it was (its bytes, version and times), or the fact that there was none, and the slot's previous state.
/// <see cref="RevertAsync"/> writes exactly that back inside the gate, so a caller whose commit failed after the vault write leaves the
/// vault as it found it. A password never goes into what is kept: only sealed bytes and the parsed verifier the registry already held.</para>
/// </remarks>
internal sealed partial class PasswordCredentialStore(VaultRuntime runtime, PasswordRegistry registry, ILogger<PasswordCredentialStore> logger) : IPasswordCredentials
{
    // The AAD purpose: a row sealed for something else (or edited into this table) doesn't open as a password verifier.
    private const string Purpose = "password-credential";

    private readonly object _loading = new();
    private readonly ConcurrentDictionary<long, Undo> _undo = new();
    private long _nextTicket;
    private volatile bool _loaded;

    /// <summary>What a row is sealed to: its typed ID and the purpose, with its version in the AAD.</summary>
    public static SealContext ContextOf(PasswordCredentialRow row) => new(row.Id.ToString(), Purpose, row.Version);

    public IPasswordCredential Open(PasswordCredentialId id)
    {
        EnsureLoaded();
        return registry.Open(id);
    }

    public async ValueTask<PasswordChange> SetAsync(PasswordCredentialId id, ReadOnlyMemory<char> password, CancellationToken cancellationToken)
    {
        RequireId(id);

        // Hashed before the gate: tens of milliseconds that nothing else waits behind.
        var verifier = HashOf(password);
        var written = await WriteAsync(id, verifier, onlyIfAbsent: false, cancellationToken).ConfigureAwait(false);
        LogSet(logger, id);
        var ticket = Interlocked.Increment(ref _nextTicket);
        _undo[ticket] = written.Undo!;
        return new PasswordChange(ticket);
    }

    public async ValueTask RevertAsync(IReadOnlyList<PasswordChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        List<Undo> undos = [];
        for (var i = changes.Count - 1; i >= 0; i--)
        {
            if (_undo.TryGetValue(changes[i].Ticket, out var undo))
            {
                undos.Add(undo);
            }
        }

        if (undos.Count == 0)
        {
            return;
        }

        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        InvalidOperationException? failure = null;
        foreach (var undo in undos)
        {
            try
            {
                await RestoreAsync(undo, cancellationToken).ConfigureAwait(false);
                LogReverted(logger, undo.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The others still go back; the caller is told which couldn't, by a failure naming only IDs.
                failure ??= new InvalidOperationException($"Couldn't put password credential {undo.Id} back as it was.", ex);
            }
        }

        foreach (var change in changes)
        {
            _undo.TryRemove(change.Ticket, out _);
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    public void Keep(IReadOnlyList<PasswordChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var change in changes)
        {
            _undo.TryRemove(change.Ticket, out _);
        }
    }

    public async ValueTask<IPasswordCredential> PrepareAsync(PasswordCredentialId id, ReadOnlyMemory<char> password, CancellationToken cancellationToken)
    {
        RequireId(id);
        var verifier = await Task.Run(() => HashOf(password), cancellationToken).ConfigureAwait(false);
        return PrepareImport(id, verifier);
    }

    public IPasswordCredential PrepareImport(PasswordCredentialId id, string passwordVerifier)
    {
        RequireId(id);
        ArgumentNullException.ThrowIfNull(passwordVerifier);
        return PasswordHash.TryParse(passwordVerifier, out var parsed)
            ? new PasswordCredentialSlot(id) { State = parsed }
            : throw new ArgumentException("Not a PBKDF2 verifier this build reads.", nameof(passwordVerifier));
    }

    public async ValueTask<bool> ImportAsync(PasswordCredentialId id, string passwordVerifier, CancellationToken cancellationToken)
    {
        RequireId(id);
        ArgumentNullException.ThrowIfNull(passwordVerifier);
        var written = await WriteAsync(id, passwordVerifier, onlyIfAbsent: true, cancellationToken).ConfigureAwait(false);
        if (written.Written)
        {
            LogImported(logger, id);
        }

        return written.Written;
    }

    public async ValueTask<bool> DeleteAsync(PasswordCredentialId id, CancellationToken cancellationToken)
    {
        RequireId(id);
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var deleted = await context.PasswordCredentials.Where(r => r.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
            registry.Clear(id);
            if (deleted)
            {
                LogDeleted(logger, id);
            }

            return deleted;
        }
    }

    public async ValueTask<IReadOnlyList<PasswordCredentialId>> ListAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return registry.SetIds();
    }

    /// <summary>Re-wraps every verifier under <paramref name="newKek"/>; the caller owns the transaction (see <see cref="KekRotation"/>).</summary>
    public static async Task<int> RewrapAsync(VaultDbContext context, Kek old, Kek newKek, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var row in await context.PasswordCredentials.ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            row.Sealed = EnvelopeCipher.Rewrap(old, newKek, row.Sealed, ContextOf(row));
            count++;
        }

        return count;
    }

    // The password's UTF-8 bytes are in a buffer that is zeroed as soon as it is hashed.
    private static string HashOf(ReadOnlyMemory<char> password)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(password.Span)];
        try
        {
            Encoding.UTF8.GetBytes(password.Span, bytes);
            return PasswordHash.Create(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    // Writes the row back as it was (or deletes the one a first set made), in the KEK-check transaction like every seal; the call holds the gate.
    private async ValueTask RestoreAsync(Undo undo, CancellationToken cancellationToken)
    {
        if (undo.Sealed is { } previous && !EnvelopeCipher.KekIdOf(previous).SequenceEqual(runtime.Kek.Id))
        {
            throw new InvalidOperationException("The master key changed since the password was set, so the old row can't be put back.");
        }

        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var transaction = await runtime.BeginSealingAsync(context, cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                var row = await context.PasswordCredentials.SingleOrDefaultAsync(r => r.Id == undo.Id, cancellationToken).ConfigureAwait(false);
                if (undo.Sealed is null)
                {
                    if (row is not null)
                    {
                        context.PasswordCredentials.Remove(row);
                    }
                }
                else if (row is null)
                {
                    context.PasswordCredentials.Add(new PasswordCredentialRow
                    {
                        Id = undo.Id,
                        Version = undo.Version!.Value,
                        CreatedAt = undo.CreatedAt!.Value,
                        UpdatedAt = undo.UpdatedAt!.Value,
                        Sealed = undo.Sealed,
                    });
                }
                else
                {
                    row.Version = undo.Version!.Value;
                    row.UpdatedAt = undo.UpdatedAt!.Value;
                    row.Sealed = undo.Sealed;
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (undo.State is { } state)
        {
            registry.Put(undo.Id, state);
        }
        else
        {
            registry.Clear(undo.Id);
        }
    }

    /// <summary>What a set replaced: the row as it was (null fields: there was none) and the verifier the slot held.</summary>
    private sealed record Undo(PasswordCredentialId Id, int? Version, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt, byte[]? Sealed, ParsedPasswordHash? State);

    private static void RequireId(PasswordCredentialId id)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A credential ID can't be empty.", nameof(id));
        }
    }

    private async ValueTask<(bool Written, Undo? Undo)> WriteAsync(PasswordCredentialId id, string verifier, bool onlyIfAbsent, CancellationToken cancellationToken)
    {
        if (!PasswordHash.TryParse(verifier, out var parsed))
        {
            throw new ArgumentException("Not a PBKDF2 verifier this build reads.", nameof(verifier));
        }

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var now = runtime.Time.GetUtcNow();
        var bytes = Encoding.UTF8.GetBytes(verifier);
        Undo? undo = null;
        try
        {
            var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var transaction = await runtime.BeginSealingAsync(context, cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    // An existing row that didn't load (see Load) is replaced, not kept: importing is how it gets repaired.
                    if (onlyIfAbsent && registry.IsSet(id))
                    {
                        return (false, null);
                    }

                    var row = await context.PasswordCredentials.SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
                    undo = row is null
                        ? new Undo(id, null, null, null, null, registry.Current(id))
                        : new Undo(id, row.Version, row.CreatedAt, row.UpdatedAt, [.. row.Sealed], registry.Current(id));
                    if (row is null)
                    {
                        row = new PasswordCredentialRow { Id = id, Version = 1, CreatedAt = now };
                        context.PasswordCredentials.Add(row);
                    }
                    else
                    {
                        row.Version++;
                    }

                    row.UpdatedAt = now;
                    row.Sealed = EnvelopeCipher.Seal(runtime.Kek, bytes, ContextOf(row));
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            registry.Put(id, parsed);
            return (true, undo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async ValueTask EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        Load();
    }

    // The synchronous twin, for Open: the snapshot compiler is synchronous, and the load runs once. It never starts the vault itself
    // (that is asynchronous, and blocking a thread on it risks a deadlock): the host initializes the vault at startup, long before a compile.
    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        if (!runtime.IsStarted)
        {
            throw new InvalidOperationException(
                "The vault is not initialized: await IVaultLifecycle.InitializeAsync (the host does at startup) before opening a password credential.");
        }

        using var gate = runtime.Enter();
        Load();
    }

    private void Load()
    {
        lock (_loading)
        {
            if (_loaded)
            {
                return;
            }

            using var context = runtime.CreateContext();
            foreach (var row in context.PasswordCredentials.AsNoTracking().ToList())
            {
                try
                {
                    using var verifier = EnvelopeCipher.Open(runtime.Kek, row.Sealed, ContextOf(row));
                    if (PasswordHash.TryParse(Encoding.UTF8.GetString(verifier.Span), out var parsed))
                    {
                        registry.Put(row.Id, parsed);
                    }
                    else
                    {
                        LogUnreadable(logger, row.Id, null);
                    }
                }
                catch (CryptographicException ex)
                {
                    // One damaged row must not stop the others; its user can't sign in until a password is set again.
                    LogUnreadable(logger, row.Id, ex);
                }
            }

            _loaded = true;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Password credential {CredentialId} set.")]
    private static partial void LogSet(ILogger logger, PasswordCredentialId credentialId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password credential {CredentialId} imported from an existing verifier.")]
    private static partial void LogImported(ILogger logger, PasswordCredentialId credentialId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password credential {CredentialId} put back as it was before a failed change.")]
    private static partial void LogReverted(ILogger logger, PasswordCredentialId credentialId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password credential {CredentialId} deleted.")]
    private static partial void LogDeleted(ILogger logger, PasswordCredentialId credentialId);

    [LoggerMessage(Level = LogLevel.Error, Message = "The password credential {CredentialId} can't be opened, so its user can't sign in until a password is set again; the other credentials still load.")]
    private static partial void LogUnreadable(ILogger logger, PasswordCredentialId credentialId, Exception? exception);
}
