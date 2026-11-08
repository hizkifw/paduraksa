using System.Data.Common;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Paduraksa.Admin.Api;
using Paduraksa.Application.Identity;

namespace Paduraksa.Admin.Auth;

/// <summary>
/// The first-run gate (PLAN §12.1): while <see cref="SetupService"/> says no Owner exists, the admin app serves only the health
/// endpoints, the setup endpoints (<c>/api/v1/setup</c>) and whatever <see cref="AdminPipelineHooks.SetupAllowedPathPrefixes"/> lists
/// (the OpenAPI document and the docs); everything else is a 503 problem naming the setup step. The state is read on every request
/// (the service stops asking the database once setup is complete), so finishing setup opens the app at once. When the state cannot be
/// read (the config database is down) the gate stays shut, with a different message.
/// </summary>
internal sealed partial class AdminSetupGateMiddleware(RequestDelegate next, SetupService setup, AdminPipelineHooks hooks, ILoggerFactory loggerFactory)
{
    internal const string SetupPath = "/api/v1/setup";

    private readonly ILogger _logger = loggerFactory.CreateLogger("Paduraksa.Admin.Setup");

    public async Task InvokeAsync(HttpContext context)
    {
        // First-run setup is never reachable from the Internet through a proxied admin host. The internal listener's secret
        // already proves the hop came from this process; this refuses the setup surface even so (the owner reaches the admin
        // port or the loopback plain-HTTP fallback listener for it).
        if (context.IsOnInternalListener()
            && context.Request.Path.StartsWithSegments(SetupPath, StringComparison.OrdinalIgnoreCase))
        {
            await AdminProblems.Forbidden(
                "Setup is not available through the proxy",
                "First-run setup is only served on the admin port or the loopback plain-HTTP fallback listener.",
                "setup_via_proxy").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (IsAlwaysOpen(context.Request.Path))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        bool complete;
        try
        {
            complete = await setup.IsCompleteAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            LogUnavailable(_logger, ex);
            await ApiProblems.Problem(
                StatusCodes.Status503ServiceUnavailable,
                "The admin app cannot tell whether first-run setup is complete (the config store is unavailable).",
                code: "setup_unavailable",
                title: "Setup state unavailable").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (complete)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        await ApiProblems.Problem(
            StatusCodes.Status503ServiceUnavailable,
            "First-run setup is not complete. POST the setup token (from the server log, or `paduraksa setup-token`) with the first Owner's username and password to " + SetupPath + ".",
            instance: SetupPath,
            code: "setup_required",
            title: "Setup required").ExecuteAsync(context).ConfigureAwait(false);
    }

    private bool IsAlwaysOpen(PathString path) =>
        path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/readyz", StringComparison.OrdinalIgnoreCase)
        || path.Equals(SetupPath, StringComparison.OrdinalIgnoreCase)
        || hooks.SetupAllowedPathPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't read whether first-run setup is complete; the admin app stays closed.")]
    private static partial void LogUnavailable(ILogger logger, Exception exception);
}
