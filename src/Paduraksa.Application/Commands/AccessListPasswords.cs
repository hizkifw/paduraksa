using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Security;

namespace Paduraksa.Application.Commands;

/// <summary>
/// The one place a plain password is turned into what the document keeps. A user written with a
/// <see cref="AccessListUserConfig.Password"/> gets a fresh PBKDF2 hash (a new salt every time) and the password is dropped;
/// one written with neither a password nor a hash keeps the hash the list already has for that name, so editing a list
/// doesn't ask for every password again. A password that can't be hashed (empty, or longer than the gate reads) is left in
/// place for the validator to refuse, which keeps it out of every document.
/// </summary>
internal static class AccessListPasswords
{
    public static ConfigCommand Prepare(ConfigCommand command, ConfigDocument current) => command switch
    {
        UpsertAccessListCommand { AccessList: { } list } upsert => upsert with { AccessList = Prepare(list, Existing(current, list)) },
        ReplaceConfigCommand { Document: { } document } replace => replace with
        {
            Document = document with
            {
                AccessLists = [.. (document.AccessLists ?? []).Select(l => l is null ? l! : Prepare(l, Existing(current, l)))],
            },
        },
        _ => command,
    };

    private static AccessListConfig? Existing(ConfigDocument current, AccessListConfig list) =>
        current.AccessLists.FirstOrDefault(l => l.Id == list.Id);

    private static AccessListConfig Prepare(AccessListConfig list, AccessListConfig? existing)
    {
        if (list.BasicAuth is not { } auth)
        {
            return list;
        }

        Dictionary<string, string?> known = [];
        foreach (var user in existing?.BasicAuth?.Users ?? [])
        {
            known.TryAdd(user.Username, user.PasswordHash);
        }

        return list with
        {
            BasicAuth = auth with { Users = [.. (auth.Users ?? []).Select(u => u is null ? u! : Prepare(u, known))] },
        };
    }

    private static AccessListUserConfig Prepare(AccessListUserConfig user, Dictionary<string, string?> known)
    {
        if (user.Password is { Length: > 0 } password && System.Text.Encoding.UTF8.GetByteCount(password) <= PasswordHash.MaxPasswordBytes)
        {
            return user with { PasswordHash = PasswordHash.Create(password), Password = null };
        }

        if (user.Password is null && string.IsNullOrEmpty(user.PasswordHash) && user.Username is { } name && known.TryGetValue(name, out var kept))
        {
            return user with { PasswordHash = kept };
        }

        return user;
    }
}
