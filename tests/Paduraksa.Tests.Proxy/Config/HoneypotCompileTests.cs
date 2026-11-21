using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Honeypot;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Config;

/// <summary>A host's trap through the compiler: its site key, its prefix, the robots.txt line that leads away from it, and what it refuses.</summary>
public sealed class HoneypotCompileTests
{
    private static readonly HostHoneypotSpec On = new() { Enabled = true };

    private static ProxyConfigModel Model(params HostSpec[] hosts) => new() { Upstreams = [Upstream("web", "http://127.0.0.1:1/")], Hosts = hosts };

    private static HostSpec Host(string label, HostHoneypotSpec? honeypot = null, HostRobotsSpec? robots = null) =>
        ProxyHost(label, label + ".test", Route(label + "-root", "web")) with { Honeypot = honeypot ?? On, Robots = robots ?? new HostRobotsSpec { Mode = RobotsMode.DisallowAll } };

    private static CompiledHost Compiled(RuntimeSnapshot snapshot, string label) => snapshot.Hosts.Find(label + ".test", out _)!;

    [Fact]
    public void AHostWithoutTheSwitchHasNoTrap()
    {
        var rig = new HoneypotRig();
        var snapshot = Compile(Model(Host("a", HostHoneypotSpec.Off)), rig.Options);

        Assert.Null(Compiled(snapshot, "a").Honeypot);
        Assert.Empty(rig.Factory.Created);
    }

    [Fact]
    public void TheSiteKeyIsHmacOverThePrefixAndTheHostIdUnderTheProofOfWorkKey()
    {
        var rig = new HoneypotRig();
        var snapshot = Compile(Model(Host("a"), Host("b")), rig.Options);

        var (key, options) = Assert.Single(rig.Factory.Created, c => c.Options.Prefix == Compiled(snapshot, "a").Honeypot!.Prefix);
        var expected = new byte[32];
        Span<byte> input = [.. "paduraksa-honeypot-site-v1"u8, .. HostScope("a")];
        _ = new TestMacKey().Sign(input, expected);
        Assert.Equal(expected, key);
        Assert.Equal(HoneypotLimits.DefaultPowShare, options.PowShare);

        // A host's prefix is its own, and the same host keeps it across compiles.
        Assert.NotEqual(Compiled(snapshot, "a").Honeypot!.Prefix, Compiled(snapshot, "b").Honeypot!.Prefix);
        var again = Compile(Model(Host("a"), Host("b")), new HoneypotRig().Options);
        Assert.Equal(Compiled(snapshot, "a").Honeypot!.Prefix, Compiled(again, "a").Honeypot!.Prefix);
    }

    [Fact]
    public void TheNamesTheDashboardShowsAreTheOnesTheCompilerDerives_AndNoneWithoutTheKey()
    {
        var rig = new HoneypotRig();
        var snapshot = Compile(Model(Host("a")), rig.Options);

        Assert.True(SnapshotCompiler.TryDeriveHoneypotNames(rig.Options, TestIds.Host("a"), out var prefix, out var siteName));
        Assert.Equal(Compiled(snapshot, "a").Honeypot!.Prefix, prefix);
        Assert.Equal(rig.Factory.Created.Single().Options.SiteName, siteName);

        Assert.False(SnapshotCompiler.TryDeriveHoneypotNames(new HoneypotRig(withKey: false).Options, TestIds.Host("a"), out prefix, out siteName));
        Assert.Empty(prefix);
        Assert.Empty(siteName);
    }

    [Fact]
    public void AConfiguredPathAndNameWinOverTheDerivedOnes()
    {
        var rig = new HoneypotRig();
        var snapshot = Compile(Model(Host("a", On with { Path = "/notes/", SiteName = "Field Notes", PowShare = 0, PowDifficulty = 12, NoIndex = false })), rig.Options);

        var trap = Compiled(snapshot, "a").Honeypot!;
        Assert.Equal("/notes/", trap.Prefix);
        Assert.False(trap.NoIndex);
        Assert.Equal(12, trap.PowDifficulty);
        var (_, options) = Assert.Single(rig.Factory.Created);
        Assert.Equal(new HoneypotSiteOptions("/notes/", "Field Notes", 0), options);

        // No page is ever behind a challenge, so there is no challenge page to build.
        Assert.Null(trap.Page);
    }

