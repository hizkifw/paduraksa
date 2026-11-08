using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Issuance;

namespace Paduraksa.Dashboard.Certificates;

/// <summary>A certificate, ACME or DNS operation that did not complete, said in a sentence (the services' own messages, which never carry a secret).</summary>
public static class CertificateMessages
{
    public static IReadOnlyList<string> Errors(CertificateOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            CertificateOperationStatus.Forbidden => ["Your role can't change certificates."],
            CertificateOperationStatus.NotFound => ["The certificate no longer exists."],
            CertificateOperationStatus.Unavailable => ["The config store is unavailable, so certificates can't be changed right now."],
            _ => Sentences(result.Errors, "The operation was refused."),
        };
    }

    public static IReadOnlyList<string> Errors(IssuanceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            IssuanceOutcome.Forbidden => ["Your role can't request or renew certificates."],
            IssuanceOutcome.InProgress => ["This certificate is being issued already; its progress shows on the certificate."],
            IssuanceOutcome.NotFound => ["The certificate or its order no longer exists."],
            IssuanceOutcome.Unavailable => ["The config store is unavailable, so certificates can't be issued right now."],
            IssuanceOutcome.Failed => [Sentence(result.Message ?? "The CA did not issue the certificate.")],
            _ => Sentences(result.Errors, "The request was refused."),
        };
    }

    public static IReadOnlyList<string> Errors(IssuanceChangeStatus status, IReadOnlyList<ConfigError> errors, string? message, string what) => status switch
    {
        IssuanceChangeStatus.Denied => [$"Your role can't change {what}."],
        IssuanceChangeStatus.NotFound => [$"The {what[..^1]} no longer exists."],
        IssuanceChangeStatus.Unavailable => ["The config store is unavailable, so nothing can be changed right now."],
        IssuanceChangeStatus.Failed => [Sentence(message is { Length: > 0 } ? $"The CA refused or could not be reached: {message}" : "The CA refused or could not be reached.")],
        _ => Sentences(errors, "The request was refused."),
    };

    /// <summary>The errors as sentences, or <paramref name="fallback"/> when there are none.</summary>
    public static IReadOnlyList<string> Sentences(IReadOnlyList<ConfigError> errors, string fallback)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return errors.Count == 0 ? [fallback] : [.. errors.Select(e => Sentence(e.Message)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Starts with a capital and ends with a full stop.</summary>
    public static string Sentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string trimmed = text.Trim();
        trimmed = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        return trimmed.EndsWith('.') || trimmed.EndsWith('?') || trimmed.EndsWith('!') ? trimmed : trimmed + ".";
    }
}

/// <summary>An upload as the form hands it to the page: the stream from the browser, the password (cleared after the call) and a label.</summary>
public sealed record CertificateUpload(Stream Data, ReadOnlyMemory<char> Password, string? Label)
{
    public override string ToString() => $"{nameof(CertificateUpload)} {{ Label = {Label}, Password = *** }}";
}

/// <summary>A self-signed or local-CA certificate as the form hands it to the page.</summary>
public sealed record LocalCertificateRequest(IReadOnlyList<string> Names, Paduraksa.Core.Vault.KeySpec KeySpec, TimeSpan? Validity, string? Label);
