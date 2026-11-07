using Paduraksa.Core.Model;

namespace Paduraksa.Tests.Unit.Model;

/// <summary>The names are a storage format (DB columns, audit hashes): this pins every one of them.</summary>
public sealed class ModelNamesTests
{
    [Fact]
    public void NamesArePinned()
    {
        Assert.Equal(["system", "cli", "user", "token"], Enum.GetValues<ActorKind>().Select(k => k.ToName()));
        Assert.Equal(["system", "cli", "dashboard", "api", "mcp"], Enum.GetValues<Channel>().Select(c => c.ToName()));
        Assert.Equal(["success", "denied", "invalid", "conflict", "failed"], Enum.GetValues<AuditOutcome>().Select(o => o.ToName()));
        Assert.Equal(["acme", "uploaded", "self_signed", "local_ca", "local_ca_root"], Enum.GetValues<CertificateSource>().Select(s => s.ToName()));
        Assert.Equal(["pending", "active", "failed"], Enum.GetValues<CertificateStatus>().Select(s => s.ToName()));
    }

    [Fact]
    public void ParseRoundTripsEveryName()
    {
        Assert.All(Enum.GetValues<ActorKind>(), k => Assert.Equal(k, ModelNames.ParseActorKind(k.ToName())));
        Assert.All(Enum.GetValues<Channel>(), c => Assert.Equal(c, ModelNames.ParseChannel(c.ToName())));
        Assert.All(Enum.GetValues<AuditOutcome>(), o => Assert.Equal(o, ModelNames.ParseAuditOutcome(o.ToName())));
        Assert.All(Enum.GetValues<CertificateSource>(), s => Assert.Equal(s, ModelNames.ParseCertificateSource(s.ToName())));
        Assert.All(Enum.GetValues<CertificateStatus>(), s => Assert.Equal(s, ModelNames.ParseCertificateStatus(s.ToName())));
    }

    [Theory]
    [InlineData("")]
    [InlineData("User")] // names are exact, lowercase
    [InlineData("3")] // never numbers
    [InlineData("robot")]
    public void ParseRejectsAnythingElse(string name)
    {
        Assert.Throws<FormatException>(() => ModelNames.ParseActorKind(name));
        Assert.Throws<FormatException>(() => ModelNames.ParseChannel(name));
        Assert.Throws<FormatException>(() => ModelNames.ParseAuditOutcome(name));
        Assert.Throws<FormatException>(() => ModelNames.ParseCertificateSource(name));
        Assert.Throws<FormatException>(() => ModelNames.ParseCertificateStatus(name));
    }

    [Fact]
    public void UndefinedValuesHaveNoName()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((ActorKind)0).ToName());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Channel)99).ToName());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((AuditOutcome)(-1)).ToName());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((CertificateSource)0).ToName());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((CertificateStatus)9).ToName());
    }
}
