using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Integration.Proxy.PublicProxyTests;

namespace Paduraksa.Tests.Integration.Proxy;

/// <summary>A rule set through the whole public host and a real socket: what a deny, a drop and an allow look like to a client,
/// the spellings of a path a rule still catches, robots.txt, and that a refusal is answered before a password is checked.</summary>
public sealed class PublicRuleSetTests : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";

    private EdgeUpstream? _upstream;

    private EdgeUpstream Upstream => _upstream!;

    public async ValueTask InitializeAsync() => _upstream = await EdgeUpstream.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_upstream is not null)
        {
            await _upstream.DisposeAsync();
        }
    }

    [Fact]
    public async Task ADenyIsA403ThatTheUpstreamNeverSeesAndAnAllowCarvesAnException()
    {
        await using var host = await StartedAsync();

        var (denied, deniedUpstream) = await SendAsync(host, "rules.test", "/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("forbidden\n", await denied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(deniedUpstream);

        var (allowed, _) = await SendAsync(host, "rules.test", "/admin/public/logo.png");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        var (other, _) = await SendAsync(host, "rules.test", "/index.html");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        // The method is a condition of its own.
        var (trace, _) = await SendAsync(host, "rules.test", "/index.html", request => request.Method = HttpMethod.Trace);
        Assert.Equal(HttpStatusCode.Forbidden, trace.StatusCode);
    }

    [Fact]
    public async Task ABrowserGetsThePageAndAnythingElseThePlainText()
    {
        await using var host = await StartedAsync();

        var (page, _) = await SendAsync(host, "rules.test", "/admin", request => request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml"));
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Equal("text/html; charset=utf-8", page.Content.Headers.ContentType!.ToString());
        Assert.Contains("<h1>403 Forbidden</h1>", html, StringComparison.Ordinal);
        Assert.Contains("accept that request", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUserAgentRuleRefusesByNameIgnoringCaseAndTheEmptyFlagRefusesAMissingOne()
    {
        await using var host = await StartedAsync();

        var (named, _) = await SendAsync(host, "rules.test", "/", userAgent: "Mozilla/5.0 SQLMap/1.7");
        Assert.Equal(HttpStatusCode.Forbidden, named.StatusCode);

        var (browser, _) = await SendAsync(host, "rules.test", "/");
        Assert.Equal(HttpStatusCode.OK, browser.StatusCode);

        var (none, _) = await SendAsync(host, "rules.test", "/", userAgent: null);
        Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);
    }

    [Fact]
    public async Task ADropClosesTheConnectionWithoutAnAnswer()
    {
        await using var host = await StartedAsync();

        Assert.Equal(
            string.Empty,
            await EdgeClient.RawHttpAsync(host, Encoding.ASCII.GetBytes("GET /x/.env HTTP/1.1\r\nHost: rules.test\r\nUser-Agent: Firefox\r\nConnection: close\r\n\r\n")));
    }

    [Theory]
    [InlineData("//admin")]
    [InlineData("/ADMIN")]
    [InlineData("/admin;jsessionid=1")]
    [InlineData("/%5Cadmin")]
    [InlineData("/public/..%5Cadmin")]
    [InlineData("/admin%2Fusers")]
    [InlineData("/%61dmin")]
    [InlineData("/./admin/.")]
    public async Task ASpellingOfAPathTheUpstreamWouldReadAsItIsStillCaughtByTheRule(string target)
    {
        await using var host = await StartedAsync();

        var response = await EdgeClient.RawHttpAsync(host, Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: rules.test\r\nUser-Agent: Firefox\r\nConnection: close\r\n\r\n"));

        Assert.StartsWith("HTTP/1.1 403", response, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task RobotsTxtIsReadableEvenWhenEverythingElseIsDenied(string method)
    {
        await using var host = await StartedAsync();

        var (robots, upstream) = await SendAsync(host, "closed.test", "/robots.txt", request => request.Method = new HttpMethod(method));
        Assert.Equal(HttpStatusCode.OK, robots.StatusCode);
        if (method == "GET")
        {
            Assert.NotEmpty(upstream);
        }

        var (other, _) = await SendAsync(host, "closed.test", "/index.html");
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task ARefusalIsAnsweredBeforeAPasswordIsEverChecked()
    {
        await using var host = await StartedAsync();
        var stats = host.Shared.AccessLists.Stats;
        var wrong = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:not the password")));

        // A denied path with a wrong password: 403, and no PBKDF2 ran.
        var before = stats.Verifications;
        var (denied, _) = await SendAsync(host, "guarded.test", "/admin", request => request.Headers.Authorization = wrong);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(before, stats.Verifications);

        // An open path with the same password: the list asks, and the check runs.
        var (checkedPassword, _) = await SendAsync(host, "guarded.test", "/index.html", request => request.Headers.Authorization = wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, checkedPassword.StatusCode);
        Assert.True(stats.Verifications > before);
    }

    private async Task<PaduraksaHost> StartedAsync()
    {
        var host = TestHost.Create();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var credentials = host.Shared.PasswordCredentials;
        var credential = PasswordCredentialId.New();
        await credentials.SetAsync(credential, Password.AsMemory(), TestContext.Current.CancellationToken);
        AccessListSpec office = new()
        {
            Id = TestIds.AccessList("office"),
            Name = "office",
            Realm = "Staff area",
            Users = [new AccessListUserSpec("alice", credentials.Open(credential))],
        };

        var rules = TestIds.RuleSet("rules");
        var closed = TestIds.RuleSet("closed");
        ProxyConfigModel model = new()
        {
            Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("echo"), Destinations = [new(TestIds.Destination("echo-1"), Upstream.Address)] }],
            AccessLists = [office],
            RuleSets =
            [
                new RuleSetSpec
                {
                    Id = rules,
                    Name = "rules",
                    Rules =
                    [
                        new RuleSpec { Action = RuleAction.Allow, PathKind = RulePathKind.Prefix, Pattern = "/admin/public" },
                        new RuleSpec { Action = RuleAction.Deny, PathKind = RulePathKind.Prefix, Pattern = "/admin" },
                        new RuleSpec { Action = RuleAction.Drop, PathKind = RulePathKind.Glob, Pattern = "/**/.env" },
                        new RuleSpec { Action = RuleAction.Deny, Methods = ["TRACE"] },
                        new RuleSpec { Action = RuleAction.Deny, UserAgentContains = ["sqlmap"], UserAgentEmpty = true },
                    ],
                },
                new RuleSetSpec { Id = closed, Name = "closed", Rules = [new RuleSpec { Action = RuleAction.Deny, PathKind = RulePathKind.Prefix, Pattern = "/" }] },
            ],
            Hosts =
            [
                Guarded("rules", "rules.test", [new RuleSetPolicySpec(rules)]),
                Guarded("closed", "closed.test", [new RuleSetPolicySpec(closed)]),

                // The order the mapping gives a list with passwords: the rules first, so a refusal costs no PBKDF2.
                Guarded("guarded", "guarded.test", [new RuleSetPolicySpec(rules), new AccessListPolicySpec(office.Id)]),
            ],
        };
        var published = await host.Shared.SnapshotPublisher.PublishAsync(Compile(host, model), TestContext.Current.CancellationToken);
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));
        return host;
    }

    private static HostSpec Guarded(string label, string name, PolicySpec[] policies) => new()
    {
        Id = TestIds.Host(label),
        Names = [name],
        Routes = [new RouteSpec { Id = TestIds.Route(label + "-root"), Upstream = TestIds.Upstream("echo"), Policies = policies }],
    };

    // Every request carries a browser's user agent unless the test says otherwise, so the user-agent rules aren't what these hit.
    private static async Task<(HttpResponseMessage Response, Dictionary<string, string> Upstream)> SendAsync(
        PaduraksaHost host, string hostHeader, string path, Action<HttpRequestMessage>? configure = null, string? userAgent = "Mozilla/5.0 Firefox/130.0")
    {
        using var client = EdgeClient.Create(out _);
        using var request = EdgeClient.Request(host, "http", hostHeader, path, null);
        if (userAgent is not null)
        {
            request.Headers.UserAgent.ParseAdd(userAgent);
        }

        configure?.Invoke(request);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, response.IsSuccessStatusCode && request.Method == HttpMethod.Get ? EdgeUpstream.Parse(body) : []);
    }
}
