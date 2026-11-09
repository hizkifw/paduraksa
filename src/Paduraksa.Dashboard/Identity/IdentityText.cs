using System.Globalization;

using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Identity;

/// <summary>The words for roles, permissions, identity outcomes and sessions, as the settings and account pages show them.</summary>
public static class IdentityText
{
    /// <summary>The roles in the order they are offered: most to least power, the Auditor last.</summary>
    public static IReadOnlyList<Role> Roles { get; } = [Role.Owner, Role.Admin, Role.Operator, Role.Viewer, Role.Auditor];

    /// <summary>A role's name, as people say it.</summary>
    public static string Name(Role role) => role switch
    {
        Role.Owner => "Owner",
        Role.Admin => "Admin",
        Role.Operator => "Operator",
        Role.Viewer => "Viewer",
        Role.Auditor => "Auditor",
        _ => role.ToString(),
    };

    /// <summary>What a role may do, in one line.</summary>
    public static string Describe(Role role) => role switch
    {
        Role.Owner => "Everything: users and roles, the vault, and every token and session.",
        Role.Admin => "All config, certificates, providers and logs, and the audit log. Sees users but can't change them.",
        Role.Operator => "Hosts, upstreams and certificates. No users, secrets or audit log.",
        Role.Viewer => "Reads the config and certificates and runs the simulator. Changes nothing.",
        Role.Auditor => "Reads the audit log, the logs and the config. Changes nothing.",
        _ => string.Empty,
    };

    /// <summary>A permission as a token scope's checkbox says it.</summary>
    public static string Describe(Permission permission) => permission switch
    {
        Permission.ConfigRead => "Read hosts, upstreams, profiles, settings and history",
        Permission.ConfigWrite => "Change them, roll back",
        Permission.CertificatesRead => "Read the certificate inventory",
        Permission.CertificatesWrite => "Upload, issue, renew and delete certificates",
        Permission.ProvidersRead => "Read ACME accounts and DNS providers",
        Permission.ProvidersWrite => "Change ACME accounts and DNS providers",
        Permission.UsersRead => "List users and their roles",
        Permission.UsersWrite => "Create, change and delete users",
        Permission.AuditRead => "Read the audit log",
        Permission.LogsRead => "Browse, tail and download the log files",
        Permission.Simulate => "Run the request simulator",
        Permission.VaultManage => "Manage secrets in the vault",
        Permission.TokensOwn => "Manage one's own API tokens",
        Permission.TokensAll => "Manage everyone's API tokens",
        Permission.SessionsAll => "Manage everyone's sessions",
        _ => permission.ToName(),
    };

    /// <summary>The scopes a token may be given, by area. Tokens can't manage tokens or sessions, so those aren't offered.</summary>
    public static IReadOnlyList<(string Area, IReadOnlyList<Permission> Scopes)> ScopeAreas { get; } =
    [
        ("Config", [Permission.ConfigRead, Permission.ConfigWrite]),
        ("Certificates", [Permission.CertificatesRead, Permission.CertificatesWrite]),
        ("ACME and DNS", [Permission.ProvidersRead, Permission.ProvidersWrite]),
        ("Simulator", [Permission.Simulate]),
        ("Audit", [Permission.AuditRead]),
        ("Logs", [Permission.LogsRead]),
        ("Users", [Permission.UsersRead, Permission.UsersWrite]),
        ("Vault", [Permission.VaultManage]),
    ];

    /// <summary>Whether a scope only reads.</summary>
    public static bool IsRead(Permission permission) =>
        permission is Permission.ConfigRead or Permission.CertificatesRead or Permission.ProvidersRead or Permission.UsersRead
            or Permission.AuditRead or Permission.Simulate or Permission.LogsRead;

    /// <summary>Why an identity change was refused, in a sentence; <paramref name="problem"/> is the service's own reason when it gave one.</summary>
    public static string Refusal(IdentityStatus status, string? problem = null) => status switch
    {
        IdentityStatus.Denied => "Your role can't do that.",
        IdentityStatus.NotFound => "It's gone: someone deleted it or it was never there.",
        IdentityStatus.Invalid => problem ?? "That isn't valid.",
        IdentityStatus.Conflict => problem ?? "That clashes with something that exists.",
        IdentityStatus.LastOwner => LastOwner,
        IdentityStatus.InvalidCredentials => "That password or code isn't right.",
        _ => problem ?? "It didn't work.",
    };

