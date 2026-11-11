using System.Text;

using Paduraksa.Application.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>A stored document whose access-list users still carry a <c>passwordHash</c> is read into the model with a
/// <c>credentialId</c> instead, the same one every time, and the verifiers come out for the vault.</summary>
public sealed class LegacyPasswordUpgradeTests
{
    private const string Hash = "$pbkdf2-sha256$i=210000$c2FsdHNhbHRzYWx0c2FsdA$YWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWE";
    private static readonly string ListId = AccessListId.FromValue(new Guid("01900000-0000-7000-8000-000000000001")).ToString();

    private static byte[] Old(string users) => Encoding.UTF8.GetBytes(
        $$$"""{"apiVersion":"paduraksa.dev/v1","settings":{"version":0,"trustedProxies":{"mode":"generic"}},"profiles":[],"upstreams":[],"hosts":[],"rateLimitPolicies":[],"accessLists":[{"id":"{{{ListId}}}","name":"office","version":1,"basicAuth":{"users":{{{users}}}}}]}""");

    [Fact]
    public void AUsersHashBecomesACredentialAndTheVerifierComesOut()
    {
        var document = CanonicalConfigJson.Deserialize(Old($$"""[{"username":"alice","passwordHash":"{{Hash}}"}]"""), out var upgrade);

        var user = Assert.Single(document.AccessLists[0].BasicAuth!.Users);
        Assert.NotNull(user.CredentialId);
        Assert.True(upgrade.Upgraded);
        var credential = Assert.Single(upgrade.Credentials);
        Assert.Equal((user.CredentialId, Hash), (credential.Id, credential.Verifier));
        Assert.DoesNotContain("passwordHash", Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(document)), StringComparison.Ordinal);
        Assert.DoesNotContain(Hash, Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(document)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameDocumentConvertsToTheSameCredentialsAndTheConvertedOneNeedsNoConversion()
    {
        var json = Old($$"""[{"username":"alice","passwordHash":"{{Hash}}"},{"username":"bob","passwordHash":"{{Hash}}x"}]""");

        var once = CanonicalConfigJson.Deserialize(json, out var first);
        var twice = CanonicalConfigJson.Deserialize(json, out var second);

        Assert.Equal(first.Credentials.Select(c => c.Id), second.Credentials.Select(c => c.Id));
        Assert.Equal(2, first.Credentials.Select(c => c.Id).Distinct().Count());
        Assert.Equal(CanonicalConfigJson.Serialize(once), CanonicalConfigJson.Serialize(twice));
        CanonicalConfigJson.Deserialize(CanonicalConfigJson.Serialize(once), out var again);
        Assert.Equal(DocumentUpgrade.None, again);
    }

    [Fact]
    public void ACredentialIdThatIsAlreadyThereIsKeptAndTheVerifierIsImportedUnderIt()
    {
        var id = PasswordCredentialId.New();

        CanonicalConfigJson.Deserialize(Old($$"""[{"username":"alice","credentialId":"{{id}}","passwordHash":"{{Hash}}"}]"""), out var upgrade);

        Assert.Equal(id, Assert.Single(upgrade.Credentials).Id);
    }

    [Fact]
    public void AnEmptyHashIsDroppedWithoutACredential()
    {
        var document = CanonicalConfigJson.Deserialize(Old("""[{"username":"alice","passwordHash":""}]"""), out var upgrade);

        Assert.Empty(upgrade.Credentials);
        Assert.Null(Assert.Single(document.AccessLists[0].BasicAuth!.Users).CredentialId);
    }

    [Fact]
    public void AVerifierNeverShowsInTheTextOfTheConversionResult() =>
        Assert.DoesNotContain(Hash, new LegacyCredential(PasswordCredentialId.New(), Hash).ToString(), StringComparison.Ordinal);

    [Fact]
    public void WhatWasConvertedIsSaidForEachKindSoTheStartupMessageCanWordIt()
    {
        CanonicalConfigJson.Deserialize(Old($$"""[{"username":"alice","passwordHash":"{{Hash}}"}]"""), out var passwordsOnly);
        CanonicalConfigJson.Deserialize(CanonicalConfigJson.Serialize(new Paduraksa.Contracts.Config.ConfigDocument()), out var nothing);

        Assert.Equal((false, true, true), (passwordsOnly.AllowLists, passwordsOnly.Passwords, passwordsOnly.Upgraded));
        Assert.Equal((false, false, false), (nothing.AllowLists, nothing.Passwords, nothing.Upgraded));
    }
}
