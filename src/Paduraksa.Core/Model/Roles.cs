namespace Paduraksa.Core.Model;

/// <summary>
/// An admin role (PLAN §10.1). A role is a named set of <see cref="Permission"/>s (<c>RolePermissions</c> in
/// Application), so custom roles can be added later. Persisted by name (<see cref="ModelNames"/>).
/// </summary>
public enum Role
{
    /// <summary>Everything, including users, the tokens and sessions of others, and the vault.</summary>
    Owner = 1,

    /// <summary>All config, certificates and providers, and the audit log; no user or vault management.</summary>
    Admin = 2,

    /// <summary>Hosts and certificates; no users, secrets, provider changes or audit log.</summary>
    Operator = 3,

    /// <summary>Read-only, and may run the request simulator.</summary>
    Viewer = 4,

    /// <summary>The audit log and read-only access.</summary>
    Auditor = 5,
}

/// <summary>
/// One thing an admin may do (PLAN §10.1). Roles grant sets of them; an API token's scopes are a subset of its owner's.
/// Persisted by name (<see cref="ModelNames"/>), which is also how a token scope is written (<c>config:write</c>).
/// </summary>
#pragma warning disable CA1711 // "Permission" is the domain's word (PLAN §10.1); the rule is about the attribute-like suffix.
public enum Permission
#pragma warning restore CA1711
{
    /// <summary>Read the config document, revisions and settings.</summary>
    ConfigRead = 1,

    /// <summary>Run config commands: hosts, upstreams, profiles, settings, import, rollback.</summary>
    ConfigWrite = 2,

    /// <summary>List and read the certificate inventory (public data).</summary>
    CertificatesRead = 3,

    /// <summary>Register, change and delete certificates, and start issuance.</summary>
    CertificatesWrite = 4,

    /// <summary>Read ACME accounts and DNS providers (never their credentials).</summary>
    ProvidersRead = 5,

    /// <summary>Create, change and delete ACME accounts and DNS providers.</summary>
    ProvidersWrite = 6,

    /// <summary>List users and read their roles.</summary>
    UsersRead = 7,

    /// <summary>Create, change, disable and delete users and assign roles.</summary>
    UsersWrite = 8,

    /// <summary>Issue, list and revoke one's own API tokens.</summary>
    TokensOwn = 9,

    /// <summary>List and revoke the API tokens of every user.</summary>
    TokensAll = 10,

    /// <summary>List and revoke the sessions of every user.</summary>
    SessionsAll = 11,

    /// <summary>Read the audit log.</summary>
    AuditRead = 12,

    /// <summary>Manage secrets in the vault (store, replace, delete; never read).</summary>
    VaultManage = 13,

    /// <summary>Run the request simulator.</summary>
    Simulate = 14,
}
