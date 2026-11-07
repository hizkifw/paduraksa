using Paduraksa.Acme.Dns;
using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

using static Paduraksa.Tests.Data.Config.TestConfigDatabase;
using static Paduraksa.Tests.Issuance.IssuanceHarness;

namespace Paduraksa.Tests.Issuance;

/// <summary>DNS providers (PLAN §9.3): a kind, a label and the vault secret of the credential, nothing more.</summary>
public sealed class DnsProviderServiceTests
{
    [Fact]
    public async Task AProviderReferencesAVaultSecretAndIsAudited()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var secret = await h.Secrets.PutAsync(SecretKind.DnsApiToken, new byte[] { 1 }, new SecretOptions("t", null, ["api.dns.test"]), Ct);

        var result = await h.DnsProviders.CreateAsync(new CreateDnsProviderRequest(DnsProviderKind.DeSec, "  deSEC main  ", secret), Admin, Ct);

        Assert.Equal(IssuanceChangeStatus.Committed, result.Status);
        var provider = result.Provider!;
        Assert.Equal((DnsProviderKind.DeSec, "deSEC main", secret), (provider.Kind, provider.Label, provider.CredentialSecretId));
        var audit = Assert.Single(await h.AuditAsync("acme.dns_provider.create"));
        Assert.Equal((AuditOutcome.Success, provider.Id.ToString()), (audit.Outcome, audit.Target));
        Assert.Equal(provider.Id, Assert.Single(await h.DnsProviders.ListAsync(Ct)).Id);
        Assert.Equal(provider, await h.DnsProviders.GetAsync(provider.Id, Ct));
        await h.Config.AssertChainIntactAsync();
    }

    [Fact]
    public async Task ARequestThatCannotWorkIsRefused()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var token = await h.Secrets.PutAsync(SecretKind.DnsApiToken, new byte[] { 1 }, new SecretOptions("t", null, ["api.dns.test"]), Ct);
        var eab = await h.Secrets.PutAsync(SecretKind.EabHmacKey, new byte[] { 1 }, new SecretOptions("e", MacAlgorithm.HmacSha256, null), Ct);

        (CreateDnsProviderRequest Request, string Code)[] cases =
        [
            (new(DnsProviderKind.Cloudflare, "x", SecretId.New()), IssuanceErrorCodes.CredentialMissing),
            (new(DnsProviderKind.Cloudflare, "x", eab), IssuanceErrorCodes.CredentialMissing),
            (new(DnsProviderKind.Cloudflare, "  ", token), IssuanceErrorCodes.LabelRequired),
            (new(DnsProviderKind.Cloudflare, new string('x', 257), token), IssuanceErrorCodes.TextTooLong),
            (new((DnsProviderKind)99, "x", token), IssuanceErrorCodes.KindUndefined),
        ];
        foreach (var (request, code) in cases)
        {
            var result = await h.DnsProviders.CreateAsync(request, Admin, Ct);

            Assert.Equal(IssuanceChangeStatus.Invalid, result.Status);
            Assert.Equal(code, Assert.Single(result.Errors).Code);
        }

        Assert.Empty(await h.DnsProviders.ListAsync(Ct));
    }

    [Fact]
    public async Task TestCredentialsReportsTheProvidersVerdictWithoutThrowing()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var provider = await h.CreateDnsProviderAsync();

        Assert.Equal(new DnsProviderTestResult(true, true), await h.DnsProviders.TestAsync(provider.Id, Ct));

        h.Dns.Provider.Check = new DnsCredentialCheck(false, "the token was revoked");
        Assert.Equal(new DnsProviderTestResult(true, false, "the token was revoked"), await h.DnsProviders.TestAsync(provider.Id, Ct));

        Assert.Equal(new DnsProviderTestResult(false, false), await h.DnsProviders.TestAsync(DnsProviderId.New(), Ct));
        Assert.Equal(provider.CredentialSecretId, h.Dns.Created[0].Credential);
    }
}
