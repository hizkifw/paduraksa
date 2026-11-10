using MudBlazor;

using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Providers;

/// <summary>A known ACME CA: its directory, its name and where its terms of service are.</summary>
/// <param name="Key">A short stable key for the preset picker.</param>
/// <param name="Name">What the CA is called.</param>
/// <param name="DirectoryUrl">Its RFC 8555 directory.</param>
/// <param name="TermsUrl">Where a person reads its terms (the CA's own page; the dashboard fetches nothing).</param>
/// <param name="NeedsEab">Whether it requires external account binding.</param>
/// <param name="Note">One line about when to use it.</param>
public sealed record AcmeCaPreset(string Key, string Name, string DirectoryUrl, string TermsUrl, bool NeedsEab, string Note);

/// <summary>How the provider pages and forms name ACME accounts, CAs and DNS providers.</summary>
public static class ProviderDisplay
{
    public const string LetsEncrypt = "https://acme-v02.api.letsencrypt.org/directory";

    public const string LetsEncryptStaging = "https://acme-staging-v02.api.letsencrypt.org/directory";

    /// <summary>The presets of the account form, the recommended one first; "custom" is any other directory URL.</summary>
    public static IReadOnlyList<AcmeCaPreset> Presets { get; } =
    [
        new("letsencrypt", "Let's Encrypt", LetsEncrypt, "https://letsencrypt.org/repository/", false, "Free, trusted everywhere. Rate-limited: test with staging first."),
        new("letsencrypt-staging", "Let's Encrypt (staging)", LetsEncryptStaging, "https://letsencrypt.org/docs/staging-environment/", false, "For trying things out: generous limits, certificates no browser trusts."),
        new("zerossl", "ZeroSSL", "https://acme.zerossl.com/v2/DV90", "https://zerossl.com/terms/", true, "Needs an EAB key from your ZeroSSL account."),
        new("google", "Google Trust Services", "https://dv.acme-v02.api.pki.goog/directory", "https://pki.goog/repository/", true, "Needs an EAB key from Google Cloud."),
    ];

    /// <summary>The CA's name for a directory URL (a preset's), else the URL's host.</summary>
    public static string CaName(string directoryUrl)
    {
        var preset = Presets.FirstOrDefault(p => string.Equals(p.DirectoryUrl, directoryUrl, StringComparison.OrdinalIgnoreCase));
        if (preset is not null)
        {
            return preset.Name;
        }

        return Uri.TryCreate(directoryUrl, UriKind.Absolute, out var uri) ? uri.Host : directoryUrl;
    }

    /// <summary>Whether the directory is a staging (untrusted) one.</summary>
    public static bool IsStaging(string directoryUrl) => directoryUrl.Contains("staging", StringComparison.OrdinalIgnoreCase);

    /// <summary>An account as a select lists it: its label, else its contact, with the CA.</summary>
    public static string AccountName(AcmeAccountSummary account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var who = account.Label is { Length: > 0 } label ? label
            : account.Contacts.Count > 0 ? Contact(account.Contacts[0])
            : "No contact";
        return $"{who} - {CaName(account.DirectoryUrl)}";
    }

    /// <summary>A contact without its <c>mailto:</c>.</summary>
    public static string Contact(string contact) =>
        contact.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? contact[7..] : contact;

    public static string KindLabel(DnsProviderKind kind) => kind switch
    {
        DnsProviderKind.Cloudflare => "Cloudflare",
        DnsProviderKind.DeSec => "deSEC",
        _ => kind.ToString(),
    };

    public static string KindIcon(DnsProviderKind kind) => kind switch
    {
        DnsProviderKind.Cloudflare => Icons.Material.Outlined.Cloud,
        _ => Icons.Material.Outlined.Shield,
    };

    /// <summary>What credential a kind takes and where to make one.</summary>
    public static string CredentialHint(DnsProviderKind kind) => kind switch
    {
        DnsProviderKind.Cloudflare => "An API token with Zone:Read and DNS:Edit on the zones (My Profile, API Tokens). Not the global API key.",
        DnsProviderKind.DeSec => "A token from desec.io (Token management). It can manage every domain of the account.",
        _ => "The provider's API token.",
    };
}
