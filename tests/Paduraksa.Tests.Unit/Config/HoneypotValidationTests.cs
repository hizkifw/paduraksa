using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Paduraksa.Application.Config.ConfigErrorCodes;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>The trap's values at the layer that sets them, and which hosts may have one.</summary>
public sealed class HoneypotValidationTests
{
    [Theory]
    [InlineData("/archive-k7f3/", true)]
    [InlineData("/blog/2024_notes.v2/", true)]
    [InlineData("/a/b/", true)]
    [InlineData("/", false)]
    [InlineData("", false)]
    [InlineData("/archive", false)]
    [InlineData("archive/", false)]
    [InlineData("/archive//", false)]
    [InlineData("/archive/../x/", false)]
    [InlineData("/archive./", false)]
    [InlineData("/arch ive/", false)]
    [InlineData("/archive%2F/", false)]
    [InlineData("/archive?x/", false)]
    [InlineData("/archive;x/", false)]
    [InlineData("/archive\\x/", false)]
    [InlineData("/café/", false)]
    [InlineData("/.well-known/", false)]
    [InlineData("/x/.well-known/y/", false)]
    [InlineData("/.WELL-KNOWN/paduraksa/", false)]
    public void ThePathIsACanonicalPrefix(string path, bool valid) =>
        Assert.Equal(valid ? [] : [("/settings/defaults/honeypot/path", HostHoneypotPathInvalid)], Errors(new HoneypotSettings { Path = path }));

    [Fact]
    public void ThePathHasALengthLimit()
    {
        var longest = "/" + new string('a', HoneypotLimits.MaxPathLength - 2) + "/";
        Assert.Empty(Errors(new HoneypotSettings { Path = longest }));
        Assert.Equal(
            [("/settings/defaults/honeypot/path", HostHoneypotPathInvalid)],
            Errors(new HoneypotSettings { Path = "/" + new string('a', HoneypotLimits.MaxPathLength - 1) + "/" }));
    }

    [Theory]
    [InlineData("The Daily Post", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("two\nlines", false)]
    public void TheSiteNameIsOneLineOfText(string name, bool valid) =>
        Assert.Equal(valid ? [] : [("/settings/defaults/honeypot/siteName", HostHoneypotSiteNameInvalid)], Errors(new HoneypotSettings { SiteName = name }));

    [Fact]
    public void TheSiteNameHasALengthLimit()
    {
        Assert.Empty(Errors(new HoneypotSettings { SiteName = new string('a', HoneypotLimits.MaxSiteNameLength) }));
        Assert.Equal(
            [("/settings/defaults/honeypot/siteName", HostHoneypotSiteNameInvalid)],
            Errors(new HoneypotSettings { SiteName = new string('a', HoneypotLimits.MaxSiteNameLength + 1) }));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(100, true)]
    [InlineData(-1, false)]
    [InlineData(101, false)]
    public void ThePowShareIsAPercentage(int share, bool valid) =>
        Assert.Equal(valid ? [] : [("/settings/defaults/honeypot/powShare", HostHoneypotPowShareInvalid)], Errors(new HoneypotSettings { PowShare = share }));

    [Theory]
    [InlineData(ProofOfWorkLimits.MinDifficulty, true)]
    [InlineData(ProofOfWorkLimits.MaxDifficulty, true)]
    [InlineData(ProofOfWorkLimits.MinDifficulty - 1, false)]
    [InlineData(ProofOfWorkLimits.MaxDifficulty + 1, false)]
    public void ThePowDifficultyIsTheBrowserChecksRange(int difficulty, bool valid) =>
        Assert.Equal(valid ? [] : [("/settings/defaults/honeypot/powDifficulty", HostHoneypotPowDifficultyInvalid)], Errors(new HoneypotSettings { PowDifficulty = difficulty }));

    [Fact]
    public void AnAdminHostCannotSwitchTheTrapOn()
    {
        var document = Valid();
        var admin = new HostConfig(Id<HostId>(99), ["admin.example.com"], Kind: HostKind.Admin, Settings: new HostSettings { Honeypot = new HoneypotSettings { Enabled = true } });
        document = document with { Hosts = [.. document.Hosts, admin] };

        var errors = ConfigValidator.Validate(document).Select(e => (e.Path, e.Code));

        Assert.Contains(errors, e => e.Code == HostHoneypotAdminHost && e.Path.EndsWith("/settings/honeypot/enabled", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAdminHostMayInheritAnOnSwitch()
    {
        var document = Valid();
        var admin = new HostConfig(Id<HostId>(99), ["admin.example.com"], Kind: HostKind.Admin);
        document = document with
        {
            Settings = document.Settings with { Defaults = new HostSettings { Honeypot = new HoneypotSettings { Enabled = true } } },
            Hosts = [.. document.Hosts, admin],
        };

        Assert.DoesNotContain(ConfigValidator.Validate(document), e => e.Code == HostHoneypotAdminHost);
    }

    private static IEnumerable<(string Path, string Code)> Errors(HoneypotSettings honeypot)
    {
        var document = Valid();
        document = document with { Settings = document.Settings with { Defaults = new HostSettings { Honeypot = honeypot } } };
        return ConfigValidator.Validate(document).Select(e => (e.Path, e.Code));
    }
}
