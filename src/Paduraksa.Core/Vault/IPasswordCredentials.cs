using Paduraksa.Core.Model;

namespace Paduraksa.Core.Vault;

/// <summary>
/// A basic-auth user's password as the vault keeps it: a handle you can verify against and can't read. It is a reference to
/// the credential, not to one password: it stays valid across <see cref="IPasswordCredentials.SetAsync"/> and
/// <see cref="IPasswordCredentials.DeleteAsync"/>, which is what lets a compiled snapshot hold it while the password changes
/// underneath. Cheap, thread-safe and owned by the vault.
/// </summary>
public interface IPasswordCredential
{
    PasswordCredentialId Id { get; }

    /// <summary>Whether the vault holds a password for it now. A user whose credential is not set cannot sign in.</summary>
    bool IsSet { get; }
}

/// <summary>
/// Sets, imports and deletes the passwords of access-list users. Nothing here returns a password or a hash: the config
/// document holds a <see cref="PasswordCredentialId"/>, and the verifier (<see cref="IPasswordVerifier"/>) is the only way
/// a password is ever checked.
/// </summary>
public interface IPasswordCredentials
{
    /// <summary>
    /// The handle for <paramref name="id"/>, whether or not a password is set (then <see cref="IPasswordCredential.IsSet"/> is
    /// false, and becomes true if one is set later). Synchronous: the snapshot compiler is, and the vault holds every credential
    /// in memory.
    /// </summary>
    IPasswordCredential Open(PasswordCredentialId id);

    /// <summary>Hashes <paramref name="password"/> (PBKDF2-HMAC-SHA-256, a fresh random salt) and keeps only that, sealed like every
    /// other vault secret; replaces the credential's password if there is one.</summary>
    /// <exception cref="ArgumentException">The password is empty or longer than <see cref="AccessListLimits.MaxPasswordBytes"/> bytes in UTF-8.</exception>
    ValueTask<PasswordChange> SetAsync(PasswordCredentialId id, ReadOnlyMemory<char> password, CancellationToken cancellationToken);

    /// <summary>
    /// Puts every credential <paramref name="changes"/> names back as it was before the change (its sealed row exactly, or no row at
    /// all), last change first, inside the vault's gate: the way a caller whose own commit failed takes back the passwords it set. A
    /// change already kept or reverted is skipped.
    /// </summary>
    /// <exception cref="InvalidOperationException">A credential couldn't be put back (the master key changed since, say); the
    /// ones that could be have been.</exception>
    ValueTask RevertAsync(IReadOnlyList<PasswordChange> changes, CancellationToken cancellationToken);

    /// <summary>Lets <paramref name="changes"/> stand and forgets what it took to undo them. Call it when the commit they belong to
    /// succeeded; a change that is neither kept nor reverted is held in memory.</summary>
    void Keep(IReadOnlyList<PasswordChange> changes);

    /// <summary>
    /// Hashes <paramref name="password"/> into a handle that exists for the caller alone: nothing is stored, <see cref="Open"/> never
    /// returns it and no verification cache sees it. For compiling a draft whose passwords the vault does not hold (yet), so the
    /// request simulator can try them.
    /// </summary>
    /// <exception cref="ArgumentException">The password is empty or longer than <see cref="AccessListLimits.MaxPasswordBytes"/> bytes in UTF-8.</exception>
    ValueTask<IPasswordCredential> PrepareAsync(PasswordCredentialId id, ReadOnlyMemory<char> password, CancellationToken cancellationToken);

    /// <summary><see cref="PrepareAsync"/> for an existing verifier (the PHC string), as <see cref="ImportAsync"/> would keep it.</summary>
    /// <exception cref="ArgumentException">Not a verifier this build reads.</exception>
    IPasswordCredential PrepareImport(PasswordCredentialId id, string passwordVerifier);

    /// <summary>
    /// Keeps an existing PBKDF2 verifier as it is (the PHC string <c>$pbkdf2-sha256$i=210000$salt$hash</c>, no re-hash), for the
    /// documents an earlier build wrote. False, with nothing changed, when the credential already has a password.
    /// </summary>
    /// <exception cref="ArgumentException">Not a verifier this build reads.</exception>
    ValueTask<bool> ImportAsync(PasswordCredentialId id, string passwordVerifier, CancellationToken cancellationToken);

    /// <summary>Forgets the password. False when there was none. Handles stay valid and report <see cref="IPasswordCredential.IsSet"/> false.</summary>
    ValueTask<bool> DeleteAsync(PasswordCredentialId id, CancellationToken cancellationToken);

    /// <summary>The credentials that have a password.</summary>
    ValueTask<IReadOnlyList<PasswordCredentialId>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>A password set by <see cref="IPasswordCredentials.SetAsync"/>, which can be kept or reverted. It names the change and
/// nothing about the password.</summary>
/// <param name="Ticket">The vault's number for the change.</param>
public readonly record struct PasswordChange(long Ticket);