    [Fact]
    public void ADisabledHostServesNoTrap()
    {
        var rig = new HoneypotRig();

        var snapshot = Compile(Model(Host("a") with { Enabled = false }), rig.Options);

        Assert.Empty(snapshot.HostsByIndex);
        Assert.Empty(rig.Factory.Created);
    }

    [Fact]
    public void AnAdminHostServesNoTrapWhateverItsSpecSays()
    {
        var rig = new HoneypotRig();
        HostSpec admin = new() { Id = TestIds.Host("admin"), Names = ["admin.test"], Kind = HostKind.Admin, Https = true, ForceHttps = true, Honeypot = On };

        var snapshot = Compile(Model(admin), rig.Options with { AdminUpstream = true });

        Assert.Null(Compiled(snapshot, "admin").Honeypot);
        Assert.Empty(rig.Factory.Created);
    }

    [Fact]
    public void WithoutTheKeyAConfiguredPathAnswersAndADerivedOneIsOff()
    {
        var rig = new HoneypotRig(withKey: false);
        var snapshot = Compile(Model(Host("a", On with { Path = "/notes/" }), Host("b")), rig.Options);

        var configured = Compiled(snapshot, "a").Honeypot!;
        Assert.Equal("/notes/", configured.Prefix);
        Assert.Null(configured.Site);
        Assert.Null(Compiled(snapshot, "b").Honeypot);
        Assert.Empty(rig.Factory.Created);
    }