    /// <summary>The last-Owner guard's refusal.</summary>
    public const string LastOwner =
        "Paduraksa needs at least one enabled Owner, and this is the last one. Make someone else an Owner first, then change this user.";

    /// <summary>The answer when every password-check slot is taken.</summary>
    public const string Busy = "Too many password checks are running right now. Try again in a moment.";

    /// <summary>A browser's user agent in a few words: "Firefox on Linux", or the start of it when it isn't recognised.</summary>
    public static string Browser(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "Unknown browser";
        }

        string browser = userAgent switch
        {
            _ when userAgent.Contains("Edg/", StringComparison.Ordinal) => "Edge",
            _ when userAgent.Contains("OPR/", StringComparison.Ordinal) => "Opera",
            _ when userAgent.Contains("Firefox/", StringComparison.Ordinal) => "Firefox",
            _ when userAgent.Contains("Chrome/", StringComparison.Ordinal) || userAgent.Contains("Chromium/", StringComparison.Ordinal) => "Chrome",
            _ when userAgent.Contains("Safari/", StringComparison.Ordinal) => "Safari",
            _ when userAgent.StartsWith("curl/", StringComparison.Ordinal) => "curl",
            _ => string.Empty,
        };
        string system = userAgent switch
        {
            _ when userAgent.Contains("Android", StringComparison.Ordinal) => "Android",
            _ when userAgent.Contains("iPhone", StringComparison.Ordinal) || userAgent.Contains("iPad", StringComparison.Ordinal) => "iOS",
            _ when userAgent.Contains("Windows", StringComparison.Ordinal) => "Windows",
            _ when userAgent.Contains("Mac OS X", StringComparison.Ordinal) || userAgent.Contains("Macintosh", StringComparison.Ordinal) => "macOS",
            _ when userAgent.Contains("CrOS", StringComparison.Ordinal) => "ChromeOS",
            _ when userAgent.Contains("Linux", StringComparison.Ordinal) => "Linux",
            _ => string.Empty,
        };
        return (browser, system) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{browser} on {system}",
            ({ Length: > 0 }, _) => browser,
            (_, { Length: > 0 }) => $"A browser on {system}",
            _ => userAgent.Length > 40 ? string.Concat(userAgent.AsSpan(0, 40), "...") : userAgent,
        };
    }

    /// <summary>An ID shortened for a table: its prefix and the first characters (<c>usr_034u3c...</c>).</summary>
    public static string ShortId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id.Length > 14 ? string.Concat(id.AsSpan(0, 12), "...") : id;
    }

    /// <summary>A token's state: active, expired or revoked, as a word.</summary>
    public static (string Label, Config.Tone Tone) State(ApiTokenSummary token, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Revoked ? ("Revoked", Config.Tone.Default)
            : !IsLive(token, now) ? ("Expired", Config.Tone.Warning)
            : ("Active", Config.Tone.Success);
    }

    /// <summary>Whether a token still works: not revoked, and not past its expiry.</summary>
    public static bool IsLive(ApiTokenSummary token, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(token);
        return !token.Revoked && (token.ExpiresAt is not { } end || end > now);
    }

    /// <summary>The refusal when a role can't read the config (every role can: the person has none that counts).</summary>
    public const string CantReadConfig = "Your role can't read the config";

    /// <summary>What to do about <see cref="CantReadConfig"/>.</summary>
    public const string CantReadConfigHelp = "Every role can read it. An Owner can give you one; Viewer reads without changing anything.";

    /// <summary>The refusal when a role can't change the config, naming the roles that can (config:write).</summary>
    public const string CantChangeConfig = "Your role can't change the config. An Owner can give you the Operator, Admin or Owner role.";

    /// <summary>When something ends, from now: "in 5 days", "never".</summary>
    public static string Until(DateTimeOffset? end, DateTimeOffset now)
    {
        if (end is not { } at)
        {
            return "Never";
        }

        TimeSpan left = at - now;
        return left <= TimeSpan.Zero ? $"Ended {Config.RevisionText.Ago(at, now)}"
            : left < TimeSpan.FromHours(1) ? "Within the hour"
            : left < TimeSpan.FromDays(1) ? $"In {(int)left.TotalHours} h"
            : left < TimeSpan.FromDays(60) ? $"In {(int)left.TotalDays} days"
            : at.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }
}
