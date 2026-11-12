using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Paduraksa.Admin.Json;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Integration.Hosting;
using static Paduraksa.Tests.Integration.Admin.AdminApiHarness;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// Notification channels over the admin API on a real host, with a real vault: who may use them, that a channel's secret goes into the vault and
/// is never in a response, the OpenAPI document or the audit log, and that "send test" reaches a fake webhook with its signature.
/// </summary>
public sealed class NotificationApiTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private const string HookPath = "/hooks/very-secret-path-123";
    private const string SigningKey = "very-secret-signing-key";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Json(string json) => JsonBody(json);

    [Theory]
    [InlineData("owner", HttpStatusCode.OK)]
    [InlineData("admin", HttpStatusCode.OK)]
    [InlineData("operator", HttpStatusCode.OK)]
    [InlineData("viewer", HttpStatusCode.OK)]
    [InlineData("auditor", HttpStatusCode.OK)]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    public async Task ReadingChannelsAndHealthNeedsOnlyTheReadPermissions(string? caller, HttpStatusCode expected)
    {
        using var channels = await shared.Api.GetAsync("/api/v1/notification-channels", caller);
        using var health = await shared.Api.GetAsync("/api/v1/upstreams/health", caller);

        Assert.Equal(expected, channels.StatusCode);
        Assert.Equal(expected, health.StatusCode);
    }

    [Theory]
    [InlineData("owner", HttpStatusCode.Created)]
    [InlineData("admin", HttpStatusCode.Created)]
    [InlineData("operator", HttpStatusCode.Forbidden)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    public async Task OnlyProvidersWriteMayAddAChannel(string caller, HttpStatusCode expected)
    {
        using var response = await shared.Api.SendAsync(HttpMethod.Post, "/api/v1/notification-channels", caller, content: Json(
            """{"kind":"webhook","label":"hook","events":["upstream.down"],"secret":"https://hooks.example.com/in"}"""));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ASecretGoesIntoTheVaultAndNeverComesBackAndATestReachesTheServerSigned()
    {
        var api = shared.Api;
        await using var hook = await RecordingHook.StartAsync();
        List<string> bodies = [];

        using var created = await api.SendAsync(HttpMethod.Post, "/api/v1/notification-channels", content: Json(
            $$"""{"kind":"webhook","label":"ops hook","events":["upstream.down","certificate.expiring"],"secret":"{{hook.Url}}{{HookPath}}","signingSecret":"{{SigningKey}}"}"""));
        bodies.Add(await created.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var channel = JsonSerializer.Deserialize(bodies[0], AdminJsonContext.Default.NotificationChannelSummary)!;
        Assert.Equal((NotificationChannelKind.Webhook, true, true), (channel.Kind, channel.HasSecret, channel.HasSigningSecret));
        Assert.Equal([NotificationEventKind.UpstreamDown, NotificationEventKind.CertificateExpiring], channel.Events);
        Assert.Equal($"/api/v1/notification-channels/{channel.Id}", created.Headers.Location?.OriginalString);

        using var list = await api.GetAsync("/api/v1/notification-channels");
        bodies.Add(await list.Content.ReadAsStringAsync(Ct));
        using var one = await api.GetAsync($"/api/v1/notification-channels/{channel.Id}");
        bodies.Add(await one.Content.ReadAsStringAsync(Ct));
        using var replaced = await api.SendAsync(HttpMethod.Put, $"/api/v1/notification-channels/{channel.Id}", content: Json(
            """{"kind":"webhook","label":"ops hook 2","events":["upstream.up"]}"""));
        bodies.Add(await replaced.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.True(JsonSerializer.Deserialize(bodies[^1], AdminJsonContext.Default.NotificationChannelSummary)!.HasSecret);

        using var test = await api.SendAsync(HttpMethod.Post, $"/api/v1/notification-channels/{channel.Id}/test");
        bodies.Add(await test.Content.ReadAsStringAsync(Ct));
        var outcome = JsonSerializer.Deserialize(bodies[^1], AdminJsonContext.Default.NotificationTestOutcome)!;
        Assert.True(outcome.Delivered, outcome.Error);
        var received = Assert.Single(hook.Received);
        Assert.Equal(HookPath, received.Path);
        Assert.Equal("test", JsonDocument.Parse(received.Body).RootElement.GetProperty("event").GetString());
        Assert.StartsWith("sha256=", received.Signature, StringComparison.Ordinal);

        using var after = await api.GetAsync($"/api/v1/notification-channels/{channel.Id}");
        var summary = JsonSerializer.Deserialize(await after.Content.ReadAsStringAsync(Ct), AdminJsonContext.Default.NotificationChannelSummary)!;
        Assert.NotNull(summary.LastSentAt);
        Assert.Null(summary.LastError);

        using var audit = await api.GetAsync("/api/v1/audit?action=notification.*");
        var auditBody = await audit.Content.ReadAsStringAsync(Ct);
        bodies.Add(auditBody);
        foreach (var action in new[] { "notification.channel.create", "notification.channel.update", "notification.channel.test" })
        {
            Assert.Contains(action, auditBody, StringComparison.Ordinal);
        }

        using var openApi = await api.GetAsync("/api/v1/openapi.json", null);
        bodies.Add(await openApi.Content.ReadAsStringAsync(Ct));
        Assert.Contains("notification-channels", bodies[^1], StringComparison.Ordinal);
        foreach (var body in bodies)
        {
            Assert.DoesNotContain(HookPath, body, StringComparison.Ordinal);
            Assert.DoesNotContain(SigningKey, body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"secretId\"", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"signingSecretId\"", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sec_", body, StringComparison.Ordinal);
        }

        using var deleted = await api.SendAsync(HttpMethod.Delete, $"/api/v1/notification-channels/{channel.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var gone = await api.GetAsync($"/api/v1/notification-channels/{channel.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task AChannelThatCannotWorkIsRefusedWithoutEchoingTheSecret()
    {
        var api = shared.Api;

        using var bad = await api.SendAsync(HttpMethod.Post, "/api/v1/notification-channels", content: Json(
            """{"kind":"discord","label":"d","events":["upstream.down"],"secret":"https://example.com/not-a-discord-hook-xyz"}"""));
        using var unknownKind = await api.SendAsync(HttpMethod.Post, "/api/v1/notification-channels", content: Json(
            """{"kind":"sms","label":"d","events":["upstream.down"],"secret":"x"}"""));
        using var unknownEvent = await api.SendAsync(HttpMethod.Post, "/api/v1/notification-channels", content: Json(
            """{"kind":"webhook","label":"d","events":["upstream.sideways"],"secret":"https://hooks.example.com/in"}"""));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Contains("notification.url.invalid", await bad.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-discord-hook-xyz", await bad.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, unknownKind.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownEvent.StatusCode);
        using var missing = await api.SendAsync(HttpMethod.Post, $"/api/v1/notification-channels/{NotificationChannelId.New()}/test");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task AnUnknownUpstreamHasNoHealthAndAHealthReadIsAList()
    {
        using var unknown = await shared.Api.GetAsync($"/api/v1/upstreams/{UpstreamId.New()}/health");
        using var all = await shared.Api.GetAsync("/api/v1/upstreams/health");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        Assert.NotNull(JsonSerializer.Deserialize(await all.Content.ReadAsStringAsync(Ct), AdminJsonContext.Default.IReadOnlyListUpstreamHealthView));
    }

    /// <summary>A loopback server that records what is POSTed to it (and ignores the Node prober's <c>GET /</c> that this machine sends to new ports).</summary>
    private sealed class RecordingHook : IAsyncDisposable
    {
        private readonly IHost _host;
        private readonly ConcurrentQueue<(string Path, string Body, string? Signature)> _received = new();

        private RecordingHook(IHost host, string url)
        {
            _host = host;
            Url = url;
        }

        public string Url { get; }

        public IReadOnlyList<(string Path, string Body, string? Signature)> Received => [.. _received];

        public static async Task<RecordingHook> StartAsync()
        {
            RecordingHook? hook = null;
            var host = new HostBuilder()
                .ConfigureWebHost(web => web
                    .UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0))
                    .Configure(app => app.Run(async context =>
                    {
                        if (context.Request.Method != "POST")
                        {
                            return;
                        }

                        using StreamReader reader = new(context.Request.Body, Encoding.UTF8);
                        hook!._received.Enqueue((context.Request.Path.Value!, await reader.ReadToEndAsync(), context.Request.Headers["X-Paduraksa-Signature"].ToString() is { Length: > 0 } sig ? sig : null));
                    })))
                .Build();
            await host.StartAsync(Ct);
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
            hook = new RecordingHook(host, address);
            return hook;
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }
    }
}
