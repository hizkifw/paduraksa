using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Notifications;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// Notification channels, their delivery history and live upstream health. A channel's URL, token or password is stored in the vault the moment it arrives and is never
/// returned: there is no endpoint that reads a secret and no response type that could carry one. Channels share <c>providers:read</c> and
/// <c>providers:write</c> with ACME accounts and DNS providers, since they hold credentials the same way.
/// </summary>
internal static class NotificationEndpoints
{
    private static readonly QueryParameter Channel = QueryParameter.Id<NotificationChannelId>("channel", "Only events with a delivery to this channel (ntf_...).");
    private static readonly QueryParameter Kind = QueryParameter.Choice("kind", "Only this kind of event.", Enum.GetValues<NotificationEventKind>().Select(k => k.ToName()));
    private static readonly QueryParameter Outcome = QueryParameter.Choice("outcome", "Only events with a delivery in this state (to the channel, when one is given).", Enum.GetValues<NotificationDeliveryState>().Select(s => s.ToName()));

    public static void Map(RouteGroupBuilder api)
    {
        var channels = api.MapGroup("/notification-channels").WithTags("Notifications");
        channels.MapGet("/", ListAsync).WithName("ListNotificationChannels").WithSummary("List notification channels")
            .WithDescription("With each channel's last delivery: lastSentAt, and lastError (the last failure after its retries, cleared by the next delivery).")
            .WithPage().Produces<Page<NotificationChannelSummary>>().ProducesApiProblems(400).RequirePermission(Permission.ProvidersRead);
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

        var notifications = api.MapGroup("/notifications").WithTags("Notifications");
        notifications.MapGet("/history", HistoryAsync).WithName("QueryNotificationHistory").WithSummary("Query the notification history")
            .WithDescription("Every event that was published, newest first, each with what happened on every channel it was addressed to (state, attempts, the next try of a pending one, the last error, when it was sent). An event no channel subscribes to is listed with no deliveries. The filters are all optional and all must match; from (inclusive) and to (exclusive) are the times of publication. History is kept for Paduraksa:Notifications:HistoryRetention.")
            .WithQuery(Channel, Kind, Outcome, AuditEndpoints.From, AuditEndpoints.To)
            .WithPage().Produces<Page<NotificationHistoryEvent>>().ProducesApiProblems(400).RequirePermission(Permission.ProvidersRead);
        notifications.MapPost("/deliveries/{id:NotificationDeliveryId}/retry", RetryDeliveryAsync).WithName("RetryNotificationDelivery").WithSummary("Send a failed or expired delivery again")
            .WithDescription("Puts the delivery back to pending, due now, with a fresh retry schedule; the channel sends it within seconds. Only a failed or expired delivery can be sent again, and only while its channel exists and is not paused (422 otherwise). Audited (notification.delivery.retry) with the delivery, its event and its channel. Delivery is at-least-once, so a receiver that already got the message once may get it again with the same event ID.")
            .Produces<NotificationRetryOutcome>().ProducesApiProblems(404, 422).RequirePermission(Permission.ProvidersWrite);
        notifications.MapPost("/events/{id:NotificationEventId}/retry", RetryEventAsync).WithName("RetryNotificationEvent").WithSummary("Send every failed or expired delivery of an event again")
            .WithDescription("As the delivery's retry, for every failed or expired delivery of the event whose channel exists and is not paused. 422 when there is none. Audited (notification.event.retry) with how many.")
            .Produces<NotificationRetryOutcome>().ProducesApiProblems(404, 422).RequirePermission(Permission.ProvidersWrite);

        var upstreams = api.MapGroup("/upstreams").WithTags("Upstreams");
        upstreams.MapGet("/health", ListHealth).WithName("ListUpstreamHealth").WithSummary("Read every upstream's live health")
            .WithDescription("As the running proxy sees it: each destination's verdict and why, the whole upstream's state (unchecked when it has no health check, unknown, healthy, degraded, down) and its latest change since the process started. Read once a second by a watcher, so it is at most a few seconds old.")
            .Produces<IReadOnlyList<UpstreamHealthView>>().RequirePermission(Permission.ConfigRead);
        upstreams.MapGet("/{id:UpstreamId}/health", GetHealth).WithName("GetUpstreamHealth").WithSummary("Read one upstream's live health")
            .Produces<UpstreamHealthView>().ProducesApiProblems(404).RequirePermission(Permission.ConfigRead);
    }

