using System.Globalization;
using System.Text;

using MudBlazor;

using Paduraksa.Contracts.Certificates;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Certificates;

/// <summary>Where a certificate came from, as the dashboard lists it: the inventory's sources plus the process's own built-in ones.</summary>
public enum CertificateOrigin
{
    Acme,
    Uploaded,
    SelfSigned,
    LocalCa,
    LocalCaRoot,
    BuiltIn,
}

/// <summary>Where a new certificate comes from, as the new-certificate drawer offers it.</summary>
public enum AddCertificateKind
{
    Acme,
    Upload,
    SelfSigned,
    LocalCaLeaf,
}

/// <summary>Issuing from the local CA as the pages do it: the root is created first when there is none (asking again changes nothing).</summary>
public static class LocalCa
{
    public static async Task<Contracts.Certificates.CertificateOperationResult> IssueAsync(
        Application.Certificates.CertificateOperations operations, LocalCertificateRequest request, Application.Commands.CommandOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(request);
        Contracts.Certificates.CertificateOperationResult root = await operations.EnsureLocalCaAsync(new LocalCaRequest(), origin).ConfigureAwait(false);
        if (root.Status is not (CertificateOperationStatus.Completed or CertificateOperationStatus.Unchanged))
        {
            return root;
        }

        return await operations.IssueFromLocalCaAsync(new LocalCaLeafRequest(request.Names, request.KeySpec, request.Validity, request.Label), origin).ConfigureAwait(false);
    }
}

/// <summary>How the certificate pages word and colour things: sources, states, the expiry countdown, issuers and fingerprints.</summary>
public static class CertificateDisplay
{
    /// <summary>"Soon": the countdown turns amber inside this many days.</summary>
    public const int WarningDays = 30;

    /// <summary>The countdown turns red inside this many days.</summary>
    public const int UrgentDays = 7;

    public static CertificateOrigin OriginOf(CertificateSource source) => source switch
    {
        CertificateSource.Acme => CertificateOrigin.Acme,
        CertificateSource.Uploaded => CertificateOrigin.Uploaded,
        CertificateSource.SelfSigned => CertificateOrigin.SelfSigned,
        CertificateSource.LocalCa => CertificateOrigin.LocalCa,
        _ => CertificateOrigin.LocalCaRoot,
    };

    public static string Label(CertificateOrigin origin) => origin switch
    {
        CertificateOrigin.Acme => "ACME",
        CertificateOrigin.Uploaded => "Uploaded",
        CertificateOrigin.SelfSigned => "Self-signed",
        CertificateOrigin.LocalCa => "Local CA",
        CertificateOrigin.LocalCaRoot => "Local CA root",
        _ => "Built-in",
    };

    public static string Icon(CertificateOrigin origin) => origin switch
    {
        CertificateOrigin.Acme => Icons.Material.Outlined.Public,
        CertificateOrigin.Uploaded => Icons.Material.Outlined.UploadFile,
        CertificateOrigin.SelfSigned => Icons.Material.Outlined.Draw,
        CertificateOrigin.LocalCa => Icons.Material.Outlined.Lan,
        CertificateOrigin.LocalCaRoot => Icons.Material.Outlined.AccountTree,
        _ => Icons.Material.Outlined.Memory,
    };

    /// <summary>The tone name of a certificate's state (for <c>pdk-tone-*</c>), from its stored status and the clock.</summary>
    public static string StatusTone(CertificateStatus status, DateTimeOffset notBefore, DateTimeOffset notAfter, DateTimeOffset now) => status switch
    {
        CertificateStatus.Failed => "error",
        CertificateStatus.Pending => "info",
        _ when now > notAfter => "error",
        _ when now < notBefore => "warning",
        _ => "success",
    };

    public static string StatusLabel(CertificateStatus status, DateTimeOffset notBefore, DateTimeOffset notAfter, DateTimeOffset now) => status switch
    {
        CertificateStatus.Failed => "Failed",
        CertificateStatus.Pending => "Pending",
        _ when now > notAfter => "Expired",
        _ when now < notBefore => "Not yet valid",
        _ => "Active",
    };

