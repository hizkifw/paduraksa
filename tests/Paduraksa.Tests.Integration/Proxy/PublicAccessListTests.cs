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

/// <summary>An access list through the whole public host: the 401 and its challenge, the page for a browser, and the
/// <c>Authorization</c> header the upstream does not see (unless the list passes it on).</summary>
public sealed class PublicAccessListTests : IAsyncLifetime
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
    public async Task TheUpstreamNeverSeesTheCredentialsTheListConsumed()
    {
        await using var host = await StartedAsync();

        var (response, upstream) = await SendAsync(host, "private.test", request => request.Headers.Authorization = Credentials("alice", Password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Authorization", upstream.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AListThatPassesCredentialsOnLetsTheUpstreamSeeThem()
    {
        await using var host = await StartedAsync();
        var credentials = Credentials("alice", Password);

        var (response, upstream) = await SendAsync(host, "shared.test", request => request.Headers.Authorization = credentials);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(credentials.ToString(), upstream["Authorization"]);
    }

    [Fact]
    public async Task MissingOrWrongCredentialsAre401WithTheChallengeAndNothingForwarded()
    {
        await using var host = await StartedAsync();

        foreach (var configure in new Action<HttpRequestMessage>?[]
        {
            null,
            request => request.Headers.Authorization = Credentials("alice", "not the password"),
            request => request.Headers.Authorization = Credentials("mallory", Password),
        })
        {
            var (response, upstream) = await SendAsync(host, "private.test", configure);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Basic realm=\"Staff area\", charset=\"UTF-8\"", Assert.Single(response.Headers.WwwAuthenticate).ToString());
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.Empty(upstream);
        }
    }

    [Fact]
    public async Task ABrowserGetsThePageAndAnythingElseThePlainText()
    {
        await using var host = await StartedAsync();

        using var client = EdgeClient.Create(out _);
        using var browser = EdgeClient.Request(host, "http", "private.test", "/", null);
        browser.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
        using var page = await client.SendAsync(browser, TestContext.Current.CancellationToken);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
        Assert.Equal("text/html; charset=utf-8", page.Content.Headers.ContentType!.ToString());
        Assert.Contains("<h1>401 Unauthorized</h1>", html, StringComparison.Ordinal);
        Assert.Single(page.Headers.WwwAuthenticate);

        using var curl = EdgeClient.Request(host, "http", "private.test", "/", null);
        using var plain = await client.SendAsync(curl, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, plain.StatusCode);
        Assert.StartsWith("text/plain", plain.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        Assert.Equal("authentication required\n", await plain.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<PaduraksaHost> StartedAsync()
    {
        var host = TestHost.Create();
        await host.StartAsync(TestContext.Current.CancellationToken);
        AccessListSpec office = await ListAsync(host, "office", "Staff area", passUpstream: false);
        AccessListSpec shared = await ListAsync(host, "shared", "Shared area", passUpstream: true);
        ProxyConfigModel model = new()
        {
            Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("echo"), Destinations = [new(TestIds.Destination("echo-1"), Upstream.Address)] }],
            AccessLists = [office, shared],
            Hosts =
            [
                Guarded("private", "private.test", office),
                Guarded("shared", "shared.test", shared),
            ],
        };
        var published = await host.Shared.SnapshotPublisher.PublishAsync(Compile(host, model), TestContext.Current.CancellationToken);
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));
        return host;
    }

    // The password goes into the host's own vault, and the list holds the handle the vault gives back: the real path from a document to the gate.
    private static async Task<AccessListSpec> ListAsync(PaduraksaHost host, string label, string realm, bool passUpstream)
    {
        var credentials = host.Shared.PasswordCredentials;
        var id = PasswordCredentialId.New();
        await credentials.SetAsync(id, Password.AsMemory(), TestContext.Current.CancellationToken);
        return new()
        {
            Id = TestIds.AccessList(label),
            Name = label,
            Realm = realm,
            PassCredentialsUpstream = passUpstream,
            Users = [new AccessListUserSpec("alice", credentials.Open(id))],
        };
    }

    private static HostSpec Guarded(string label, string name, AccessListSpec list) => new()
    {
        Id = TestIds.Host(label),
        Names = [name],
        Routes = [new RouteSpec { Id = TestIds.Route(label + "-root"), Upstream = TestIds.Upstream("echo"), Policies = [new AccessListPolicySpec(list.Id)] }],
    };

    private static AuthenticationHeaderValue Credentials(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    private static async Task<(HttpResponseMessage Response, Dictionary<string, string> Upstream)> SendAsync(
        PaduraksaHost host, string hostHeader, Action<HttpRequestMessage>? configure = null)
    {
        using var client = EdgeClient.Create(out _);
        using var request = EdgeClient.Request(host, "http", hostHeader, "/", null);
        configure?.Invoke(request);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, response.IsSuccessStatusCode ? EdgeUpstream.Parse(body) : []);
    }
}
