using System.Net;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Paduraksa.Core.Hosting;
using Paduraksa.Server.Configuration;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The <c>paduraksa</c> process: one core host, the public app (generic web host) and the admin <c>WebApplication</c>, sharing one
/// <see cref="SharedServices"/>.
/// <para><b>Start:</b> core first (snapshot loaded), then public and admin concurrently; if either fails the other's start
/// is cancelled, everything that started is stopped and the failure rethrown.</para>
/// <para><b>Stop:</b> readiness flips to "not ready", then public (drains in-flight proxied requests), then admin, then core.
/// Public goes first because it is the traffic-bearing side: the admin still answers <c>/readyz</c> with 503 while it drains,
/// operators keep the dashboard, and requests proxied to the admin's internal listener can still complete. Core goes last
/// because its background services serve both apps. All three share one budget, <see cref="PaduraksaOptions.ShutdownTimeout"/>;
/// the shared singletons are disposed after all three containers.</para>
/// </summary>
internal sealed partial class PaduraksaHost : IAsyncDisposable
{
    private readonly ConfigurationManager _configuration;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource<ShutdownRequest> _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _shutdownToken = new();
    private readonly TaskCompletionSource _startFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Bounds the stop that follows an aborted start; a StopAsync arriving during the start links its token to it.
    private readonly CancellationTokenSource _abortedStartDrain = new();
    private int _state = (int)HostState.Created;
    private int _disposed;
    private int _signalCount;

    // Only ever flips false -> true (a host failed to stop) and is read after that stop has completed. There's no
    // read-modify-write, so volatile (visibility across async continuations on other threads) is all it needs.
    private volatile bool _stopFaulted;

    private PaduraksaHost(ConfigurationManager configuration, SharedServices shared, IHost core, IHost publicApp, WebApplication adminApp)
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

    /// <summary>The public app: a generic host with an explicit pipeline (see <see cref="PublicApp"/>).</summary>
    public IHost Public { get; }

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
        var environmentName = hostOptions.EnvironmentName
                              ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                              ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                              ?? Environments.Production;
        var contentRootPath = Path.GetFullPath(hostOptions.ContentRootPath ?? AppContext.BaseDirectory);

