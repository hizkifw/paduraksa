using Bunit;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Dashboard.Providers;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The ACME account and DNS provider forms: the CA's terms are required, secrets are password fields that are emptied as
/// the form is sent and never shown again, and "Test credentials" shows its verdict plainly.
/// </summary>
public sealed class ProviderFormTests
{
    [Fact]
    public async Task AnAccount_NeedsTheTermsAgreed()
    {
        await using DashboardTestContext context = new();
        var calls = 0;
        var form = context.Render<AcmeAccountForm>(p => p
            .Add(f => f.Submit, _ => { calls++; return Task.FromResult<AcmeAccountResult?>(null); }));

        await form.InvokeAsync(() => form.Find(".pdk-acme-account-submit").Click());

        Assert.Equal(0, calls);
        Assert.Contains("The CA requires you to agree to its terms.", form.Markup, StringComparison.Ordinal);
        Assert.Equal("https://letsencrypt.org/repository/", form.Find(".pdk-acme-tos a").GetAttribute("href"));
    }

    [Fact]
    public async Task AnAccount_WithEab_HandsTheKeyOver_AndTheFieldIsEmptiedForGood()
    {
        await using DashboardTestContext context = new();
        AcmeAccountSubmission? sent = null;
        string? hmacSeen = null;
        AcmeAccountSummary? created = null;
        var form = context.Render<AcmeAccountForm>(p => p
            .Add(f => f.Submit, submission =>
            {
                sent = submission;
                hmacSeen = new string(submission.EabHmacKey);
                return Task.FromResult<AcmeAccountResult?>(new AcmeAccountResult(IssuanceChangeStatus.Committed, Account()));
            })
            .Add(f => f.Created, account => created = account));

        form.Find(".pdk-acme-email input").Change("ops@example.com");
        form.Find(".pdk-acme-eab-toggle input").Change(true);
        form.Find(".pdk-acme-eab-kid input").Change("kid-1");
        form.Find(".pdk-acme-eab-hmac input").Change("c2VjcmV0LWhtYWMta2V5");
        Assert.Equal("password", form.Find(".pdk-acme-eab-hmac input").GetAttribute("type"));
        form.Find(".pdk-acme-tos input").Change(true);
        await form.InvokeAsync(() => form.Find(".pdk-acme-account-submit").Click());

        Assert.NotNull(created);
        Assert.Equal(("https://acme-v02.api.letsencrypt.org/directory", "ops@example.com", true, "kid-1"), (sent!.DirectoryUrl, sent.Email, sent.TermsOfServiceAgreed, sent.EabKid));
        Assert.Equal("c2VjcmV0LWhtYWMta2V5", hmacSeen);
        // The array is cleared after the call, the field emptied, and nothing of it is on the page or in the submission's text.
        Assert.All(sent.EabHmacKey, c => Assert.Equal('\0', c));
        Assert.True(string.IsNullOrEmpty(form.Find(".pdk-acme-eab-hmac input").GetAttribute("value")));
        Assert.DoesNotContain("c2VjcmV0LWhtYWMta2V5", form.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("c2VjcmV0", sent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACaRefusal_IsShownWithTheCasWords()
    {
        await using DashboardTestContext context = new();
        var form = context.Render<AcmeAccountForm>(p => p
            .Add(f => f.Submit, _ => Task.FromResult<AcmeAccountResult?>(new AcmeAccountResult(IssuanceChangeStatus.Failed, Message: "invalid contact domain"))));

        form.Find(".pdk-acme-tos input").Change(true);
        await form.InvokeAsync(() => form.Find(".pdk-acme-account-submit").Click());

        Assert.Contains("The CA refused or could not be reached: invalid contact domain.", form.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADnsProvider_TakesItsTokenOnce()
    {
        await using DashboardTestContext context = new();
        DnsCredentialSubmission? sent = null;
        string? tokenSeen = null;
        var form = context.Render<DnsProviderForm>(p => p
            .Add(f => f.Submit, submission =>
            {
                sent = submission;
                tokenSeen = new string(submission.Credential);
                return Task.FromResult<DnsProviderResult?>(new DnsProviderResult(IssuanceChangeStatus.Committed, Provider()));
            }));

        form.Find(".pdk-dns-kind-desec").Click();
        form.Find(".pdk-dns-label input").Change("deSEC - home");
        form.Find(".pdk-dns-credential input").Change("  token-123  ");
        Assert.Equal("password", form.Find(".pdk-dns-credential input").GetAttribute("type"));
        Assert.Contains("desec.io", form.Markup, StringComparison.Ordinal);
        await form.InvokeAsync(() => form.Find(".pdk-dns-submit").Click());

        Assert.Equal((DnsProviderKind.DeSec, "deSEC - home", "token-123"), (sent!.Kind, sent.Label, tokenSeen));
        Assert.All(sent.Credential, c => Assert.Equal('\0', c));
        Assert.True(string.IsNullOrEmpty(form.Find(".pdk-dns-credential input").GetAttribute("value")));
        Assert.DoesNotContain("token-123", form.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADnsProvider_WithoutAToken_IsNotSent()
    {
        await using DashboardTestContext context = new();
        var calls = 0;
        var form = context.Render<DnsProviderForm>(p => p
            .Add(f => f.Submit, _ => { calls++; return Task.FromResult<DnsProviderResult?>(null); }));

        await form.InvokeAsync(() => form.Find(".pdk-dns-submit").Click());

        Assert.Equal(0, calls);
        Assert.Contains("Paste the provider's API token.", form.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false, false, "pdk-test-ok", "Credentials work")]
    [InlineData(false, false, false, "pdk-test-failed", "The token didn't work")]
    [InlineData(false, true, false, "pdk-test-denied", "Not allowed")]
    [InlineData(false, false, true, "pdk-test-running", "Testing...")]
    public async Task TheTestResult_SaysWhatHappened(bool valid, bool denied, bool testing, string marker, string headline)
    {
        await using DashboardTestContext context = new();
        DnsProviderTestResult result = new(Found: true, valid, valid ? null : "Cloudflare did not accept the token (6003)") { Denied = denied };

        var view = context.Render<DnsTestResultView>(p => p
            .Add(v => v.Result, result)
            .Add(v => v.Testing, testing)
            .Add(v => v.ProviderName, "Cloudflare"));

        Assert.Single(view.FindAll($".{marker}"));
        Assert.Contains(headline, view.Markup, StringComparison.Ordinal);
        Assert.Equal(!valid && !denied && !testing, view.Markup.Contains("(6003)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEabKey_IsReadAsTheCaShowsIt()
    {
        var key = SecretInput.Base64UrlKey("c2VjcmV0LWhtYWMta2V5".AsSpan());
        Assert.Equal("secret-hmac-key"u8.ToArray(), key);
        Assert.Null(SecretInput.Base64UrlKey("not base64url!".AsSpan()));
        Assert.Null(SecretInput.Base64UrlKey(string.Empty.AsSpan()));
    }

    private static AcmeAccountSummary Account() => new(
        AcmeAccountId.New(), "https://acme-v02.api.letsencrypt.org/directory", ["mailto:ops@example.com"], AcmeAccountStatus.Active,
        "https://acme.test/acct/1", JwsAlgorithm.ES256, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "kid-1");

    private static DnsProviderSummary Provider() => new(DnsProviderId.New(), DnsProviderKind.DeSec, "deSEC - home", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
