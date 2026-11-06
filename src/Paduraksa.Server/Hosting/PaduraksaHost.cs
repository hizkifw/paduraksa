using System.Runtime.InteropServices;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Paduraksa.Server.Configuration;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The <c>paduraksa</c> process: one core host plus the public and admin <c>WebApplication</c>s, sharing one
/// <see cref="SharedServices"/> (PLAN §3).
/// <para><b>Start:</b> core host first (snapshot loaded), then public and admin concurrently. If either fails, the
/// other one's start is cancelled, everything that started is stopped, and the failure is rethrown.</para>
/// <para><b>Stop:</b> readiness flips to "not ready", then public (stops accepting, drains in-flight proxied requests),
/// then admin, then core. Public goes first because it's the traffic-bearing side: while it drains, the admin still
/// answers <c>/readyz</c> with 503 (so orchestrators and load balancers see the drain), operators keep the dashboard,
/// and requests proxied to the admin's internal listener (PLAN §3.1) can still complete. Core goes last because its
/// background services serve both apps. All three share one budget, <see cref="PaduraksaOptions.ShutdownTimeout"/>.
/// Shared singletons are disposed after all three containers.</para>
/// </summary>
internal sealed partial class PaduraksaHost : IAsyncDisposable
{
    private readonly ConfigurationManager _configuration;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<ShutdownRequest> _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _shutdownToken = new();
    private int _state = (int)HostState.Created;
    private int _disposed;
    private int _signalCount;
    private bool _stopFaulted;

    private PaduraksaHost(ConfigurationManager configuration, SharedServices shared, IHost core, WebApplication publicApp, WebApplication adminApp)
    {
        _configuration = configuration;
        Shared = shared;
        Core = core;
        Public = publicApp;
        Admin = adminApp;
        _logger = new RoleLoggerFactory(shared.LoggerFactory, HostRole.Core).CreateLogger<PaduraksaHost>();
    }

    public SharedServices Shared { get; }

    public PaduraksaOptions Options => Shared.Options;

    public IHost Core { get; }

    public WebApplication Public { get; }

    public WebApplication Admin { get; }

    public HostState State => (HostState)Volatile.Read(ref _state);

    /// <summary>Completes when shutdown is requested: signal, <see cref="RequestShutdown"/>, or one host stopping on its own.</summary>
    public Task<ShutdownRequest> ShutdownRequested => _shutdown.Task;

