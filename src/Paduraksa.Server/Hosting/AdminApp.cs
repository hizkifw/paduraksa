using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Paduraksa.Admin;
using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Identity;
using Paduraksa.Admin.Mcp;
using Paduraksa.Admin.Web;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Settings;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Hosting;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Identity;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Placeholders;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The admin <c>WebApplication</c>: HTTPS on <c>Paduraksa:Listen:Admin</c> (default 9443) with a certificate the process
/// issues from the local CA and keeps current (<see cref="AdminCertificateService"/>), a plain-HTTP request on that port
/// answered with a redirect to <c>https</c> (<see cref="AdminPlainHttpRedirect"/>), and the internal listener the public
/// proxy forwards an admin host's requests through. It serves the health endpoints, the local CA root's downloads, the
/// security pipeline and identity API (<c>Paduraksa.Admin</c>), the resource REST API (<c>/api/v1</c> with its OpenAPI
/// document and reference UI), and the dashboard at <c>/</c> with the sign-in forms behind its pages. TODO: /mcp and /metrics.
/// </summary>
internal static partial class AdminApp
{
    private static readonly string[] GetAndHead = [HttpMethods.Get, HttpMethods.Head];

    /// <summary>The dashboard's paths, as the admin app's sign-in forms and pipeline hooks need them (the dashboard owns the names).</summary>
    internal static AdminWebOptions DashboardWeb { get; } = new()
    {
        HomePage = DashboardRoutes.Home,
        SignInPage = DashboardRoutes.SignIn,
        SecondFactorPage = DashboardRoutes.SignInVerify,
        SetupPage = DashboardRoutes.Setup,
        SetupCompletedPage = DashboardRoutes.Home,
        SignInAction = DashboardRoutes.Actions.SignIn,
        SecondFactorAction = DashboardRoutes.Actions.SecondFactor,
        SignOutAction = DashboardRoutes.Actions.SignOut,
        SetupAction = DashboardRoutes.Actions.Setup,
        CircuitHubPath = DashboardRoutes.CircuitHub,
        StaticAssetPathPrefixes = DashboardRoutes.StaticAssetPrefixes,
        ContentSecurityPolicy = DashboardSecurity.ContentSecurityPolicy,
    };