        ConfigurationManager configuration = new();
        SharedServices? shared = null;
        List<IDisposable> built = [];
        try
        {
            AddConfigurationSources(configuration, hostOptions, environmentName, contentRootPath);

            var options = BindAndValidate(configuration, hostOptions.AllowEphemeralPorts, contentRootPath);
            shared = SharedServices.Create(configuration, options, hostOptions.ConfigureShared, hostOptions.ConfigureLogging);

            // Configuration shapes that would otherwise be silently ignored (a list child that is not an array index).
            var startupLogger = new RoleLoggerFactory(shared.LoggerFactory, HostRole.Core).CreateLogger(typeof(PaduraksaHost).FullName!);
            foreach (var warning in options.ListBindingWarnings)
            {
                LogConfigurationWarning(startupLogger, warning);
            }

            HostContext context = new(
                typeof(PaduraksaHost).Assembly.GetName().Name ?? "paduraksa",
                environmentName,
                contentRootPath,
                configuration,
                options,
                shared,
                hostOptions.ConfigureServices);

            var core = Track(built, CoreHost.Build(context));
            var publicApp = Track(built, PublicApp.Build(context));
            var adminApp = Track(built, AdminApp.Build(context));
            return new PaduraksaHost(configuration, shared, core, publicApp, adminApp);
        }
        catch
        {
            // Each step is guarded so a failing cleanup can't replace the original exception. Before the shared
            // logger factory exists there's nothing to log to, but then the only cleanup left is the configuration.
            ILogger logger = shared is null
                ? NullLogger.Instance
                : new RoleLoggerFactory(shared.LoggerFactory, HostRole.Core).CreateLogger<PaduraksaHost>();
            for (var i = built.Count - 1; i >= 0; i--)
            {
                FailureCleanup.Run(built[i].Dispose, logger, "disposing a host");
            }

            if (shared is { } builtShared)
            {
                FailureCleanup.Run(() => builtShared.DisposeAsync().AsTask().GetAwaiter().GetResult(), logger, "disposing shared services");
            }

            FailureCleanup.Run(configuration.Dispose, logger, "disposing the configuration");
            throw;
        }
    }

    /// <summary>
    /// Starts core, then public and admin concurrently. Can be called once; a second call throws.
    /// <para>On success the state is <see cref="HostState.Running"/>. On failure everything that started is stopped
    /// again, the state becomes <see cref="HostState.Faulted"/> and the failure is rethrown.</para>
    /// <para>A shutdown request arriving while starting (<see cref="StopAsync"/>, <see cref="RequestShutdown"/>, a
    /// signal, or a host stopping on its own) aborts the start the same way and throws
    /// <see cref="OperationCanceledException"/>. The state then becomes <see cref="HostState.Stopped"/>, or
    /// <see cref="HostState.Faulted"/> if a host stopped on its own. Cancelling <paramref name="cancellationToken"/>
    /// is such a request too (as if <see cref="RequestShutdown"/> had been called): the host won't run afterwards.</para>
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, (int)HostState.Starting, (int)HostState.Created) != (int)HostState.Created)
        {
            throw new InvalidOperationException($"The host can only be started once (state: {State}).");
        }

        try
        {
            using var cancellation = cancellationToken.Register(static self => ((PaduraksaHost)self!).RequestShutdown(), this);
            await StartHostsAsync().ConfigureAwait(false);
        }
        finally
        {
            // The state is final by now; a StopAsync that arrived during the start waits for this.
            _startFinished.TrySetResult();
        }
    }

    /// <summary>
    /// Graceful stop in order public, admin, core. Doesn't dispose anything. Depends on the state:
    /// <list type="bullet">
    /// <item><b>Running:</b> stops, and the state becomes <see cref="HostState.Stopped"/>.</item>
    /// <item><b>Starting:</b> the stop takes effect. It requests shutdown, which aborts the start (<see cref="StartAsync"/>
    /// stops whatever already started and throws to its caller), and waits for the start to finish. If the start
    /// completed anyway, the host is then stopped as if it had been running.</item>
    /// <item><b>Created</b> (never started), <b>Stopping</b> (another stop is in progress and isn't awaited),
    /// <b>Stopped</b>, <b>Faulted</b>: no-op.</item>
    /// </list>
    /// In both stopping cases <paramref name="cancellationToken"/> cuts the graceful drain short: the hosts still
    /// stop, without waiting. It never makes this method throw.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (State == HostState.Starting)
        {
            using var drain = cancellationToken.Register(static cts => ((CancellationTokenSource)cts!).Cancel(), _abortedStartDrain);
            RequestShutdown();
            await _startFinished.Task.ConfigureAwait(false);
        }

        if (Interlocked.CompareExchange(ref _state, (int)HostState.Stopping, (int)HostState.Running) != (int)HostState.Running)
        {
            return;
        }

        RequestShutdown();
        await StopHostsAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _state, (int)HostState.Stopped);
        LogStopped(_logger);
    }

    /// <summary>Asks <see cref="RunAsync"/> to shut down and aborts a <see cref="StartAsync"/> in progress. The first request wins.</summary>
    public void RequestShutdown() => RequestShutdownCore(new ShutdownRequest(ShutdownReason.Requested, Role: null));

    /// <summary>
    /// <c>paduraksa serve</c>: start, wait for a signal or another shutdown request, stop gracefully.
    /// Process-global signal handling lives only here, so tests drive <see cref="StartAsync"/>/<see cref="StopAsync"/> directly.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using var sigquit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, OnSignal);
        using var cancellation = cancellationToken.Register(static self => ((PaduraksaHost)self!).RequestShutdown(), this);

        try
        {
            await StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.Task.IsCompleted)
        {
            // Start was aborted by a shutdown request. A signal (or caller cancellation) is a clean exit;
            // a host stopping itself during startup is a startup failure.
            var aborted = await _shutdown.Task.ConfigureAwait(false);
            return aborted.Reason == ShutdownReason.HostStopped ? ExitCodes.StartupFailed
                : _stopFaulted ? ExitCodes.Failure
                : ExitCodes.Success;
        }
#pragma warning disable CA1031 // Startup failures are logged by StartAsync and turned into an exit code.
        catch (Exception)
