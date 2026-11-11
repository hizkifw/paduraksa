// Linked into the test projects that need password credentials without a vault database, with the vault's registry (TestPasswords.props).
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Passwords;

namespace Paduraksa.Tests.Shared;

/// <summary>
/// <see cref="IPasswordCredentials"/> over the vault's own in-memory registry: the real hash, handle and cache, none of the
/// database. Hashes are made at the lowest accepted cost, so a test that sets a dozen passwords stays quick.
/// </summary>
internal sealed class TestPasswords : IPasswordCredentials
{
    public TestPasswords(TimeProvider? time = null)
    {
        Registry = new PasswordRegistry(time ?? TimeProvider.System);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (PasswordCredentialId Id, ParsedPasswordHash? State)> _undo = new();
    private long _tickets;

    public PasswordRegistry Registry { get; }

    public IPasswordVerifier Verifier => Registry;

    /// <summary>A handle with a password that no registry owns: fixtures made once and shared by many tests, each with a verifier
    /// (and so a cache) of its own.</summary>
    public static IPasswordCredential Credential(string? password, PasswordCredentialId id = default)
    {
        PasswordCredentialSlot slot = new(id.IsEmpty ? PasswordCredentialId.New() : id);
        if (password is not null)
        {
            slot.State = Parse(PasswordHash.Create(password, PasswordHash.MinIterations));
        }

        return slot;
    }

    /// <summary>What the vault does when a password changes: the same handle, a new verifier (a new salt).</summary>
    public static void Change(IPasswordCredential credential, string? password) =>
        ((PasswordCredentialSlot)credential).State = password is null ? null : Parse(Hash(password));

    public static string Hash(string password) => PasswordHash.Create(password, PasswordHash.MinIterations);

    public IPasswordCredential Open(PasswordCredentialId id) => Registry.Open(id);

    // The vault's own guard (PasswordHash.Create) refuses what the real one refuses, with its words.
    public ValueTask<PasswordChange> SetAsync(PasswordCredentialId id, ReadOnlyMemory<char> password, CancellationToken cancellationToken)
    {
        var parsed = Parse(Hash(password.ToString()));
        var ticket = Interlocked.Increment(ref _tickets);
        _undo[ticket] = (id, Registry.Current(id));
        Registry.Put(id, parsed);
        return ValueTask.FromResult(new PasswordChange(ticket));
    }

    /// <summary>Set to make <see cref="RevertAsync"/> fail, as a vault that can't put a password back does.</summary>
    public Exception? RevertFailure { get; set; }

    public ValueTask RevertAsync(IReadOnlyList<PasswordChange> changes, CancellationToken cancellationToken)
    {
        if (RevertFailure is { } failure)
        {
            throw failure;
        }

        foreach (var change in changes.Reverse())
        {
            if (_undo.TryRemove(change.Ticket, out var undo))
            {
                if (undo.State is { } state)
                {
                    Registry.Put(undo.Id, state);
                }
                else
                {
                    Registry.Clear(undo.Id);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public void Keep(IReadOnlyList<PasswordChange> changes)
    {
        foreach (var change in changes)
        {
            _undo.TryRemove(change.Ticket, out _);
        }
    }

    public ValueTask<IPasswordCredential> PrepareAsync(PasswordCredentialId id, ReadOnlyMemory<char> password, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IPasswordCredential>(new PasswordCredentialSlot(id) { State = Parse(Hash(password.ToString())) });

    public IPasswordCredential PrepareImport(PasswordCredentialId id, string passwordVerifier) =>
        PasswordHash.TryParse(passwordVerifier, out var parsed)
            ? new PasswordCredentialSlot(id) { State = parsed }
            : throw new ArgumentException("Not a PBKDF2 verifier this build reads.", nameof(passwordVerifier));

    public ValueTask<bool> ImportAsync(PasswordCredentialId id, string passwordVerifier, CancellationToken cancellationToken)
    {
        if (!PasswordHash.TryParse(passwordVerifier, out var parsed))
        {
            throw new ArgumentException("Not a PBKDF2 verifier this build reads.", nameof(passwordVerifier));
        }

        if (Registry.IsSet(id))
        {
            return ValueTask.FromResult(false);
        }

        Registry.Put(id, parsed);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> DeleteAsync(PasswordCredentialId id, CancellationToken cancellationToken)
    {
        var was = Registry.IsSet(id);
        Registry.Clear(id);
        return ValueTask.FromResult(was);
    }

    public ValueTask<IReadOnlyList<PasswordCredentialId>> ListAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Registry.SetIds());

    private static ParsedPasswordHash Parse(string verifier) =>
        PasswordHash.TryParse(verifier, out var parsed) ? parsed : throw new InvalidOperationException("The test verifier didn't parse.");
}