    public static WebApplication Build(HostContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = context.ApplicationName,
            EnvironmentName = context.EnvironmentName,
            ContentRootPath = context.ContentRootPath,
        });
        HostSetup.ApplyCommon(builder, context, HostRole.Admin);

        var listen = context.Options.Listen;
        // The validator already checked this; failing here would mean options that bypassed it.
        if (!IPAddress.TryParse(listen.AdminBind, out var bind))
        {
            throw new InvalidOperationException(
                $"{PaduraksaOptions.SectionName}:Listen:AdminBind must be an IP address, was '{listen.AdminBind}'.");
        }

        // The local CA's certificate, ensured by the core host's AdminCertificateService before this app starts and hot-swapped
        // per handshake on a renewal. On any failure that service keeps serving the previous certificate (the in-memory
        // placeholder until one was ever loaded), so this listener is always up.
        var adminCertificate = context.Shared.AdminCertificate;

        AdminSourcePolicy sources = new(listen.AdminAllowedSources);
        var internalListener = context.Shared.AdminInternal;
        var logger =
            new RoleLoggerFactory(context.Shared.LoggerFactory, HostRole.Admin)
                .CreateLogger(typeof(AdminApp).FullName!);
        ListenOptions? internalListenOptions = null;
        builder.WebHost.UseKestrelCore().UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(bind, listen.Admin, admin =>
            {
                admin.Protocols = HttpProtocols.Http1AndHttp2;
                RestrictSources(admin, sources);
                // A browser handed http://host:9443/ gets a redirect to https://host:9443/ rather than an empty response; a
                // TLS handshake is left untouched for the middleware below (AdminPlainHttpRedirect).
                admin.UsePlainHttpRedirect(logger);
                admin.UseHttps(adminCertificate.CreateTlsOptions());
            });

            // The internal listener the public proxy reaches. Plain HTTP by design: it is a Unix socket in the
            // process's 0700 run directory (0600 for the socket), or a loopback port when one can't be used. No TLS: nothing
            // leaves the process boundary, and no certificate is needed.
            if (internalListener.Enabled)
            {
                if (internalListener.UsesUnixSocket)
                {
                    kestrel.ListenUnixSocket(internalListener.SocketPath!, internalOptions =>
                    {
                        internalOptions.Protocols = HttpProtocols.Http1;
                        MarkInternal(internalOptions);
                    });
                }
                else
                {
                    kestrel.Listen(IPAddress.Loopback, 0, internalOptions =>
                    {
                        internalListenOptions = internalOptions;
                        internalOptions.Protocols = HttpProtocols.Http1;
                        MarkInternal(internalOptions);
                    });
                }
            }

            // Opt-in, loopback only. ListenLocalhost binds 127.0.0.1 and ::1 and nothing else; no setting moves it.
            if (listen.AdminLoopbackHttp > 0)
            {
                kestrel.ListenLocalhost(listen.AdminLoopbackHttp, fallback => fallback.Protocols = HttpProtocols.Http1);
            }
        });
        builder.Services.AddRoutingCore();
        builder.Services.AddSingleton(sources);
        builder.Services.AddPaduraksaAdminAuth(
            context.Options.Admin.ToSecurityOptions(listen.AdminLoopbackHttp, [.. listen.AdminAllowedSources]));
        builder.Services.AddPaduraksaAdminApi(context.Options.Mcp.Enabled
            ? new AdminMcpOptions(Paduraksa.Mcp.SandboxChildCommand.ForThisServer(), context.Options.Mcp.IdleTimeout)
            : null);
        builder.Services.AddPaduraksaAdminWeb(DashboardWeb);
        builder.Services.AddPaduraksaDashboard(new DashboardOptions
        {
            MinPasswordLength = context.Options.Admin.MinPasswordLength,
            LiveMinPasswordLength = () => (int)ServerSettingCatalog.AdminMinPasswordLength.Resolve(context.Shared.ServerSettings.Current),
            McpEnabled = context.Options.Mcp.Enabled,
            SessionRecheckInterval = context.Options.Admin.SessionRecheckInterval,
        });
        // The dashboard's password checks (change password, TOTP, recovery codes) share the API's derivation slots.
        builder.Services.Replace(ServiceDescriptor.Singleton<IPasswordCheckSlots>(static sp =>
            new AdminPasswordCheckSlots(sp.GetRequiredService<AdminThrottle>())));
        DashboardAssets.Use(builder);

        HostSetup.ApplyCallerServices(builder, context, HostRole.Admin);
        HostSetup.PinListenerConfiguration(builder);

        var app = builder.Build();
        FollowServerSettings(app, context.Shared.ServerSettings);
        app.Lifetime.ApplicationStarted.Register(() =>
            CompleteInternalListener(internalListener, internalListenOptions, logger));
        // First, before anything writes a response: the dashboard and the API are served over HTTP/2 too.
        app.Use(static (context, next) =>
        {
            Paduraksa.Proxy.Http.KestrelHttp2StreamReuse.BeforeRequest(context);
            return next(context);
        });
        app.UsePaduraksaAdminApi();
        app.UsePaduraksaAdminAuth();
        app.UsePaduraksaAdminWeb();
        MapHealth(app);
        app.MapPaduraksaIdentityApi();
        // The resource API, the local CA root's download endpoints among them (anonymous, open during setup).
        app.MapPaduraksaAdminApi();
        app.MapPaduraksaAdminWeb();
        DashboardAssets.Map(app);
        app.MapPaduraksaDashboard();
        return app;
    }

    /// <summary>The admin app's own consumers of the server settings: the sign-in rate limits, taken from the next attempt.</summary>
    private static void FollowServerSettings(WebApplication app, ServerSettingsState state)
    {
        var throttle = app.Services.GetRequiredService<AdminThrottle>();
        void Apply(ServerSettings values) => throttle.Reconfigure(
            (int)ServerSettingCatalog.AdminRateLimitPermitsPerIp.Resolve(values),
            (int)ServerSettingCatalog.AdminRateLimitPermitsPerUser.Resolve(values),
            ServerSettingCatalog.AdminRateLimitWindow.Resolve(values));
        Apply(state.Current);
        state.Changed += Apply;
        app.Lifetime.ApplicationStopped.Register(() => state.Changed -= Apply);
    }

    /// <summary>
    /// Finishes the internal listener once Kestrel has bound: the Unix socket's mode is set to 0600 (the directory is already
    /// 0700), or the fallback's OS-assigned port is recorded so the proxy's connect callback can reach it.
    /// </summary>
    private static void CompleteInternalListener(AdminInternalListener listener, ListenOptions? internalOptions,
        ILogger logger)
    {
        if (!listener.Enabled)
        {
            return;
        }

        try
        {
            if (listener.UsesUnixSocket)
            {
                var path = listener.SocketPath!;
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                listener.RecordBoundSocket();
                if (listener.OwnershipCheckSkipped)
                {
                    LogOwnershipSkipped(logger, path);
                }

                // The internal listener is not a client endpoint: GetListenAddresses leaves it out.
                LogInternalSocket(logger, path);
                return;
            }

            var port = internalOptions?.IPEndPoint?.Port;
            if (port is > 0)
            {
                listener.BindLoopback(port.Value);
                LogInternalLoopback(logger, listener.FallbackReason?.Message ?? "unknown reason",
                    $"127.0.0.1:{port.Value}");
                return;
            }

            LogInternalPortUnknown(logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or InvalidOperationException)
        {
            LogInternalFinishFailed(logger, ex);
        }
    }

    /// <summary>Marks connections accepted on the internal listener only; nothing infers it from a peer address or a port.</summary>
    private static void MarkInternal(ListenOptions options) =>
        options.Use(next => connection =>
        {
            connection.Items[AdminInternalConnectionMarker.Key] = true;
            return next(connection);
        });

    /// <summary>
    /// Refuses connections from outside <c>AdminAllowedSources</c> before TLS starts: the TCP handshake completes, then the connection is
    /// aborted without a byte sent. Loopback is not implied. No-op without an allowlist. Only the main admin listener has it: the
    /// loopback plain-HTTP fallback listener is loopback-only by construction, and the internal admin listener applies the list per
    /// request to the forwarded client.
    /// </summary>
    private static void RestrictSources(ListenOptions listener, AdminSourcePolicy sources)
    {
        if (!sources.IsRestricted)
        {
            return;
        }

        listener.Use(next => connection =>
        {
            if (sources.IsAllowed((connection.RemoteEndPoint as IPEndPoint)?.Address))
            {
                return next(connection);
            }

            connection.Abort();
            return Task.CompletedTask;
        });
    }

    /// <summary>Unauthenticated, status only, never any detail.</summary>
    /// <remarks>
    /// A degraded start (the proxy serves the last-known-good config because the config database can't be used, docs/design/config.md "Startup, last-known-good and degraded starts") is still <b>ready</b>: the proxy serves traffic, and taking it out of rotation would turn a config-store
    /// problem into an outage. <c>/readyz</c> then answers 200 with the body <c>degraded</c> instead of <c>ready</c>, a
    /// status a keyword monitor can alert on, and the cause is logged at Critical.
    /// </remarks>
    private static void MapHealth(WebApplication app)
    {
        var snapshots = app.Services.GetRequiredService<SnapshotStore>();
        var health = app.Services.GetRequiredService<HealthState>();
        var config = app.Services.GetRequiredService<ConfigService>();

        // Live: the process is up and a snapshot is loaded.
        app.MapMethods("/healthz", GetAndHead, context =>
            WriteStatusAsync(context, snapshots.HasPublished, "ok\n", "unavailable\n"));

        // Ready: listeners bound, a snapshot live and not draining; degraded or not (see the remarks). The vault is implied:
        // the core host starts it first and a vault that can't start stops the process before any listener binds.
        app.MapMethods("/readyz", GetAndHead, context =>
            WriteStatusAsync(context, health.IsReady && snapshots.HasPublished,
                config.Status.IsDegraded ? "degraded\n" : "ready\n", "not ready\n"));
    }

    private static Task WriteStatusAsync(HttpContext context, bool healthy, string okBody, string failBody)
    {
        var response = context.Response;
        response.StatusCode = healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
        response.Headers.CacheControl = "no-store";
        response.ContentType = "text/plain; charset=utf-8";
        return HttpMethods.IsHead(context.Request.Method)
            ? Task.CompletedTask
            : response.WriteAsync(healthy ? okBody : failBody, context.RequestAborted);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The admin internal listener is the Unix socket {Path}")]
    private static partial void LogInternalSocket(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The admin internal TCP fallback listener is {Address}; the Unix socket couldn't be used: {Reason}")]
    private static partial void LogInternalLoopback(ILogger logger, string reason, string address);

    [LoggerMessage(Level = LogLevel.Warning,
        Message =
            "Couldn't check the owner of {Path}; this platform exposes no statx owner. Continuing with the symlink check only")]
    private static partial void LogOwnershipSkipped(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error,
        Message =
            "The admin internal listener bound, but its loopback port could not be read; proxied admin requests answer 502")]
    private static partial void LogInternalPortUnknown(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Could not finish the admin internal listener; proxied admin requests may answer 502")]
    private static partial void LogInternalFinishFailed(ILogger logger, Exception exception);
}