#pragma warning restore CA1031
        {
            return ExitCodes.StartupFailed;
        }

        var request = await _shutdown.Task.ConfigureAwait(false);
        var initiator = request.Role?.ToLogName() ?? "process";
        LogShuttingDown(_logger, request.Reason, initiator);
        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        return request.Reason == ShutdownReason.HostStopped || _stopFaulted ? ExitCodes.Failure : ExitCodes.Success;
    }

    /// <summary>Bound addresses of a web app (e.g. <c>https://[::]:443</c>); empty for the core host or before start.</summary>
    /// <remarks>The admin app's internal listener is process-local plumbing, not a client endpoint, so it is left out.</remarks>
    public IReadOnlyList<string> GetListenAddresses(HostRole role)
    {
        var app = role switch
        {
            HostRole.Public => Public,
            HostRole.Admin => Admin,
            _ => null,
        };
        IReadOnlyList<string> addresses = app?.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToArray() ?? [];
        return role == HostRole.Admin
            ? [.. addresses.Where(address => !IsInternalAdminAddress(address, Shared.AdminInternal))]
            : addresses;
    }

    private static bool IsInternalAdminAddress(string address, AdminInternalListener listener)
    {
        if (!listener.Enabled)
        {
            return false;
        }

        // Kestrel lists a Unix-socket listener as http://unix:<path>; the loopback fallback as its loopback URL.
        if (listener.UsesUnixSocket)
        {
            return address.StartsWith("http://unix:", StringComparison.OrdinalIgnoreCase);
        }

        return listener.LoopbackEndPoint is { } bound
            && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttp
            && uri.Port == bound.Port
            && IPAddress.TryParse(uri.Host, out var parsed)
            && IPAddress.IsLoopback(parsed!);
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
            // Runs while a disposal failure may be propagating; don't let these replace it.
            FailureCleanup.Run(_configuration.Dispose, _logger, "disposing the configuration");
            _shutdownToken.Dispose();
            _abortedStartDrain.Dispose();
        }
    }

    /// <summary>
    /// The bootstrap options alone (appsettings, environment, validated, the data directory fully qualified), without building any
    /// host: for CLI commands that only need to know where the data is.
    /// </summary>
    /// <exception cref="OptionsValidationException">The <c>Paduraksa</c> settings are invalid.</exception>
    internal static PaduraksaOptions LoadOptions(PaduraksaHostOptions? hostOptions = null)
    {
        hostOptions ??= new PaduraksaHostOptions();
        var environmentName = hostOptions.EnvironmentName
                              ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                              ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                              ?? Environments.Production;
        var contentRootPath = Path.GetFullPath(hostOptions.ContentRootPath ?? AppContext.BaseDirectory);
        using ConfigurationManager configuration = new();
        AddConfigurationSources(configuration, hostOptions, environmentName, contentRootPath);
        return BindAndValidate(configuration, hostOptions.AllowEphemeralPorts, contentRootPath);
    }

    private static void AddConfigurationSources(ConfigurationManager configuration, PaduraksaHostOptions hostOptions, string environmentName, string contentRootPath)
    {
        configuration.SetBasePath(contentRootPath);

        // Bootstrap config only (the real config lives in the database), so no reload-on-change watchers.
        configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        configuration.AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false);
        configuration.AddEnvironmentVariables();
        hostOptions.ConfigureConfiguration?.Invoke(configuration);
    }

    private static PaduraksaOptions BindAndValidate(ConfigurationManager configuration, bool allowEphemeralPorts, string contentRootPath)
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

        // The binder ignores a scalar where an IList<string> is expected (and anything that isn't an array index):
        // normalise both environment forms here, before the validator reads the lists.
        ConfigurationListOptions.Bind(configuration, options);

        // Before the validator reads it: a relative trust file is relative to the content root, like the data directory below.
        if (options.Acme is { TrustedRootsFile: { Length: > 0 } trustedRoots }
            && trustedRoots.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) < 0
            && !Path.IsPathFullyQualified(trustedRoots))
        {
            options.Acme.TrustedRootsFile = Path.GetFullPath(trustedRoots, contentRootPath);
        }

        var result = new PaduraksaOptionsValidator(allowEphemeralPorts).Validate(Microsoft.Extensions.Options.Options.DefaultName, options);
        if (result.Failed)
        {
            throw new OptionsValidationException(PaduraksaOptions.SectionName, typeof(PaduraksaOptions), result.Failures);
        }

        // Relative paths resolve against the content root, like appsettings, never against the working directory
        // (a systemd unit with WorkingDirectory=/ would otherwise put the data in /data).
        if (!Path.IsPathFullyQualified(options.DataDirectory))
        {
            try
            {
                options.DataDirectory = Path.GetFullPath(options.DataDirectory, contentRootPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // The validator rejects the known cases ('\0'); anything else the platform refuses is still bad config.
                throw new OptionsValidationException(
                    PaduraksaOptions.SectionName,
                    typeof(PaduraksaOptions),
                    [$"{PaduraksaOptions.SectionName}:DataDirectory isn't a valid path: {ex.Message}"]);
            }
        }

        // The logs live under the data directory unless told otherwise, and a relative Logs:Directory is relative to the data
        // directory (not the content root): the whole point of the data directory is one tree to mount and back up.
        try
        {
            options.Logs.ResolvedDirectory = string.IsNullOrWhiteSpace(options.Logs.Directory)
                ? Path.Combine(options.DataDirectory, Paduraksa.Core.Logging.LogFileNames.DefaultDirectoryName)
                : Path.IsPathFullyQualified(options.Logs.Directory)
                    ? options.Logs.Directory
                    : Path.GetFullPath(options.Logs.Directory, options.DataDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new OptionsValidationException(
                PaduraksaOptions.SectionName,
                typeof(PaduraksaOptions),
                [$"{PaduraksaOptions.SectionName}:Logs:Directory isn't a valid path: {ex.Message}"]);
        }

        return options;
    }

    private static T Track<T>(List<IDisposable> built, T host)
        where T : IDisposable
    {
        built.Add(host);
        return host;
    }

    private async Task StartWebAppAsync(IHost app, CancellationTokenSource startCts)
    {
        try
        {
            await app.StartAsync(startCts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Fail fast: don't let the sibling app finish starting only to be stopped again. Cancellation runs
            // registered callbacks, which may throw; that must not replace the start failure.
            await FailureCleanup.RunAsync(startCts.CancelAsync, _logger, "cancelling the sibling app's start").ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartHostsAsync()
    {
        try
        {
            WatchForSelfStop(Core, HostRole.Core);
            WatchForSelfStop(Public, HostRole.Public);
            WatchForSelfStop(Admin, HostRole.Admin);

            // Any shutdown request cancels the start: StopAsync, RequestShutdown, a signal, the caller's token, or a
            // host stopping itself.
            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken.Token);
            await Core.StartAsync(startCts.Token).ConfigureAwait(false);
            await Task.WhenAll(
                StartWebAppAsync(Public, startCts),
                StartWebAppAsync(Admin, startCts)).ConfigureAwait(false);

            // A shutdown can be requested without any start observing the cancellation, e.g. once Kestrel has
            // already bound. Since .NET 10, BackgroundService.ExecuteAsync always runs in the background, so a host
            // stopping itself after a crash surfaces this way too. Either way the process must not end up running.
            if (_shutdown.Task.IsCompleted)
            {
                throw new OperationCanceledException("Shutdown was requested during startup.");
            }
        }
        catch (Exception ex)
        {
            ShutdownRequest? abortedBy = ex is OperationCanceledException && _shutdown.Task.IsCompleted ? _shutdown.Task.Result : null;
            if (abortedBy is { Role: { } stoppedRole })
            {
                // A host stopped itself mid-start (e.g. a BackgroundService crashed). That host already logged the
                // real error; the cancellation is only how the remaining starts were aborted.
                var stoppedApp = stoppedRole.ToLogName();
                LogHostStoppedDuringStartup(_logger, stoppedApp);
            }
            else if (abortedBy is { } request)
            {
                LogStartAborted(_logger, request.Reason);
            }
            else
            {
                LogStartFailed(_logger, ex);
            }

            await FailureCleanup.RunAsync(() => StopHostsAsync(_abortedStartDrain.Token), _logger, "stopping the hosts").ConfigureAwait(false);

            // A requested stop ends cleanly; a host stopping on its own mid-start is a failed startup.
            var final = abortedBy is { Reason: not ShutdownReason.HostStopped } ? HostState.Stopped : HostState.Faulted;
            Volatile.Write(ref _state, (int)final);
            throw;
        }

        Shared.Health.MarkListenersBound();
        Volatile.Write(ref _state, (int)HostState.Running);
    }

    private async Task StopHostsAsync(CancellationToken cancellationToken)
    {
        Shared.Health.MarkStopping();

        // One budget for the whole stop, not one per host: the public drain may use most of it. Once it's spent,
        // admin and core still stop, just without waiting (Kestrel aborts the remaining connections).
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Options.ShutdownTimeout);

        // The renewal scheduler first: a certificate it is working on (an HTTP-01 validation needs the public listener to answer) gets a
        // bounded time to finish, and nothing new starts, before the public drain closes the listener.
        await DrainSchedulerAsync(budget.Token).ConfigureAwait(false);
        await StopHostAsync(Public, HostRole.Public, budget.Token).ConfigureAwait(false);
        await StopHostAsync(Admin, HostRole.Admin, budget.Token).ConfigureAwait(false);
        await StopHostAsync(Core, HostRole.Core, budget.Token).ConfigureAwait(false);
    }

    /// <summary>How long the scheduler's current certificate may take to finish when the process stops (within the shutdown budget).</summary>
    private static readonly TimeSpan SchedulerDrainGrace = TimeSpan.FromSeconds(10);

    private async Task DrainSchedulerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Shared.AcmeScheduler.DrainAsync(SchedulerDrainGrace, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The scheduler failing to stop must not keep the hosts running; the core host stops it again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _stopFaulted = true;
            LogStopFailed(_logger, HostRole.Core.ToLogName(), ex);
        }
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
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(
            static state =>
            {
                var (self, stoppedRole) = ((PaduraksaHost, HostRole))state!;
                self.RequestShutdownCore(new ShutdownRequest(ShutdownReason.HostStopped, stoppedRole));
            },
            (this, role));
    }

    private void RequestShutdownCore(ShutdownRequest request)
    {
        if (_shutdown.TrySetResult(request))
        {
            if (request.Reason != ShutdownReason.HostStopped)
            {
                // Before cancelling: the generic hosts log the cancelled start as "Hosting failed to start".
                Shared.StartupLogFilter.StartAbortRequested();
            }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Start aborted (reason: {Reason}); stopping everything that started")]
    private static partial void LogStartAborted(ILogger logger, ShutdownReason reason);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Paduraksa failed to start: the {App} host stopped during startup (see its error above); stopping everything that started")]
    private static partial void LogHostStoppedDuringStartup(ILogger logger, string app);

    [LoggerMessage(Level = LogLevel.Error, Message = "The {App} host failed to dispose cleanly")]
    private static partial void LogDisposeFailed(ILogger logger, string app, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shutting down (reason: {Reason}, initiated by: {Initiator})")]
    private static partial void LogShuttingDown(ILogger logger, ShutdownReason reason, string initiator);

    [LoggerMessage(Level = LogLevel.Information, Message = "Paduraksa stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configuration: {Warning}")]
    private static partial void LogConfigurationWarning(ILogger logger, string warning);

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

/// <summary>Process exit codes of <c>paduraksa serve</c>.</summary>
internal static class ExitCodes
{
    /// <summary>Stopped on request (signal, or a shutdown request), including during startup.</summary>
    public const int Success = 0;

    /// <summary>Ran, then a host stopped on its own, a host failed to stop cleanly, or disposal failed.</summary>
    public const int Failure = 1;

    /// <summary>
    /// Never ran: a host failed to start (e.g. a port is taken) or stopped itself during startup, or the process
    /// couldn't be built (e.g. an unreadable configuration file).
    /// </summary>
    public const int StartupFailed = 2;

    /// <summary>The <c>Paduraksa</c> settings failed validation, or a configuration file couldn't be parsed.</summary>
    public const int InvalidConfiguration = 3;
}
