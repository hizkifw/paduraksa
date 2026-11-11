using Microsoft.Extensions.Logging;

using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Application.Commands;

/// <summary>
/// Where a plain password goes: into the vault, and what the document keeps is the ID of the credential. A user written with a
/// <see cref="AccessListUserConfig.Password"/> gets a <see cref="PasswordCredentialId"/> (the one the list already has for that
/// name, else the one sent, else a new one) and the password is dropped from the command before anything validates, stores or
/// audits it; one written with neither a password nor an ID keeps the credential the list has for that name, so editing a list
/// doesn't ask for every password again. A password that can't be used (empty, or longer than the gate reads) is left in place for
/// the validator to refuse, which keeps it out of every document.
/// </summary>
/// <remarks>
/// <para><b>Nothing reaches the vault until the command is certain.</b> <c>Prepare</c> only collects the passwords; the
/// pipeline sets them once validation, compilation and the guards have passed and the command isn't a dry run or a simulation, so a
/// refused or previewed command changes no password. They are set <i>before</i> the revision commits, because the snapshot that
/// publishes needs them live: if the commit then fails, the password is changed and the config is not (the revision is what is
/// atomic, the vault is not part of its transaction).</para>
/// <para><b>A password is a change.</b> The document doesn't hold it, so a command that changes only a password would diff to
/// nothing; <see cref="WithPasswordChanges"/> names the list as modified (field <c>users</c>), which commits a revision, moves the
/// list's version (an editor holding the old one conflicts) and is audited like any change to the list. The revision records
/// that the list changed, never the password.</para>
/// <para><b>Lifecycle.</b> A credential lives as long as the live config names it: <see cref="DeleteUnreferencedAsync"/> deletes what
/// a committed change stopped naming. Revisions aren't consulted. Keeping a credential for as long as any revision names it would
/// keep a removed user's password for as long as the history lasts, and a rollback would bring that user back with it; a rollback
/// restores the user, and the password is whatever the vault holds then, or none (a warning).</para>
/// </remarks>
internal static partial class AccessListPasswords
{
    /// <summary>A password to set once the command is certain. Its text is in memory for the command's duration and nowhere else.</summary>
    internal readonly record struct PendingPassword(PasswordCredentialId Id, string Password)
    {
        public override string ToString() => $"PendingPassword {{ Id = {Id} }}";
    }

    internal sealed record Prepared(ConfigCommand Command, IReadOnlyList<PendingPassword> Passwords)
    {
        public IReadOnlySet<PasswordCredentialId> Ids { get; } = new HashSet<PasswordCredentialId>(Passwords.Select(static p => p.Id));
    }

    public static Prepared Prepare(ConfigCommand command, ConfigDocument current)
    {
        List<PendingPassword> pending = [];
        ConfigCommand prepared = command switch
        {
            UpsertAccessListCommand { AccessList: { } list } upsert => upsert with { AccessList = Prepare(list, Existing(current, list), pending) },
            ReplaceConfigCommand { Document: { } document } replace => replace with
            {
                Document = document with
                {
                    AccessLists = [.. (document.AccessLists ?? []).Select(l => l is null ? l! : Prepare(l, Existing(current, l), pending))],
                },
            },
            _ => command,
        };
        return new Prepared(prepared, pending);
    }

    /// <summary><paramref name="changes"/> plus, for every list that has a user with a new password and isn't already reported as
    /// changed, that list as modified.</summary>
    public static IReadOnlyList<ConfigChange> WithPasswordChanges(IReadOnlyList<ConfigChange> changes, ConfigDocument arranged, Prepared prepared)
    {
        if (prepared.Passwords.Count == 0)
        {
            return changes;
        }

        List<ConfigChange> all = [.. changes];
        var lists = arranged.AccessLists ?? [];
        for (var i = 0; i < lists.Count; i++)
        {
            var list = lists[i];
            if (list?.BasicAuth?.Users is not { } users
                || !users.Any(u => u?.CredentialId is { } id && prepared.Ids.Contains(id))
                || all.OfType<AccessListChange>().Any(c => c.AccessListId == list.Id))
            {
                continue;
            }

            var path = $"/accessLists/{i}";
            all.Add(new AccessListChange(ConfigChangeKind.Modified, list.Id, path, [$"{path}/basicAuth/users"]));
        }

        return all;
    }

    /// <summary>The credentials <paramref name="document"/> names.</summary>
    public static HashSet<PasswordCredentialId> Referenced(ConfigDocument document) =>
    [
        .. (document.AccessLists ?? [])
            .SelectMany(static l => l?.BasicAuth?.Users ?? [])
            .Select(static u => u?.CredentialId)
            .OfType<PasswordCredentialId>()
            .Where(static id => !id.IsEmpty),
    ];

