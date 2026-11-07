using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Vault.Secrets;

/// <summary>A secret opened inside the vault: its row and the plaintext of its value (and, if asked, of the version before). Dispose to zero them.</summary>
internal sealed class UnsealedSecret(SecretRow row, SecretBuffer current, SecretBuffer? previous) : IDisposable
{
    public SecretRow Row { get; } = row;

    public SecretBuffer Current { get; } = current;

    /// <summary>The previous value, only if it was asked for and its grace window has not ended.</summary>
    public SecretBuffer? Previous { get; } = previous;

    public void Dispose()
    {
        Current.Dispose();
        Previous?.Dispose();
    }
}

/// <summary>
/// The one place a secret's row becomes plaintext. The plaintext it returns goes to the vault's own capabilities
/// (the authenticating handler, the MAC cache) and nowhere else: no public type has a way to ask for it.
/// </summary>
internal sealed class SecretReader(VaultRuntime runtime)
{
    /// <summary>The metadata of a row (never its value).</summary>
    public static SecretMetadata ToMetadata(SecretRow row) =>
        new(row.Id, row.Kind, row.Label, row.MacAlgorithm, [.. row.AllowedHosts], row.CreatedAt, row.UpdatedAt, row.Version);

    /// <summary>
    /// What a secret's sealed value is bound to: its ID and version, and in the purpose its kind, MAC algorithm and allowed
    /// hosts. The metadata that decides what the secret may be used for is thereby authenticated by the AAD: editing it in the
    /// database (widening the hosts of a DNS token, switching a MAC key's algorithm) makes the value fail to open. The
    /// label is not bound; it decides nothing. A change of hosts is made by replacing the value, which seals again.
    /// </summary>
    public static SealContext ContextOf(SecretRow row, int version) =>
        new(row.Id.ToString(), SecretKinds.Purpose(row.Kind, row.MacAlgorithm, row.AllowedHosts), version);

    /// <summary>Opens the current value of a secret, or returns null if there is no such secret.</summary>
    public async ValueTask<UnsealedSecret?> OpenAsync(SecretId id, bool includePrevious, CancellationToken cancellationToken)
    {
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var row = await context.Secrets.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
            return row is null ? null : Unseal(row, includePrevious);
        }
    }

    /// <summary>The synchronous twin of <see cref="OpenAsync"/>, for the MAC cache's refresh (a rare read on a request path).</summary>
    public UnsealedSecret? Open(SecretId id, bool includePrevious)
    {
        using var gate = runtime.Enter();
        using var context = runtime.CreateContext();
        var row = context.Secrets.AsNoTracking().SingleOrDefault(s => s.Id == id);
        return row is null ? null : Unseal(row, includePrevious);
    }

    /// <summary>Like <see cref="Open"/> but never waits for the gate: <paramref name="busy"/> is true (and the result null) if it is held.</summary>
    public UnsealedSecret? OpenWithoutWaiting(SecretId id, bool includePrevious, out bool busy)
    {
        busy = !runtime.TryEnter(out var gate);
        if (busy)
        {
            return null;
        }

        using (gate)
        {
            using var context = runtime.CreateContext();
            var row = context.Secrets.AsNoTracking().SingleOrDefault(s => s.Id == id);
            return row is null ? null : Unseal(row, includePrevious);
        }
    }

    private UnsealedSecret Unseal(SecretRow row, bool includePrevious)
    {
        var kek = runtime.Kek;
        var current = EnvelopeCipher.Open(kek, row.Sealed, ContextOf(row, row.Version));
        SecretBuffer? previous = null;
        try
        {
            if (includePrevious
                && row.PreviousSealed is { } previousSealed
                && row.PreviousVersion is { } previousVersion
                && row.PreviousRetiredAt is { } retiredAt
                && runtime.Time.GetUtcNow() < retiredAt + runtime.Policy.MacGraceWindow)
            {
                previous = EnvelopeCipher.Open(kek, previousSealed, ContextOf(row, previousVersion));
            }

            return new UnsealedSecret(row, current, previous);
        }
        catch
        {
            current.Dispose();
            previous?.Dispose();
            throw;
        }
    }
}
