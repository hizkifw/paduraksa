using System.Reflection;
using System.Text.RegularExpressions;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using static Paduraksa.Tests.Architecture.SolutionLayers;

namespace Paduraksa.Tests.Architecture;

/// <summary>PLAN §2, §5.2, §8: the identity DTOs leak no hash, no secret reference and no raw secret by accident; the TOTP capability returns no secret.</summary>
public sealed partial class IdentityContractTests
{
    private const string IdentityNamespace = "Paduraksa.Contracts.Identity";

    // Names that mean "stored credential material": a hash, a vault reference, a TOTP secret. (Request records may carry the
    // password a person just typed; they print redacted, which UnitTests/RolePermissionTests holds.)
    [GeneratedRegex("hash|secretid|totpsecret|salt", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StoredCredentialName();

    [Fact]
    public void IdentityDtosExposeNoHashOrSecretReference()
    {
        var types = LoadAssembly(Layer.Contracts).GetExportedTypes().Where(t => t.Namespace == IdentityNamespace).ToList();

        Assert.NotEmpty(types);
        var leaks = types
            .SelectMany(t => t.GetProperties().Select(p => (Type: t, p.Name, p.PropertyType)))
            .Where(m => StoredCredentialName().IsMatch(m.Name) || m.PropertyType == typeof(SecretId) || m.PropertyType == typeof(SecretId?))
            .Select(m => $"{m.Type.Name}.{m.Name}")
            .ToList();
        Assert.True(leaks.Count == 0, "Stored credential material in the identity DTOs: " + string.Join(", ", leaks));
    }

    [Fact]
    public void IdentityDtosAreSealedRecords()
    {
        var types = LoadAssembly(Layer.Contracts).GetExportedTypes()
            .Where(t => t.Namespace == IdentityNamespace && !t.IsSubclassOf(typeof(System.Text.Json.Serialization.JsonSerializerContext)));

        Assert.All(types, t => Assert.True(t.IsSealed && t.GetMethod("<Clone>$") is not null, $"{t.Name} is not a sealed record"));
    }

    [Fact]
    public void TheTotpCapabilityReturnsNoSecretAfterEnrolment()
    {
        var returns = typeof(ITotpAuthenticator).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(m => m.Name, m => m.ReturnType);

        Assert.Equal(typeof(ValueTask<TotpEnrollment>), returns["BeginEnrollmentAsync"]);
        Assert.All(returns.Where(r => r.Key != "BeginEnrollmentAsync"), r => Assert.Equal(typeof(ValueTask<bool>), r.Value));
        // The one egress is the provisioning URI, a Uri, at enrolment; the record has no other member that could hold a secret.
        Assert.Equal(["ProvisioningUri", "SecretId"], typeof(TotpEnrollment).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract").Order());
    }

    [Fact]
    public void ApiTokenAndSessionIdsAreTypedAndDistinctFromTheirSecrets()
    {
        Assert.Equal("ses", SessionId.Prefix);
        Assert.Equal("tok", ApiTokenId.Prefix);
        Assert.Equal("usr", UserId.Prefix);
        Assert.Equal(Paduraksa.Core.Ids.IdKind.UuidV4, SessionId.Kind);
    }
}
