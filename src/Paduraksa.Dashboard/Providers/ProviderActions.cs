using System.Security.Cryptography;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Auth;

namespace Paduraksa.Dashboard.Providers;

/// <summary>
/// What the account and provider forms do, as the signed-in user (the settings pages and the first-run wizard share it): the same service
/// calls as the API's, with each secret turned into bytes for the vault and those bytes zeroed when the call returns. Null means the
/// session ended under the page (it is on its way to the sign-in page).
/// </summary>
public sealed class ProviderActions(AcmeAccountService accounts, DnsProviderService providers, DashboardCaller caller)
{
    public async Task<AcmeAccountResult?> CreateAccountAsync(AcmeAccountSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        CommandOrigin? origin = await caller.OriginAsync(cancellationToken).ConfigureAwait(false);
        if (origin is null)
        {
            return null;
        }

        byte[] key = [];
        try
        {
            if (submission.EabHmacKey.Length > 0)
            {
                key = SecretInput.Base64UrlKey(submission.EabHmacKey) ?? [];
                if (key.Length == 0)
                {
                    return new AcmeAccountResult(
                        IssuanceChangeStatus.Invalid,
                        Errors: [new ConfigError("/eabHmacKey", IssuanceErrorCodes.EabIncomplete, "the EAB HMAC key must be base64url, as the CA shows it")]);
                }
            }

            CreateAcmeAccountRequest request = new(
                submission.DirectoryUrl,
                submission.Email is { } email ? [$"mailto:{email}"] : [],
                submission.TermsOfServiceAgreed,
                EabKid: submission.EabKid,
                Label: submission.Label);
            return await accounts.CreateWithEabAsync(request, key, origin, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<AcmeAccountResult?> DeactivateAccountAsync(AcmeAccountId id, CancellationToken cancellationToken = default)
    {
        CommandOrigin? origin = await caller.OriginAsync(cancellationToken).ConfigureAwait(false);
        return origin is null ? null : await accounts.DeactivateAsync(id, origin, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DnsProviderResult?> CreateProviderAsync(DnsCredentialSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        CommandOrigin? origin = await caller.OriginAsync(cancellationToken).ConfigureAwait(false);
        if (origin is null)
        {
            return null;
        }

        byte[] credential = SecretInput.Utf8(submission.Credential);
        try
        {
            return await providers.CreateWithCredentialAsync(submission.Kind, submission.Label, credential, origin, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    public async Task<DnsProviderResult?> RotateCredentialAsync(DnsProviderId id, DnsCredentialSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        CommandOrigin? origin = await caller.OriginAsync(cancellationToken).ConfigureAwait(false);
        if (origin is null)
        {
            return null;
        }

        byte[] credential = SecretInput.Utf8(submission.Credential);
        try
        {
            return await providers.RotateCredentialAsync(id, credential, origin, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    public async Task<DnsProviderResult?> DeleteProviderAsync(DnsProviderId id, CancellationToken cancellationToken = default)
    {
        CommandOrigin? origin = await caller.OriginAsync(cancellationToken).ConfigureAwait(false);
        return origin is null ? null : await providers.DeleteWithCredentialAsync(id, origin, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DnsProviderTestResult?> TestAsync(DnsProviderId id, CancellationToken cancellationToken = default)
    {
        CommandOrigin? origin = await caller.OriginAsync(cancellationToken).ConfigureAwait(false);
        return origin is null ? null : await providers.TestAsync(id, origin, cancellationToken).ConfigureAwait(false);
    }
}