    [Theory]
    [InlineData("/", "start and end")]
    [InlineData("/a b/", "can only hold")]
    [InlineData("/.well-known/x/", ".well-known")]
    public void AnInvalidPathFailsTheCompile(string path, string reason)
    {
        var result = SnapshotCompiler.Compile(Model(Host("a", On with { Path = path })), new HoneypotRig().Options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void ValuesOutOfBoundsFailTheCompile()
    {
        var result = SnapshotCompiler.Compile(
            Model(Host("a", On with { PowShare = 101, PowDifficulty = 3, SiteName = " " })), new HoneypotRig().Options);

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.Errors.Count(e => e.Message.StartsWith("the honeypot's", StringComparison.Ordinal)));
    }

    [Fact]
    public void ASiteThatDoesNotKeepThePrefixItWasMadeForFailsTheCompile()
    {
        var rig = new HoneypotRig();
        var options = rig.Options with { Honeypot = new HoneypotRuntime(rig.Sink, new MisleadingFactory()) };

        var result = SnapshotCompiler.Compile(Model(Host("a", On with { Path = "/notes/" })), options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Message.Contains("isn't the one it was made for", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDefaultRuntimeBuildsTheGeneratorsSiteUnderItsDerivedPrefix()
    {
        var options = new HoneypotRig().Options with { Honeypot = new HoneypotRuntime() };

        var trap = Compiled(Compile(Model(Host("a")), options), "a").Honeypot!;

        var site = Assert.IsType<HoneypotSite>(trap.Site);
        Assert.Equal(trap.Prefix, site.Prefix);
        Assert.Equal(HoneypotPageKind.Index, site.Resolve(trap.Prefix).Kind);
    }

    [Fact]
    public void RobotsTxtDisallowsThePrefixInEveryGroupOfEachMode()
    {
        var rig = new HoneypotRig();
        var snapshot = Compile(
            Model(
                Host("all"),
                Host("ai", robots: new HostRobotsSpec { Mode = RobotsMode.BlockAiCrawlers }),
                Host("custom", robots: new HostRobotsSpec { Mode = RobotsMode.Custom, CustomText = "User-agent: a\r\nDisallow: /x\r\n\r\nUser-agent: b\r\nAllow: /\r\n" }),
                Host("pass", robots: new HostRobotsSpec { Mode = RobotsMode.Passthrough })),
            rig.Options);

        var all = Compiled(snapshot, "all");
        Assert.Equal($"User-agent: *\nDisallow: /\nDisallow: {all.Honeypot!.Prefix}\n", Text(all.RobotsTxt!));

        var ai = Compiled(snapshot, "ai");
        var aiText = Text(ai.RobotsTxt!);
        Assert.Contains($"Disallow: /\nDisallow: {ai.Honeypot!.Prefix}\n\nUser-agent: *\nDisallow:\nDisallow: {ai.Honeypot.Prefix}\n", aiText, StringComparison.Ordinal);
        Assert.Equal(2, aiText.Split($"Disallow: {ai.Honeypot.Prefix}", StringSplitOptions.None).Length - 1);

        var custom = Compiled(snapshot, "custom");
        Assert.Equal(
            $"User-agent: a\nDisallow: /x\nDisallow: {custom.Honeypot!.Prefix}\n\nUser-agent: b\nAllow: /\nDisallow: {custom.Honeypot.Prefix}\n",
            Text(custom.RobotsTxt!));

        // Nothing adds a line to a file the upstream serves.
        Assert.Null(Compiled(snapshot, "pass").RobotsTxt);
        Assert.Equal(HoneypotEntry.Prefix, Compiled(snapshot, "pass").Honeypot!.EntryByPrefix);
        Assert.Equal(HoneypotEntry.Robots, all.Honeypot.EntryByPrefix);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; GPTBot/1.1)")]
    [InlineData("SomeOtherCrawler/2.0")]
    public void TheAnalyticsReadTheServedFileSoATrapVisitIsABotIgnoringRobots(string userAgent)
    {
        // The drain parses the bytes the gate serves, so the trap line is what makes a request under the prefix count as ignoring robots.txt.
        var rig = new HoneypotRig();
        var host = Compiled(Compile(Model(Host("ai", robots: new HostRobotsSpec { Mode = RobotsMode.BlockAiCrawlers })), rig.Options), "ai");

        var rules = Paduraksa.Proxy.Analytics.RobotsRules.FromBody(host.RobotsTxt!.Body);

        Assert.False(rules.IsAllowed(userAgent, host.Honeypot!.Prefix));
        Assert.False(rules.IsAllowed(userAgent, host.Honeypot.Prefix + "post-1/"));

        // The rest of the preset is unchanged: only the AI crawlers are kept out of everything.
        Assert.Equal(!userAgent.Contains("GPTBot", StringComparison.Ordinal), rules.IsAllowed(userAgent, "/"));
    }

    [Fact]
    public void AHostWithoutATrapKeepsItsSharedRobotsAnswer()
    {
        var rig = new HoneypotRig();
        var snapshot = Compile(Model(Host("a", HostHoneypotSpec.Off)), rig.Options);

        Assert.Same(RobotsResponses.DisallowAll, Compiled(snapshot, "a").RobotsTxt);
    }

    private static byte[] HostScope(string label)
    {
        var scope = new byte[16];
        TestIds.Host(label).Value.TryWriteBytes(scope, bigEndian: true, out _);
        return scope;
    }

    private static string Text(PrecomputedResponse response) => System.Text.Encoding.UTF8.GetString(response.Body);

    private sealed class MisleadingFactory : IHoneypotSiteFactory
    {
        public string DerivePrefix(ReadOnlySpan<byte> siteKey) => "/derived/";

        public string DeriveSiteName(ReadOnlySpan<byte> siteKey) => "Name";

        public IHoneypotSite Create(ReadOnlySpan<byte> siteKey, HoneypotSiteOptions options) => new FakeHoneypotSite("/other/");
    }
}
