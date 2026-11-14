using System.Net;

using Paduraksa.Admin.Json;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Tests.Integration.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// <c>/api/v1/server-settings</c> over the admin port (docs/design/config.md "Server settings"): the value in force and where it comes from, the
/// bounds, who may change them, the audit of a change, a draft that carries one, and the consumers taking it with no restart.
/// </summary>
public sealed class ServerSettingsApiTests : IAsyncLifetime
{
    private AdminApiHarness _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The environment sets the analytics' retention: it wins over the config, and the page shows it read-only.
    public async ValueTask InitializeAsync() =>
        _api = await AdminApiHarness.StartAsync(Ct, settings: new Dictionary<string, string?> { ["Paduraksa:Analytics:Retention"] = "7.00:00:00" });

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private async Task<(ServerSettingsView View, string ETag)> ReadAsync(string caller = "owner")
    {
        using var response = await _api.GetAsync("/api/v1/server-settings", caller);
        return (await AdminApiHarness.ReadAsync(response, AdminJsonContext.Default.ServerSettingsView, Ct), AdminApiHarness.ETag(response)!);
    }

    private Task<HttpResponseMessage> PutAsync(ServerSettings settings, string etag, string caller = "owner", string query = "") =>
        _api.SendJsonAsync(HttpMethod.Put, "/api/v1/server-settings" + query, settings, AdminJsonContext.Default.ServerSettings, caller, etag);

    private static ServerSettingView Of(ServerSettingsView view, ServerSettingDefinition setting) => view.Settings.Single(s => s.Key == setting.Key);

    [Fact]
    public async Task TheEnvironmentWinsTheConfigComesNextAndTheDefaultLast()
    {
        var (before, etag) = await ReadAsync();
        Assert.Equal(ServerSettingSource.Default, Of(before, ServerSettingCatalog.MetricsRetention).Source);
        Assert.Equal("90.00:00:00", Of(before, ServerSettingCatalog.MetricsRetention).Value);
        var analytics = Of(before, ServerSettingCatalog.AnalyticsRetention);
        Assert.Equal((ServerSettingSource.Environment, "7.00:00:00", "Paduraksa__Analytics__Retention"), (analytics.Source, analytics.Value, analytics.EnvironmentVariable));

        using var put = await PutAsync(new ServerSettings { MetricsRetention = TimeSpan.FromDays(30), AnalyticsRetention = TimeSpan.FromDays(60) }, etag);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var (after, _) = await ReadAsync();
        var metrics = Of(after, ServerSettingCatalog.MetricsRetention);
        Assert.Equal((ServerSettingSource.Config, "30.00:00:00", "30.00:00:00"), (metrics.Source, metrics.Value, metrics.Stored));
        analytics = Of(after, ServerSettingCatalog.AnalyticsRetention);
        Assert.Equal((ServerSettingSource.Environment, "7.00:00:00", "60.00:00:00"), (analytics.Source, analytics.Value, analytics.Stored));
        Assert.Equal(TimeSpan.FromDays(7), _api.Host.Shared.ServerSettingsBindings.Analytics.Current.Retention);
    }

    [Fact]
    public async Task AChangedRetentionReachesThePruneWithNoRestart()
    {
        var (_, etag) = await ReadAsync();

        using var put = await PutAsync(new ServerSettings { MetricsRetention = TimeSpan.FromHours(6), NotificationHistoryRetention = TimeSpan.FromDays(2), LogRetainedFiles = 3 }, etag);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var bindings = _api.Host.Shared.ServerSettingsBindings;
        Assert.Equal(TimeSpan.FromHours(6), bindings.MetricRollups.Current.Retention);
        Assert.Equal(TimeSpan.FromDays(2), bindings.Notifications.Current.HistoryRetention);
        Assert.Equal(3, _api.Host.Shared.LogFiles.Access.Settings.RetainedFiles);
        Assert.Equal(3, _api.Host.Shared.LogFiles.Error.Settings.RetainedFiles);
    }

    [Theory]
    [InlineData("""{"metricsRetention":"00:10:00"}""", "metricsRetention")]
    [InlineData("""{"logRetainedFiles":0}""", "logRetainedFiles")]
    [InlineData("""{"logMaxFileSize":17179869185}""", "logMaxFileSize")]
    [InlineData("""{"logMaxAge":"3651.00:00:00"}""", "logMaxAge")]
    public async Task AValueOutsideItsBoundsIsRefusedWithItsPath(string body, string key)
    {
        var (_, etag) = await ReadAsync();

        using var response = await _api.SendAsync(HttpMethod.Put, "/api/v1/server-settings", ifMatch: etag, content: AdminApiHarness.JsonBody(body));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await AdminApiHarness.ProblemAsync(response, Ct);
        var error = Assert.Single(problem.Errors!);
        Assert.Equal(("/server/" + key, "server.setting.invalid"), (error.Path, error.Code));
    }

