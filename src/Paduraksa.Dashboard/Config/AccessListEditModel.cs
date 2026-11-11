using System.Security.Cryptography;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// An access list as its form edits it: the addresses as one box each, and the users as rows with a password box. A password
/// typed (or generated) is sent as the user's <see cref="AccessListUserConfig.Password"/> and goes to the vault; a user
/// whose box stays empty keeps the credential the list names, so an edit never asks for every password again. A new list gets a fresh typed
/// ID, and the version the form opened with makes a concurrent edit a conflict.
/// </summary>
public sealed class AccessListEditModel
{
    private readonly AccessListConfig? _original;

    private AccessListEditModel(AccessListConfig? original, AccessListId id, AccessListConfig? source = null)
    {
        _original = original;
        Id = id;
        source ??= original;
        Name = source?.Name ?? string.Empty;
        Notes = source?.Notes;
        AllowText = string.Join('\n', source?.Ip?.Allow ?? []);
        DenyText = string.Join('\n', source?.Ip?.Deny ?? []);
        AllowCountries = [.. source?.Ip?.Countries?.Allow ?? []];
        DenyCountries = [.. source?.Ip?.Countries?.Deny ?? []];
        DropDenied = source?.Ip?.DropDenied ?? false;
        Realm = source?.BasicAuth?.Realm;
        Satisfy = source?.Satisfy ?? AccessListSatisfy.All;
        PassCredentialsUpstream = source?.PassCredentialsUpstream ?? false;
        foreach (var user in source?.BasicAuth?.Users ?? [])
        {
            Users.Add(new AccessListUserEntry { Username = user.Username, StoredCredential = user.CredentialId, Password = user.Password });
        }
    }

    public AccessListId Id { get; }

    public bool IsNew => _original is null;

    /// <summary>The version the form was opened with (0 for a new list).</summary>
    public long Version => _original?.Version ?? 0;

    public string Name { get; set; }

    public string? Notes { get; set; }

    /// <summary>Allowed addresses and networks, one per line.</summary>
    public string AllowText { get; set; }

    public string DenyText { get; set; }

    /// <summary>The country codes the list allows and denies, in the order they were picked.</summary>
    public IReadOnlyList<string> AllowCountries { get; set; }

    public IReadOnlyList<string> DenyCountries { get; set; }

    public bool DropDenied { get; set; }

    /// <summary>The prompt's realm; blank uses the list's name.</summary>
    public string? Realm { get; set; }

    public AccessListSatisfy Satisfy { get; set; }

    public bool PassCredentialsUpstream { get; set; }

    public List<AccessListUserEntry> Users { get; } = [];

    public int AllowCount => HostEditModel.SplitList(AllowText).Count;

    public int DenyCount => HostEditModel.SplitList(DenyText).Count;

    public static AccessListEditModel New() => new(null, AccessListId.New());

    public static AccessListEditModel From(AccessListConfig list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return new AccessListEditModel(list, list.Id);
    }

    /// <summary>A form for a list made earlier and not applied yet (staged in a host's draft): still new, with what it was given.</summary>
    public static AccessListEditModel FromDraft(AccessListConfig list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return new AccessListEditModel(null, list.Id, list);
    }

    /// <summary>
    /// The list as the command sends it. Rules that aren't used are left out rather than sent empty, so a list with no
    /// addresses or countries writes none: dropping needs something to refuse, which the validator would otherwise report on an empty box.
    /// </summary>
    public AccessListConfig ToConfig()
    {
        var allow = HostEditModel.SplitList(AllowText);
        var deny = HostEditModel.SplitList(DenyText);
        var realm = string.IsNullOrWhiteSpace(Realm) ? null : Realm.Trim();
        return new AccessListConfig(
            Id,
            Name.Trim(),
            Version,
            string.IsNullOrWhiteSpace(Notes) ? null : Notes,
            allow.Count + deny.Count + AllowCountries.Count + DenyCountries.Count == 0 && !DropDenied
                ? null
                : new AccessListIp(allow, deny, DropDenied, AllowCountries.Count + DenyCountries.Count == 0 ? null : new AccessListCountries(AllowCountries, DenyCountries)),
            Users.Count == 0 && realm is null ? null : new AccessListBasicAuth(realm, [.. Users.Select(user => user.ToConfig())]),
            Satisfy,
            PassCredentialsUpstream);
    }
}

/// <summary>One user row of the form: a class, so the form can bind to it and remove it by reference.</summary>
public sealed class AccessListUserEntry
{
    public string Username { get; set; } = string.Empty;

    /// <summary>The vault credential the list already names for this user; null for a user added in this form.</summary>
    public PasswordCredentialId? StoredCredential { get; init; }

    /// <summary>The vault holds no password for the stored credential (a rollback brought the user back after it was deleted), so
    /// the user can't sign in until one is set. Filled from the live warnings when the form opens.</summary>
    public bool LacksPassword { get; set; }

    /// <summary>A password typed or generated here, sent once and never read back.</summary>
    public string? Password { get; set; }

    /// <summary>The password in the box was generated, so it is shown (once) instead of masked.</summary>
    public bool Revealed { get; set; }

    /// <summary>The user has a password the box doesn't show: a stored one, or one just typed.</summary>
    public bool HasPassword => !string.IsNullOrEmpty(Password) || (StoredCredential is not null && !LacksPassword);

    /// <summary>A typed password wins; otherwise the stored credential stays. A new user with neither sends an empty password, which the validator refuses.</summary>
    public AccessListUserConfig ToConfig() => !string.IsNullOrEmpty(Password)
        ? new AccessListUserConfig(Username, StoredCredential, Password)
        : StoredCredential is { } credential ? new AccessListUserConfig(Username, credential) : new AccessListUserConfig(Username, null, string.Empty);

    /// <summary>A random password for the "Generate" button: 20 characters from letters and digits, about 115 bits.</summary>
    public static string Generate()
    {
        const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(Alphabet, 20);
    }
}
