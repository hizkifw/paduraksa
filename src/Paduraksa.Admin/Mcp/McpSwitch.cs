using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Paduraksa.Admin.Mcp;

/// <summary>
/// Whether <c>/mcp</c> answers, and how long an idle sandbox child lives, as the server settings say while the process runs
/// (docs/design/admin-surfaces.md "MCP in code mode (M10)", "Switched live"). The endpoint is always mapped; while this is off a request to it is
/// answered 404 before authentication, as if it were not, and the sandbox pool has been stopped.
/// </summary>
/// <param name="enabled">Read on every request.</param>
/// <param name="idleTimeout">Read when the sandbox pool is built.</param>
public sealed class McpSwitch(Func<bool> enabled, Func<TimeSpan> idleTimeout) : IDisposable
{
    public bool Enabled => enabled();

    public TimeSpan IdleTimeout => idleTimeout();

    private readonly SemaphoreSlim _disposing = new(1, 1);
    private Func<Func<Task>?>? _detach;

    /// <summary>
    /// The settings behind the switch changed: the sandbox pool is stopped and drained (switched off, or a new idle timeout that a new pool takes).
    /// The next allowed call starts a new one.
    /// </summary>
    /// <remarks>
    /// The pool is detached before this returns, on the caller's thread, so changes detach in the order they came: a quick off and on can't have
    /// the off's stop take the pool the on's first call built. Killing the detached children runs in the background, one disposal at a time.
    /// </remarks>
    public Task ChangedAsync()
    {
        if (_detach?.Invoke() is not { } dispose)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            try
            {
                await _disposing.WaitAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The app is stopping: the toolkit's own disposal kills every child.
                return;
            }

            try
            {
                await dispose().ConfigureAwait(false);
            }
            finally
            {
                _disposing.Release();
            }
        });
    }

    internal void Attach(Func<Func<Task>?> detach) => _detach = detach;

    public void Dispose() => _disposing.Dispose();

    /// <summary>A switch that is always on with the given idle timeout: a composition without server settings.</summary>
    public static McpSwitch On(TimeSpan idleTimeout) => new(static () => true, () => idleTimeout);

    /// <summary>The 404 for <c>/mcp</c> while the switch is off; first in the pipeline, so nothing behind it (authentication, the MCP host) sees the request.</summary>
    internal static void Use(IApplicationBuilder app)
    {
        var mcp = app.ApplicationServices.GetService<McpSwitch>();
        if (mcp is null)
        {
            return;
        }

        app.Use(async (context, next) =>
        {
            if (!mcp.Enabled && context.Request.Path.StartsWithSegments(McpExtensions.Path, StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }
}