    /// <summary>
    /// Sets the passwords of a command that is about to commit and returns what it takes to take them back. If one fails the ones
    /// already set are put back before the failure leaves.
    /// </summary>
    public static async Task<IReadOnlyList<PasswordChange>> SetAsync(
        IPasswordCredentials credentials, IReadOnlyList<PendingPassword> passwords, CancellationToken cancellationToken)
    {
        List<PasswordChange> changes = [];
        try
        {
            foreach (var (id, password) in passwords)
            {
                changes.Add(await credentials.SetAsync(id, password.AsMemory(), cancellationToken).ConfigureAwait(false));
            }
        }
        catch
        {
            await credentials.RevertAsync(changes, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return changes;
    }

    /// <summary>
    /// The handles a compile that will never commit (a dry run, a draft) uses in place of the vault's own: each pending password hashed
    /// into a handle for that compile alone, and each verifier an earlier build kept parsed into one, so the request simulator verifies
    /// the draft's passwords as applying it would, and nothing reaches the vault or its cache.
    /// </summary>
    public static async Task<PendingPasswords> OverlayAsync(
        IPasswordCredentials credentials, IReadOnlyList<PendingPassword> passwords, IReadOnlyList<LegacyCredential> legacy, CancellationToken cancellationToken)
    {
        if (passwords.Count == 0 && legacy.Count == 0)
        {
            return PendingPasswords.None;
        }

        Dictionary<PasswordCredentialId, IPasswordCredential> overlay = [];
        foreach (var credential in legacy)
        {
            overlay[credential.Id] = credentials.PrepareImport(credential.Id, credential.Verifier);
        }

        foreach (var (id, password) in passwords)
        {
            overlay[id] = await credentials.PrepareAsync(id, password.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return new PendingPasswords(new HashSet<PasswordCredentialId>(), overlay);
    }

    /// <summary>The lists and user names whose passwords <paramref name="prepared"/> sets, for an audit entry that must say whose
    /// password may be in an unknown state; never the passwords.</summary>
    public static IReadOnlyList<(AccessListId List, string Username)> UsersOf(ConfigDocument document, Prepared prepared) =>
    [
        .. (document.AccessLists ?? []).SelectMany(
            list => (list?.BasicAuth?.Users ?? [])
                .Where(u => u?.CredentialId is { } id && prepared.Ids.Contains(id))
                .Select(u => (list!.Id, u.Username))),
    ];

    /// <summary>Imports the verifiers an earlier build kept in a document that is about to be used (idempotent: a credential that
    /// already has a password keeps it).</summary>
    public static async Task ImportAsync(IPasswordCredentials credentials, IReadOnlyList<LegacyCredential> legacy, CancellationToken cancellationToken)
    {
        foreach (var credential in legacy)
        {
            await credentials.ImportAsync(credential.Id, credential.Verifier, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes the credentials <paramref name="before"/> named and <paramref name="after"/> doesn't. A failure is logged,
    /// never thrown: the commit it follows stands, and the next start finds what is left (<see cref="DeleteOrphansAsync"/>).</summary>
    public static async Task DeleteUnreferencedAsync(
        IPasswordCredentials credentials, ConfigDocument before, ConfigDocument after, ILogger logger, CancellationToken cancellationToken)
    {
        var kept = Referenced(after);
        foreach (var id in Referenced(before).Where(id => !kept.Contains(id)))
        {
            try
            {
                await credentials.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDeleteFailed(logger, ex, id);
            }
        }
    }

    /// <summary>Deletes the vault's credentials the live config doesn't name: what a command that failed after setting a password left.</summary>
    public static async Task DeleteOrphansAsync(IPasswordCredentials credentials, ConfigDocument live, ILogger logger, CancellationToken cancellationToken)
    {
        var kept = Referenced(live);
        foreach (var id in await credentials.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (kept.Contains(id))
            {
                continue;
            }

            try
            {
                if (await credentials.DeleteAsync(id, cancellationToken).ConfigureAwait(false))
                {
                    LogOrphanDeleted(logger, id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDeleteFailed(logger, ex, id);
            }
        }
    }

    private static AccessListConfig? Existing(ConfigDocument current, AccessListConfig list) =>
        current.AccessLists.FirstOrDefault(l => l.Id == list.Id);

    private static AccessListConfig Prepare(AccessListConfig list, AccessListConfig? existing, List<PendingPassword> pending)
    {
        if (list.BasicAuth is not { } auth)
        {
            return list;
        }

        Dictionary<string, PasswordCredentialId> known = [];
        foreach (var user in existing?.BasicAuth?.Users ?? [])
        {
            if (user.CredentialId is { IsEmpty: false } id)
            {
                known.TryAdd(user.Username, id);
            }
        }

        return list with
        {
            BasicAuth = auth with { Users = [.. (auth.Users ?? []).Select(u => u is null ? u! : Prepare(u, known, pending))] },
        };
    }

    private static AccessListUserConfig Prepare(AccessListUserConfig user, Dictionary<string, PasswordCredentialId> known, List<PendingPassword> pending)
    {
        known.TryGetValue(user.Username ?? string.Empty, out var kept);
        if (user.Password is { Length: > 0 } password && System.Text.Encoding.UTF8.GetByteCount(password) <= AccessListLimits.MaxPasswordBytes)
        {
            var id = !kept.IsEmpty ? kept : user.CredentialId is { IsEmpty: false } given ? given : PasswordCredentialId.New();
            pending.Add(new PendingPassword(id, password));
            return user with { CredentialId = id, Password = null };
        }

        return user.Password is null && user.CredentialId is null && !kept.IsEmpty ? user with { CredentialId = kept } : user;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete password credential {CredentialId}, which no config names any more; the next start tries again.")]
    private static partial void LogDeleteFailed(ILogger logger, Exception exception, PasswordCredentialId credentialId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted password credential {CredentialId}, which the live config doesn't name (a change failed after setting it, or the user was removed).")]
    private static partial void LogOrphanDeleted(ILogger logger, PasswordCredentialId credentialId);
}
