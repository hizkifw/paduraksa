using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Notifications;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// Notification channels and live upstream health. A channel's URL, token or password is stored in the vault the moment it arrives and is never
/// returned: there is no endpoint that reads a secret and no response type that could carry one. Channels share <c>providers:read</c> and
/// <c>providers:write</c> with ACME accounts and DNS providers, since they hold credentials the same way.
/// </summary>
internal static class NotificationEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var channels = api.MapGroup("/notification-channels").WithTags("Notifications");
        channels.MapGet("/", ListAsync).WithName("ListNotificationChannels").WithSummary("List notification channels")
            .WithDescription("With each channel's last delivery: lastSentAt, and lastError (the last failure after its retries, cleared by the next delivery).")
            .Produces<IReadOnlyList<NotificationChannelSummary>>().RequirePermission(Permission.ProvidersRead);
        channels.MapGet("/{id:NotificationChannelId}", GetAsync).WithName("GetNotificationChannel").WithSummary("Get a notification channel")
            .Produces<NotificationChannelSummary>().ProducesApiProblems(404).RequirePermission(Permission.ProvidersRead);
        channels.MapPost("/", CreateAsync).WithName("CreateNotificationChannel").WithSummary("Add a notification channel")
            .WithDescription("Kinds: ntfy, gotify, discord, webhook, email. The secret (a webhook URL, a token or an SMTP password) goes into the vault and is never returned. events lists what the channel is sent: upstream.down, upstream.up, destination.down, destination.up, certificate.expiring, acme.renewal_failed, acme.renewal_recovered, signin.lockout, signin.new_address.")
            .Accepts<NotificationChannelBody>("application/json").Produces<NotificationChannelSummary>(201).ProducesApiProblems(400, 415, 422)
            .RequirePermission(Permission.ProvidersWrite);
        channels.MapPut("/{id:NotificationChannelId}", UpdateAsync).WithName("ReplaceNotificationChannel").WithSummary("Replace a notification channel's settings")
            .WithDescription("The kind is fixed. A secret left out keeps the stored one; an empty one removes an optional secret (an ntfy token, an SMTP password without a login, a webhook's signing key).")
            .Accepts<NotificationChannelBody>("application/json").Produces<NotificationChannelSummary>().ProducesApiProblems(400, 404, 415, 422)
            .RequirePermission(Permission.ProvidersWrite);
        channels.MapDelete("/{id:NotificationChannelId}", DeleteAsync).WithName("DeleteNotificationChannel").WithSummary("Delete a notification channel and its secrets")
            .Produces(204).ProducesApiProblems(404).RequirePermission(Permission.ProvidersWrite);
        channels.MapPost("/{id:NotificationChannelId}/test", TestAsync).WithName("TestNotificationChannel").WithSummary("Send a test message through a channel")
            .WithDescription("Sends one message now, without retries. A message that could not be delivered is delivered=false with the reason, not an error. The test is audited (notification.channel.test) with its verdict only.")
            .Produces<NotificationTestOutcome>().ProducesApiProblems(404).RequirePermission(Permission.ProvidersWrite);

        var upstreams = api.MapGroup("/upstreams").WithTags("Upstreams");
        upstreams.MapGet("/health", ListHealth).WithName("ListUpstreamHealth").WithSummary("Read every upstream's live health")
            .WithDescription("As the running proxy sees it: each destination's verdict and why, the whole upstream's state (unchecked when it has no health check, unknown, healthy, degraded, down) and its latest change since the process started. Read once a second by a watcher, so it is at most a few seconds old.")
            .Produces<IReadOnlyList<UpstreamHealthView>>().RequirePermission(Permission.ConfigRead);
        upstreams.MapGet("/{id:UpstreamId}/health", GetHealth).WithName("GetUpstreamHealth").WithSummary("Read one upstream's live health")
            .Produces<UpstreamHealthView>().ProducesApiProblems(404).RequirePermission(Permission.ConfigRead);
    }

    private static async Task<IResult> ListAsync(NotificationService service, CancellationToken ct) =>
        EndpointSupport.Json(await service.ListAsync(ct).ConfigureAwait(false), AdminJsonContext.Default.IReadOnlyListNotificationChannelSummary);

    private static async Task<IResult> GetAsync(NotificationChannelId id, NotificationService service, CancellationToken ct) =>
        await service.GetAsync(id, ct).ConfigureAwait(false) is { } channel
            ? EndpointSupport.Json(channel, AdminJsonContext.Default.NotificationChannelSummary)
            : ApiProblems.Problem(StatusCodes.Status404NotFound, "There is no such notification channel.");

    private static async Task<IResult> CreateAsync(HttpContext http, AdminCaller caller, NotificationService service, CancellationToken ct)
    {
        var body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.NotificationChannelBody).ConfigureAwait(false);
        return body.Ok ? Answer(await service.CreateAsync(body.Value!, caller.Origin, ct).ConfigureAwait(false), MutationKind.Create) : body.Problem!;
    }

    private static async Task<IResult> UpdateAsync(HttpContext http, AdminCaller caller, NotificationChannelId id, NotificationService service, CancellationToken ct)
    {
        var body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.NotificationChannelBody).ConfigureAwait(false);
        return body.Ok ? Answer(await service.UpdateAsync(id, body.Value!, caller.Origin, ct).ConfigureAwait(false), MutationKind.Change) : body.Problem!;
    }

    private static async Task<IResult> DeleteAsync(AdminCaller caller, NotificationChannelId id, NotificationService service, CancellationToken ct)
    {
        var result = await service.DeleteAsync(id, caller.Origin, ct).ConfigureAwait(false);
        return result.Status == IssuanceChangeStatus.Committed ? Results.NoContent() : Answer(result, MutationKind.Change);
    }

    private static async Task<IResult> TestAsync(AdminCaller caller, NotificationChannelId id, NotificationService service, CancellationToken ct)
    {
        var result = await service.TestAsync(id, caller.Origin, ct).ConfigureAwait(false);
        return result switch
        {
            { Denied: true } => ApiProblems.Problem(StatusCodes.Status403Forbidden, "You may not send test notifications.", code: ApiCodes.Auth.PermissionRequired),
            { Found: false } => ApiProblems.Problem(StatusCodes.Status404NotFound, "There is no such notification channel."),
            _ => EndpointSupport.Json(new NotificationTestOutcome(result.Delivered, result.Error), AdminJsonContext.Default.NotificationTestOutcome),
        };
    }

    private static IResult ListHealth(UpstreamHealthBoard board) =>
        EndpointSupport.Json(board.All(), AdminJsonContext.Default.IReadOnlyListUpstreamHealthView);

    private static IResult GetHealth(UpstreamId id, UpstreamHealthBoard board) =>
        board.Find(id) is { } view
            ? EndpointSupport.Json(view, AdminJsonContext.Default.UpstreamHealthView)
            : ApiProblems.Problem(StatusCodes.Status404NotFound, "The running proxy has no such upstream (it is not in the live config, or no host routes to it).");

    private static IResult Answer(NotificationChannelResult result, MutationKind kind)
    {
        var status = ApiStatusMapping.Of(result.Status, kind);
        if (ApiStatusMapping.IsSuccess(status) && result.Channel is null)
        {
            return ApiProblems.Problem(StatusCodes.Status500InternalServerError, "The channel change reported success without the channel.");
        }

        if (!ApiStatusMapping.IsSuccess(status))
        {
            return ApiProblems.Problem(status, ChangeDetail(result.Status), null, result.Errors);
        }

        return EndpointSupport.Located(
            EndpointSupport.Json(result.Channel!, AdminJsonContext.Default.NotificationChannelSummary, status),
            status == StatusCodes.Status201Created ? EndpointSupport.Path("notification-channels", result.Channel!.Id) : null);
    }

    private static string ChangeDetail(IssuanceChangeStatus status) => status switch
    {
        IssuanceChangeStatus.NotFound => "There is no such notification channel.",
        IssuanceChangeStatus.Invalid => "The request was refused; see errors.",
        IssuanceChangeStatus.Denied => "You may not change notification channels.",
        IssuanceChangeStatus.Unavailable => "The config store is unavailable, so changes are refused now.",
        _ => "The request could not be completed.",
    };
}
