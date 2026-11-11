using System.Text.Json.Serialization;

using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>
/// A named set of rules that hosts and routes apply to who may reach them: IP allow and deny entries, and HTTP basic-auth
/// users, combined as <see cref="Satisfy"/> says. It is a top-level object, so one list can guard several hosts and
/// routes, and it replaces the inline IP allow list the settings used to carry.
/// </summary>
/// <param name="Id">Identity.</param>
/// <param name="Name">Human label, unique ignoring case. The default <c>realm</c> of the password prompt.</param>
/// <param name="Version">Concurrency version.</param>
/// <param name="Notes">Free text.</param>
/// <param name="Ip">The address rules; null = none.</param>
/// <param name="BasicAuth">The users and their realm; null = no password.</param>
/// <param name="Satisfy"><see cref="AccessListSatisfy.All"/> (built-in): the address must pass and then, when the list has
/// users, the credentials. <see cref="AccessListSatisfy.Any"/>: either passes, so an address in the allow list needs no
/// password. A denied address is refused either way.</param>
/// <param name="PassCredentialsUpstream">Forward the <c>Authorization</c> header to the upstream. Off: the proxy's
/// credentials are removed once the list has accepted them, because the upstream has no use for them.</param>
public sealed record AccessListConfig(
    AccessListId Id,
    string Name,
    long Version = 0,
    string? Notes = null,
    AccessListIp? Ip = null,
    AccessListBasicAuth? BasicAuth = null,
    AccessListSatisfy Satisfy = AccessListSatisfy.All,
    bool PassCredentialsUpstream = false);

/// <summary>The address rules of an access list. Deny wins over allow; an empty allow list means any address that isn't
/// denied, and a non-empty one means only those.</summary>
/// <param name="Allow">Addresses or CIDRs (<c>192.0.2.1</c>, <c>10.0.0.0/8</c>, <c>2001:db8::/32</c>); a set (canonical
/// order: sorted, no duplicates).</param>
/// <param name="Deny">Same shape. An address here is refused whatever else it sends.</param>
/// <param name="DropDenied">Abort a refused connection instead of answering 403.</param>
public sealed record AccessListIp(IReadOnlyList<string>? Allow = null, IReadOnlyList<string>? Deny = null, bool DropDenied = false)
{
    public IReadOnlyList<string> Allow { get; init; } = Allow ?? [];

    public IReadOnlyList<string> Deny { get; init; } = Deny ?? [];
}

/// <summary>An access list's basic-auth users.</summary>
/// <param name="Realm">The realm of the password prompt: 1-64 printable ASCII characters; null = the list's name.</param>
/// <param name="Users">Canonical order: by username. Usernames are unique, case-sensitive, visible ASCII without a colon.</param>
public sealed record AccessListBasicAuth(string? Realm = null, IReadOnlyList<AccessListUserConfig>? Users = null)
{
    public IReadOnlyList<AccessListUserConfig> Users { get; init; } = Users ?? [];
}

/// <summary>One basic-auth user. The document names the user's password in the vault and never holds it, or anything that
/// verifies it: no read returns a password or a hash.</summary>
/// <param name="Username">1-64 visible ASCII characters, no colon, case-sensitive.</param>
/// <param name="CredentialId">The vault's handle on this user's password. The server makes it when a user is first written with a
/// <paramref name="Password"/> and keeps it when the password changes, so a rollback restores the user but not an older password.
/// A user whose credential the vault doesn't hold (deleted since, or never set) can't sign in, and the config says so in a warning
/// (<c>accessList.user.noPassword</c>). A write that names a user already on the list and sends neither a password nor an ID keeps
/// the credential the list has.</param>
/// <param name="Password">Write-only: the plain password of a new or changed user, 1-256 bytes in UTF-8. The server hands it to the
/// vault before anything is validated, stored or audited and never returns it, so it is null in every document it hands out.</param>
public sealed record AccessListUserConfig(
    string Username,
    PasswordCredentialId? CredentialId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Password = null)
{
    // The generated one would print the plain password of a write into any log line that formats the command or the document.
    public override string ToString() => $"AccessListUserConfig {{ Username = {Username}, CredentialId = {CredentialId} }}";
}

/// <summary>Which access list a layer applies. <see cref="AccessListId"/> names a list the document declares; both fields
/// inherit on their own, so a host can turn an inherited list off without forgetting which one it was, and a route can
/// inherit its host's, name another, or turn it off. A layer that names a list without stating
/// <see cref="Enabled"/> leaves the switch to the built-in default, which is on.</summary>
public sealed record AccessListSettings : ISettingsLayer
{
    /// <summary>Apply the resolved list. Built-in default: on. Off lifts an inherited list.</summary>
    public bool? Enabled { get; init; }

    /// <summary>The list to apply; null = inherit. With <see cref="Enabled"/> off the reference is kept but inert.</summary>
    public AccessListId? AccessListId { get; init; }

    [JsonIgnore]
    public bool SetsNothing => Enabled is null && AccessListId is null;
}