    /// <summary>The countdown's tone: red when expired or within <see cref="UrgentDays"/>, amber within <see cref="WarningDays"/>, else green.</summary>
    public static string ExpiryTone(DateTimeOffset notAfter, DateTimeOffset now)
    {
        TimeSpan left = notAfter - now;
        return left <= TimeSpan.FromDays(UrgentDays) ? "error" : left <= TimeSpan.FromDays(WarningDays) ? "warning" : "success";
    }

    /// <summary>"Expired 3 days ago", "Expires today", "12 days left", "3 months left".</summary>
    public static string ExpiresIn(DateTimeOffset notAfter, DateTimeOffset now)
    {
        TimeSpan left = notAfter - now;
        if (left <= TimeSpan.Zero)
        {
            int ago = (int)Math.Floor(-left.TotalDays);
            return ago == 0 ? "Expired today" : $"Expired {Plural(ago, "day", "days")} ago";
        }

        int days = (int)Math.Floor(left.TotalDays);
        return days switch
        {
            0 => left.TotalHours >= 1 ? $"{Plural((int)left.TotalHours, "hour", "hours")} left" : "Expires within the hour",
            < 60 => $"{Plural(days, "day", "days")} left",
            < 730 => $"{Plural(days / 30, "month", "months")} left",
            _ => $"{Plural(days / 365, "year", "years")} left",
        };
    }

    /// <summary>How much of the validity period has passed, 0 to 100.</summary>
    public static double LifetimeUsed(DateTimeOffset notBefore, DateTimeOffset notAfter, DateTimeOffset now)
    {
        double total = (notAfter - notBefore).TotalSeconds;
        return total <= 0 ? 100 : Math.Clamp((now - notBefore).TotalSeconds / total * 100, 0, 100);
    }

    /// <summary>The common name of a distinguished name (<c>CN=R11, O=Let's Encrypt, C=US</c> is <c>R11</c>), else the whole name.</summary>
    public static string CommonName(string distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
        {
            return "-";
        }

        foreach (string part in distinguishedName.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[3..];
            }
        }

        return distinguishedName;
    }

    /// <summary>The organisation of a distinguished name (<c>O=Let's Encrypt</c>), or null.</summary>
    public static string? Organization(string distinguishedName)
    {
        foreach (string part in (distinguishedName ?? string.Empty).Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.StartsWith("O=", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[2..];
            }
        }

        return null;
    }

    /// <summary>A hex fingerprint in colon-separated upper-case pairs, as browsers show it.</summary>
    public static string Fingerprint(string hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return string.Empty;
        }

        StringBuilder builder = new(hex.Length * 3 / 2);
        for (int i = 0; i < hex.Length; i += 2)
        {
            if (i > 0)
            {
                builder.Append(':');
            }

            builder.Append(hex.AsSpan(i, Math.Min(2, hex.Length - i)));
        }

        return builder.ToString().ToUpperInvariant();
    }

    /// <summary>The name a certificate goes by in a list: its label, else its first name, else its subject's common name.</summary>
    public static string DisplayName(CertificateSummary certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.Label is { Length: > 0 } label ? label
            : certificate.Names.Count > 0 ? certificate.Names[0]
            : CommonName(certificate.Subject);
    }

    /// <summary>A certificate's names on one line: up to three, then how many more.</summary>
    public static string NamesLine(CertificateSummary certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.Names.Count switch
        {
            0 => CommonName(certificate.Subject),
            <= 3 => string.Join(", ", certificate.Names),
            _ => $"{string.Join(", ", certificate.Names.Take(2))} +{(certificate.Names.Count - 2).ToString(CultureInfo.InvariantCulture)}",
        };
    }

    public static string Date(DateTimeOffset value) => value.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    public static string DateTime(DateTimeOffset value) => value.ToString("d MMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture);

    public static string Plural(int count, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");
}