    /// <summary>
    /// Builds the whole process: configuration, validated options, shared singletons and the three hosts.
    /// Throws <see cref="OptionsValidationException"/> for invalid <c>Paduraksa</c> settings.
    /// </summary>
    public static PaduraksaHost Create(PaduraksaHostOptions? hostOptions = null)
    {
        hostOptions ??= new PaduraksaHostOptions();
        string environmentName = hostOptions.EnvironmentName
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environments.Production;
        string contentRootPath = Path.GetFullPath(hostOptions.ContentRootPath ?? AppContext.BaseDirectory);

        ConfigurationManager configuration = new();
        SharedServices? shared = null;
        List<IDisposable> built = [];
        try
        {
            configuration.SetBasePath(contentRootPath);
            // Bootstrap config only (the real config lives in the DB from M2), so no reload-on-change watchers.
            configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
            configuration.AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false);
            configuration.AddEnvironmentVariables();
            hostOptions.ConfigureConfiguration?.Invoke(configuration);

            PaduraksaOptions options = BindAndValidate(configuration, hostOptions.AllowEphemeralPorts);
            shared = SharedServices.Create(configuration, options, hostOptions.ConfigureShared);

            HostContext context = new(
                typeof(PaduraksaHost).Assembly.GetName().Name ?? "paduraksa",
                environmentName,
                contentRootPath,
                configuration,
                options,
                shared,
                hostOptions.ConfigureServices);

            IHost core = Track(built, CoreHost.Build(context));
            WebApplication publicApp = Track(built, PublicApp.Build(context));
            WebApplication adminApp = Track(built, AdminApp.Build(context));
            return new PaduraksaHost(configuration, shared, core, publicApp, adminApp);
        }
        catch
        {
            for (int i = built.Count - 1; i >= 0; i--)
            {
                built[i].Dispose();
            }

            shared?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            configuration.Dispose();
            throw;
        }
    }

    /// <summary>Starts core, then public and admin concurrently. On failure everything started is stopped again.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, (int)HostState.Starting, (int)HostState.Created) != (int)HostState.Created)
        {
            throw new InvalidOperationException($"The host can only be started once (state: {State}).");
        }

        try
        {
            WatchForSelfStop(Core, HostRole.Core);
            WatchForSelfStop(Public, HostRole.Public);
            WatchForSelfStop(Admin, HostRole.Admin);

            await Core.StartAsync(cancellationToken).ConfigureAwait(false);

            using CancellationTokenSource startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Task.WhenAll(
                StartWebAppAsync(Public, startCts),
                StartWebAppAsync(Admin, startCts)).ConfigureAwait(false);

            // A host can stop itself while the others are still starting (since .NET 10, BackgroundService.ExecuteAsync
            // always runs in the background, so a crash surfaces asynchronously) without any start being cancelled,
            // e.g. once Kestrel has already bound. That's still a failed startup, never a running process.
            if (_shutdown.Task.IsCompleted && _shutdown.Task.Result.Reason == ShutdownReason.HostStopped)
            {
                throw new OperationCanceledException("A host stopped on its own during startup.");
            }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && _shutdown.Task.IsCompleted && _shutdown.Task.Result.Role is { } stoppedRole)
            {
                // A host stopped itself mid-start (e.g. a BackgroundService crashed). That host already logged the
                // real error; the cancellation is only how the remaining starts were aborted.
                string stoppedApp = stoppedRole.ToLogName();
                LogHostStoppedDuringStartup(_logger, stoppedApp);
            }
            else
            {
                LogStartFailed(_logger, ex);
            }

            await StopHostsAsync(CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref _state, (int)HostState.Faulted);
            throw;
        }

        Shared.Health.MarkListenersBound();
        Volatile.Write(ref _state, (int)HostState.Running);
    }

    /// <summary>Graceful stop in order public, admin, core. No-op unless running. Doesn't dispose anything.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, (int)HostState.Stopping, (int)HostState.Running) != (int)HostState.Running)
        {
            return;
        }

        RequestShutdown();
        await StopHostsAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _state, (int)HostState.Stopped);
        LogStopped(_logger);
    }

    /// <summary>Asks <see cref="RunAsync"/> to shut down. The first request wins.</summary>
    public void RequestShutdown() => RequestShutdownCore(new ShutdownRequest(ShutdownReason.Requested, Role: null));

    /// <summary>
    /// <c>paduraksa serve</c>: start, wait for a signal or another shutdown request, stop gracefully.
    /// Process-global signal handling lives only here, so tests drive <see cref="StartAsync"/>/<see cref="StopAsync"/> directly.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using PosixSignalRegistration sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using PosixSignalRegistration sigquit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnSignal);
        using CancellationTokenRegistration cancellation = cancellationToken.Register(static self => ((PaduraksaHost)self!).RequestShutdown(), this);

        try
        {
            await StartAsync(_shutdownToken.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.Task.IsCompleted)
        {
            // Start was aborted by a shutdown request. A signal (or caller cancellation) is a clean exit;
            // a host stopping itself during startup is a startup failure.
            ShutdownRequest aborted = await _shutdown.Task.ConfigureAwait(false);
            return aborted.Reason == ShutdownReason.HostStopped ? ExitCodes.StartupFailed : ExitCodes.Success;
        }
#pragma warning disable CA1031 // Startup failures are logged by StartAsync and turned into an exit code.
        catch (Exception)
#pragma warning restore CA1031
        {
            return ExitCodes.StartupFailed;
        }

        ShutdownRequest request = await _shutdown.Task.ConfigureAwait(false);
        string initiator = request.Role?.ToLogName() ?? "process";
        LogShuttingDown(_logger, request.Reason, initiator);
        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        return request.Reason == ShutdownReason.HostStopped || _stopFaulted ? ExitCodes.Failure : ExitCodes.Success;
    }

    /// <summary>Bound addresses of a web app (e.g. <c>https://[::]:443</c>); empty for the core host or before start.</summary>
    public IReadOnlyList<string> GetListenAddresses(HostRole role)
    {
        WebApplication? app = role switch
        {
            HostRole.Public => Public,
            HostRole.Admin => Admin,
            _ => null,
        };
        return app?.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToArray() ?? [];
    }

    public IServiceProvider GetServices(HostRole role) => role switch
    {
        HostRole.Core => Core.Services,
        HostRole.Public => Public.Services,
        HostRole.Admin => Admin.Services,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    /// <summary>Stops if still running, disposes the three containers, then the shared singletons (exactly once).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // One container failing to dispose must not leak the others, nor skip the shared singletons.
            await DisposeHostAsync(Public, HostRole.Public).ConfigureAwait(false);
            await DisposeHostAsync(Admin, HostRole.Admin).ConfigureAwait(false);
            await DisposeHostAsync(Core, HostRole.Core).ConfigureAwait(false);
            await Shared.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _configuration.Dispose();
            _shutdownToken.Dispose();
        }
    }

    private static PaduraksaOptions BindAndValidate(ConfigurationManager configuration, bool allowEphemeralPorts)
    {
        PaduraksaOptions options;
        try
        {
            options = configuration.GetSection(PaduraksaOptions.SectionName).Get<PaduraksaOptions>() ?? new PaduraksaOptions();
        }
        catch (InvalidOperationException ex)
        {
            // The binder throws InvalidOperationException for unparsable values (e.g. Http = "eighty").
            throw new OptionsValidationException(PaduraksaOptions.SectionName, typeof(PaduraksaOptions), [ex.Message]);
        }

        ValidateOptionsResult result = new PaduraksaOptionsValidator(allowEphemeralPorts).Validate(Microsoft.Extensions.Options.Options.DefaultName, options);
        return result.Failed
            ? throw new OptionsValidationException(PaduraksaOptions.SectionName, typeof(PaduraksaOptions), result.Failures)
            : options;
    }

    private static T Track<T>(List<IDisposable> built, T host)
        where T : IDisposable
    {
        built.Add(host);
        return host;
    }

    private static async Task StartWebAppAsync(WebApplication app, CancellationTokenSource startCts)
    {
        try
        {
            await app.StartAsync(startCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Fail fast: don't let the sibling app finish starting only to be stopped again.
            await startCts.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StopHostsAsync(CancellationToken cancellationToken)
    {
        Shared.Health.MarkStopping();

        // One budget for the whole stop, not one per host: the public drain may use most of it. Once it's spent,
        // admin and core still stop, just without waiting (Kestrel aborts the remaining connections).
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Options.ShutdownTimeout);
        await StopHostAsync(Public, HostRole.Public, budget.Token).ConfigureAwait(false);
        await StopHostAsync(Admin, HostRole.Admin, budget.Token).ConfigureAwait(false);
        await StopHostAsync(Core, HostRole.Core, budget.Token).ConfigureAwait(false);
    }

    private async Task StopHostAsync(IHost host, HostRole role, CancellationToken cancellationToken)
    {
        try
        {
            // Bounded by the shared Paduraksa:ShutdownTimeout budget (the hosts' own timeout is disabled).
            await host.StopAsync(cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // One host failing to stop must not keep the others running.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _stopFaulted = true;
            LogStopFailed(_logger, role.ToLogName(), ex);
        }
    }

    private async Task DisposeHostAsync(IHost host, HostRole role)
    {
        try
        {
            if (host is IAsyncDisposable asyncHost)
            {
                await asyncHost.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                host.Dispose();
            }
        }
#pragma warning disable CA1031 // Keep disposing the rest; the failure is logged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogDisposeFailed(_logger, role.ToLogName(), ex);
        }
    }

    private void WatchForSelfStop(IHost host, HostRole role)
    {
        // A host stopping on its own (e.g. a BackgroundService failing with StopHost) takes the whole process down;
        // during an orchestrated stop this is a no-op because a request has already been recorded.
        IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(
            static state =>
            {
                (PaduraksaHost self, HostRole stoppedRole) = ((PaduraksaHost, HostRole))state!;
                self.RequestShutdownCore(new ShutdownRequest(ShutdownReason.HostStopped, stoppedRole));
            },
            (this, role));
    }

    private void RequestShutdownCore(ShutdownRequest request)
    {
        if (_shutdown.TrySetResult(request))
        {
            _shutdownToken.Cancel();
        }
    }

    private void OnSignal(PosixSignalContext context)
    {
        // Count signals, not shutdown requests: the first SIGTERM after a host stopped on its own is still graceful.
        if (Interlocked.Increment(ref _signalCount) > 1)
        {
            // Second signal: let the default handler terminate the process immediately.
            return;
        }

        context.Cancel = true;
        RequestShutdownCore(new ShutdownRequest(ShutdownReason.Signal, Role: null));
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Paduraksa failed to start; stopping everything that started")]
    private static partial void LogStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Paduraksa failed to start: the {App} host stopped during startup (see its error above); stopping everything that started")]
    private static partial void LogHostStoppedDuringStartup(ILogger logger, string app);

    [LoggerMessage(Level = LogLevel.Error, Message = "The {App} host failed to dispose cleanly")]
    private static partial void LogDisposeFailed(ILogger logger, string app, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shutting down (reason: {Reason}, initiated by: {Initiator})")]
    private static partial void LogShuttingDown(ILogger logger, ShutdownReason reason, string initiator);

    [LoggerMessage(Level = LogLevel.Information, Message = "Paduraksa stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The {App} host failed to stop cleanly")]
    private static partial void LogStopFailed(ILogger logger, string app, Exception exception);
}

internal enum HostState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped,
    Faulted,
}

internal enum ShutdownReason
{
    /// <summary><see cref="PaduraksaHost.RequestShutdown()"/> or <see cref="PaduraksaHost.StopAsync"/> was called.</summary>
    Requested,

    /// <summary>SIGINT / SIGTERM / SIGQUIT.</summary>
    Signal,

    /// <summary>One of the hosts stopped on its own; treated as a failure.</summary>
    HostStopped,
}

internal readonly record struct ShutdownRequest(ShutdownReason Reason, HostRole? Role);

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int StartupFailed = 2;
    public const int InvalidConfiguration = 3;
}
