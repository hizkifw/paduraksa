using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Admin;
using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Certificates;
using Paduraksa.Admin.Identity;
using Paduraksa.Admin.Web;
using Paduraksa.Application.Commands;
using Paduraksa.Core.Hosting;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Identity;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Placeholders;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The admin <c>WebApplication</c>: HTTPS on <c>Paduraksa:Listen:Admin</c> (default 9443), served with a certificate the
/// process issues from the local CA and keeps current (<see cref="AdminCertificateService"/>; PLAN §3, §10.1).
/// Health endpoints (PLAN §11.3), the local CA root's download endpoints, the security pipeline and identity API
/// (<c>Paduraksa.Admin</c>: setup gate, authentication, CSRF defence, hardening), the optional source allowlist and the opt-in
/// loopback plain-HTTP listener.
/// The resource REST API (<c>/api/v1</c>, its OpenAPI document and the reference UI, PLAN §11.1).
/// The dashboard (PLAN §12) at <c>/</c>: <c>Paduraksa.Dashboard</c>'s Blazor components, the sign-in forms behind its pages
/// (<c>Paduraksa.Admin.Web</c>) and its static files, all from the app itself.
/// The internal listener (PLAN §3.1): a Unix socket (0600, in a 0700 directory) or a loopback fallback, plain HTTP, through
/// which the public proxy forwards an admin host's requests.
/// TODO(M10): /mcp. TODO(M9): /metrics.
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
        SetupCompletedPage = Paduraksa.Dashboard.Setup.SetupWizard.Route,
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

        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = context.ApplicationName,
            EnvironmentName = context.EnvironmentName,
            ContentRootPath = context.ContentRootPath,
        });
        HostSetup.ApplyCommon(builder, context, HostRole.Admin);

        PaduraksaListenOptions listen = context.Options.Listen;
        // The validator already checked this; failing here would mean options that bypassed it.
        if (!IPAddress.TryParse(listen.AdminBind, out IPAddress? bind))
        {
            throw new InvalidOperationException(
                $"{PaduraksaOptions.SectionName}:Listen:AdminBind must be an IP address, was '{listen.AdminBind}'.");
        }

        // The local CA's certificate, ensured by the core host's AdminCertificateService before this app starts and hot-swapped
        // per handshake on a renewal. On any failure that service keeps serving the previous certificate (the in-memory
        // placeholder until one was ever loaded), so this listener is always up.
        AdminCertificateService adminCertificate = context.Shared.AdminCertificate;

        AdminSourcePolicy sources = new(listen.AdminAllowedSources);
        AdminInternalListener internalListener = context.Shared.AdminInternal;
        ILogger logger = new RoleLoggerFactory(context.Shared.LoggerFactory, HostRole.Admin).CreateLogger(typeof(AdminApp).FullName!);
        ListenOptions? internalListenOptions = null;
        builder.WebHost.UseKestrelCore().UseKestrelHttpsConfiguration();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(bind, listen.Admin, admin =>
            {
                admin.Protocols = HttpProtocols.Http1AndHttp2;
                RestrictSources(admin, sources);
                admin.UseHttps(adminCertificate.CreateTlsOptions());
            });

            // PLAN §3.1: the internal listener the public proxy reaches. Plain HTTP by design: it is a Unix socket in the
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

            // PLAN §3: opt-in, loopback only. ListenLocalhost binds 127.0.0.1 and ::1 and nothing else; no setting moves it.
            if (listen.AdminLoopbackHttp > 0)
            {
                kestrel.ListenLocalhost(listen.AdminLoopbackHttp, fallback => fallback.Protocols = HttpProtocols.Http1);
            }
        });
        builder.Services.AddRoutingCore();
        builder.Services.AddSingleton(sources);
        builder.Services.AddPaduraksaAdminAuth(context.Options.Admin.ToSecurityOptions(listen.AdminLoopbackHttp, [.. listen.AdminAllowedSources]));
        builder.Services.AddPaduraksaAdminApi();
        builder.Services.AddPaduraksaAdminWeb(DashboardWeb);
        builder.Services.AddPaduraksaDashboard(new DashboardOptions
        {
            InstanceName = context.Options.Admin.InstanceName ?? Environment.MachineName,
            MinPasswordLength = context.Options.Admin.MinPasswordLength,
            SessionRecheckInterval = context.Options.Admin.SessionRecheckInterval,
        });
        // The dashboard's password checks (change password, TOTP, recovery codes) share the API's derivation slots.
        builder.Services.Replace(ServiceDescriptor.Singleton<IPasswordCheckSlots>(static sp => new AdminPasswordCheckSlots(sp.GetRequiredService<AdminThrottle>())));
        DashboardAssets.Use(builder);

        HostSetup.ApplyCallerServices(builder, context, HostRole.Admin);
        HostSetup.PinListenerConfiguration(builder);

        WebApplication app = builder.Build();
        app.Lifetime.ApplicationStarted.Register(() => CompleteInternalListener(internalListener, internalListenOptions, logger));
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

    /// <summary>
    /// Finishes the internal listener once Kestrel has bound: the Unix socket's mode is set to 0600 (the directory is already
    /// 0700), or the fallback's OS-assigned port is recorded so the proxy's connect callback can reach it.
    /// </summary>
    private static void CompleteInternalListener(AdminInternalListener listener, ListenOptions? internalOptions, ILogger logger)
    {
        if (!listener.Enabled)
        {
            return;
        }

        try
        {
            if (listener.UsesUnixSocket)
            {
                string path = listener.SocketPath!;
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

            int? port = internalOptions?.IPEndPoint?.Port;
            if (port is > 0)
            {
                listener.BindLoopback(port.Value);
                LogInternalLoopback(logger, listener.FallbackReason?.Message ?? "unknown reason", $"127.0.0.1:{port.Value}");
                return;
            }

            LogInternalPortUnknown(logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
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

    /// <summary>PLAN §11.3: unauthenticated, status only, never any detail.</summary>
    /// <remarks>
    /// A degraded start (the proxy serves the last-known-good config because the config database can't be used, PLAN
    /// §6.1) is still <b>ready</b>: the proxy serves traffic, and taking it out of rotation would turn a config-store
    /// problem into an outage. <c>/readyz</c> then answers 200 with the body <c>degraded</c> instead of <c>ready</c>, a
    /// status a keyword monitor can alert on, and the cause is logged at Critical. The admin UI shows the banner (M5).
    /// </remarks>
    private static void MapHealth(WebApplication app)
    {
        SnapshotStore snapshots = app.Services.GetRequiredService<SnapshotStore>();
        HealthState health = app.Services.GetRequiredService<HealthState>();
        ConfigService config = app.Services.GetRequiredService<ConfigService>();

        // Live: the process is up and a snapshot is loaded.
        app.MapMethods("/healthz", GetAndHead, context =>
            WriteStatusAsync(context, snapshots.HasPublished, "ok\n", "unavailable\n"));

        // Ready: listeners bound, a snapshot live and not draining; degraded or not (see the remarks). The vault is implied:
        // the core host starts it first and a vault that can't start stops the process before any listener binds (PLAN §3, §8).
        app.MapMethods("/readyz", GetAndHead, context =>
            WriteStatusAsync(context, health.IsReady && snapshots.HasPublished, config.Status.IsDegraded ? "degraded\n" : "ready\n", "not ready\n"));
    }

    private static Task WriteStatusAsync(HttpContext context, bool healthy, string okBody, string failBody)
    {
        HttpResponse response = context.Response;
        response.StatusCode = healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
        response.Headers.CacheControl = "no-store";
        response.ContentType = "text/plain; charset=utf-8";
        return HttpMethods.IsHead(context.Request.Method)
            ? Task.CompletedTask
            : response.WriteAsync(healthy ? okBody : failBody, context.RequestAborted);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The admin internal listener is the Unix socket {Path}")]
    private static partial void LogInternalSocket(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The admin internal TCP fallback listener is {Address}; the Unix socket couldn't be used: {Reason}")]
    private static partial void LogInternalLoopback(ILogger logger, string reason, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't check the owner of {Path}; this platform exposes no statx owner. Continuing with the symlink check only")]
    private static partial void LogOwnershipSkipped(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "The admin internal listener bound, but its loopback port could not be read; proxied admin requests answer 502")]
    private static partial void LogInternalPortUnknown(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not finish the admin internal listener; proxied admin requests may answer 502")]
    private static partial void LogInternalFinishFailed(ILogger logger, Exception exception);
}