    [Fact]
    public async Task AChangeNeedsIfMatch()
    {
        using var response = await _api.SendJsonAsync(HttpMethod.Put, "/api/v1/server-settings", new ServerSettings(), AdminJsonContext.Default.ServerSettings);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("viewer")]
    public async Task OnlyTheOwnerAndTheAdminMayChangeThemAndEveryRoleMayReadThem(string role)
    {
        var (_, etag) = await ReadAsync(role);

        using var refused = await PutAsync(new ServerSettings { MetricsRetention = TimeSpan.FromDays(1) }, etag, role);
        using var admin = await PutAsync(new ServerSettings { MetricsRetention = TimeSpan.FromDays(1) }, etag, "admin");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public async Task AnOperatorCanNotChangeThemThroughTheGlobalSettingsEither()
    {
        using var current = await _api.GetAsync("/api/v1/settings");
        var settings = await AdminApiHarness.ReadAsync(current, AdminJsonContext.Default.GlobalSettings, Ct);

        using var refused = await _api.SendJsonAsync(
            HttpMethod.Put, "/api/v1/settings", settings with { Server = new ServerSettings { LogMaxAge = TimeSpan.FromDays(1) } },
            AdminJsonContext.Default.GlobalSettings, "operator", AdminApiHarness.ETag(current));
        using var allowed = await _api.SendJsonAsync(
            HttpMethod.Put, "/api/v1/settings", settings with { UnknownHost = Core.Model.UnknownHostBehaviour.Close },
            AdminJsonContext.Default.GlobalSettings, "operator", AdminApiHarness.ETag(current));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task AChangeIsAuditedAndADryRunChangesNothing()
    {
        var (_, etag) = await ReadAsync();

        using var dry = await PutAsync(new ServerSettings { LogMaxAge = TimeSpan.FromDays(3) }, etag, query: "?dryRun=true");
        var planned = await AdminApiHarness.ReadAsync(dry, AdminJsonContext.Default.ConfigCommandResult, Ct);
        Assert.Equal(ConfigCommandStatus.DryRun, planned.Status);
        Assert.Contains(planned.Changes, c => c is SettingsChange s && s.Fields.Any(f => f.Contains("server", StringComparison.Ordinal)));
        Assert.Equal(ServerSettingSource.Default, Of((await ReadAsync()).View, ServerSettingCatalog.LogMaxAge).Source);

        using var put = await PutAsync(new ServerSettings { LogMaxAge = TimeSpan.FromDays(3) }, etag, query: "?message=Shorter+logs");
        var committed = await AdminApiHarness.ReadAsync(put, AdminJsonContext.Default.ConfigCommandResult, Ct);

        using var audit = await _api.GetAsync("/api/v1/audit?action=config.settings.update");
        var events = (await AdminApiHarness.ReadAsync(audit, AdminJsonContext.Default.PageAuditEventSummary, Ct)).Items;
        Assert.Contains(events, e => e.RevisionId == committed.Revision!.Id);
        Assert.Equal(TimeSpan.FromDays(3), _api.Host.Shared.LogFiles.Access.Settings.MaxAge);
    }

    [Fact]
    public async Task ADraftCarriesAChangeAndItsApplyMakesItLive()
    {
        using var current = await _api.GetAsync("/api/v1/settings");
        var settings = await AdminApiHarness.ReadAsync(current, AdminJsonContext.Default.GlobalSettings, Ct);
        using var created = await _api.SendAsync(HttpMethod.Post, "/api/v1/drafts");
        var draft = await AdminApiHarness.ReadAsync(created, AdminJsonContext.Default.ConfigDraft, Ct);

        using var added = await _api.SendJsonAsync(
            HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/entries",
            new AddDraftEntriesRequest([new UpdateSettingsCommand(settings with { Server = new ServerSettings { NotificationHistoryRetention = TimeSpan.FromDays(3) } })]),
            AdminJsonContext.Default.AddDraftEntriesRequest);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal(ServerSettingCatalog.NotificationHistoryRetention.Default, _api.Host.Shared.ServerSettingsBindings.Notifications.Current.HistoryRetention);

        using var review = await _api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/review");
        var reviewed = await AdminApiHarness.ReadAsync(review, AdminJsonContext.Default.DraftOutcome, Ct);
        using var applied = await _api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/apply", ifMatch: $"\"{reviewed.ReviewToken}\"");

        Assert.Equal(ConfigCommandStatus.Committed, (await AdminApiHarness.ReadAsync(applied, AdminJsonContext.Default.DraftOutcome, Ct)).Result.Status);
        Assert.Equal(TimeSpan.FromDays(3), _api.Host.Shared.ServerSettingsBindings.Notifications.Current.HistoryRetention);
    }

    [Fact]
    public async Task TheSignInPolicyReachesTheIdentityServicesAndTheRateLimitWithNoRestart()
    {
        var (_, etag) = await ReadAsync();

        using var put = await PutAsync(
            new ServerSettings { AdminSessionLifetime = TimeSpan.FromHours(2), AdminSessionIdleTimeout = TimeSpan.FromMinutes(30), AdminMinPasswordLength = 16, AdminRateLimitPermitsPerIp = 1 },
            etag);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var identity = _api.Host.Shared.ServerSettingsBindings.Identity.Current;
        Assert.Equal((TimeSpan.FromHours(2), TimeSpan.FromMinutes(30), 16), (identity.SessionLifetime, identity.SessionIdleTimeout, identity.MinPasswordLength));
        const string Body = """{"username":"nobody","password":"not the password at all"}""";
        using var first = await _api.SendAsync(HttpMethod.Post, "/api/v1/auth/login", caller: null, content: AdminApiHarness.JsonBody(Body));
        using var second = await _api.SendAsync(HttpMethod.Post, "/api/v1/auth/login", caller: null, content: AdminApiHarness.JsonBody(Body));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task AnIdleTimeoutLongerThanTheLifetimeIsRefused()
    {
        var (_, etag) = await ReadAsync();

        using var response = await PutAsync(new ServerSettings { AdminSessionLifetime = TimeSpan.FromHours(1), AdminSessionIdleTimeout = TimeSpan.FromHours(3) }, etag);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = Assert.Single((await AdminApiHarness.ProblemAsync(response, Ct)).Errors!);
        Assert.Equal(("/server/adminSessionIdleTimeout", "server.settings.conflict"), (error.Path, error.Code));
    }
}