    private static async Task<IResult> ListAsync(HttpContext http, NotificationService service, CancellationToken ct) =>
        Pagination.Respond(http, await service.ListAsync(ct).ConfigureAwait(false), c => c.Id, AdminJsonContext.Default.PageNotificationChannelSummary);

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

    private static async Task<IResult> HistoryAsync(HttpContext http, NotificationHistoryService history, CancellationToken ct)
    {
        if (!Pagination.TryGetPage(http, out var limit, out var cursor, out var problem))
        {
            return problem!;
        }

        NotificationEventId? after = null;
        if (cursor is not null)
        {
            if (!Pagination.TryDecode(cursor, out NotificationEventId id))
            {
                return ApiProblems.Problem(StatusCodes.Status400BadRequest, "The cursor is not one this API issued for the notification history.");
            }

            after = id;
        }

        if (!AuditEndpoints.TryGetTime(http, AuditEndpoints.From, out var from) || !AuditEndpoints.TryGetTime(http, AuditEndpoints.To, out var to))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "from and to must be ISO 8601 times with a zone, e.g. 2026-11-08T12:00:00Z (write the + of an offset as %2B).");
        }

        if (from > to)
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "from must not be after to.");
        }

        NotificationChannelId? channel = null;
        if (Channel.Read(http).ToString() is { Length: > 0 } channelText)
        {
            if (!TypedIdRouting.TryParse(channelText, out NotificationChannelId parsed))
            {
                return ApiProblems.Problem(StatusCodes.Status400BadRequest, "channel must be a notification channel ID (ntf_...).");
            }

            channel = parsed;
        }

        NotificationEventKind? kind = null;
        if (Kind.Read(http).ToString() is { Length: > 0 } kindText)
        {
            if (!TryParse(kindText, ModelNames.ParseNotificationEventKind, out var parsedKind))
            {
                return ApiProblems.Problem(StatusCodes.Status400BadRequest, "kind must be an event name such as upstream.down, certificate.expiring or signin.lockout.");
            }

            kind = parsedKind;
        }

        NotificationDeliveryState? outcome = null;
        if (Outcome.Read(http).ToString() is { Length: > 0 } outcomeText)
        {
            if (!TryParse(outcomeText, ModelNames.ParseNotificationDeliveryState, out var parsedOutcome))
            {
                return ApiProblems.Problem(StatusCodes.Status400BadRequest, "outcome must be pending, sent, failed, expired or dropped.");
            }

            outcome = parsedOutcome;
        }

        var page = await history.QueryAsync(new NotificationHistoryFilter(channel, kind, outcome, from, to), limit, after, ct).ConfigureAwait(false);
        return EndpointSupport.Json(
            new Page<NotificationHistoryEvent>(page.Items, page.Next is { } next ? Pagination.Encode(next) : null),
            AdminJsonContext.Default.PageNotificationHistoryEvent);
    }

    private static async Task<IResult> RetryDeliveryAsync(AdminCaller caller, NotificationDeliveryId id, NotificationHistoryService history, CancellationToken ct) =>
        Retried(await history.RetryDeliveryAsync(id, caller.Origin, ct).ConfigureAwait(false), "There is no such notification delivery.");

    private static async Task<IResult> RetryEventAsync(AdminCaller caller, NotificationEventId id, NotificationHistoryService history, CancellationToken ct) =>
        Retried(await history.RetryEventAsync(id, caller.Origin, ct).ConfigureAwait(false), "There is no such notification event.");

    private static IResult Retried(NotificationRetryResult result, string notFound)
    {
        var status = ApiStatusMapping.Of(result.Status, MutationKind.Change);
        return ApiStatusMapping.IsSuccess(status)
            ? EndpointSupport.Json(new NotificationRetryOutcome(result.Retried), AdminJsonContext.Default.NotificationRetryOutcome)
            : ApiProblems.Problem(status, result.Status switch
            {
                IssuanceChangeStatus.NotFound => notFound,
                IssuanceChangeStatus.Invalid => "The request was refused; see errors.",
                IssuanceChangeStatus.Denied => "You may not send notifications again.",
                IssuanceChangeStatus.Unavailable => "The config store is unavailable, so changes are refused now.",
                _ => "The request could not be completed.",
            }, null, result.Errors);
    }

    private static bool TryParse<T>(string text, Func<string, T> parse, out T value)
    {
        try
        {
            value = parse(text);
            return true;
        }
        catch (FormatException)
        {
            value = default!;
            return false;
        }
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
