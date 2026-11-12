using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Paduraksa.Admin.Certificates;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Cloudflare;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Issuance;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Application.Logging;
using Paduraksa.Application.Metrics;
using Paduraksa.Application.Notifications;
using Paduraksa.Application.Security;
using Paduraksa.Application.Simulation;
using Paduraksa.Core.Acme;
using Paduraksa.Core.Hosting;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Logging;
using Paduraksa.Server.Placeholders;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The process-lifetime singletons shared by the core host, the public app and the admin app.
/// <para>
/// Every one is <b>constructed here, exactly once</b>: <see cref="Builder.Add{TService}"/> accepts ready-made instances
/// only, with no factory overload, so no container can build its own copy. <see cref="RegisterInto"/> registers those same
/// instances into a container, and since Microsoft.Extensions.DependencyInjection never disposes an instance it didn't
/// create, disposing the three containers leaves them alone. <see cref="DisposeAsync"/> disposes each one exactly once, in
/// reverse creation order, after every host has stopped; the logger factory is created first, so it is disposed last and
/// shutdown logs still get flushed.
/// </para>
/// </summary>
internal sealed class SharedServices : IAsyncDisposable
{
    private readonly IReadOnlyList<KeyValuePair<Type, object>> _registrations;
    private readonly IReadOnlyList<object> _owned;
    private int _disposed;

    private SharedServices(Builder builder, ILoggerFactory loggerFactory, IDataProtectionKeyStore keyRing)
    {
        _registrations = [.. builder.Registrations];
        _owned = [.. builder.Owned];
        LoggerFactory = loggerFactory;
        TimeProvider = Get<TimeProvider>();
        Options = Get<PaduraksaOptions>();
        Health = Get<HealthState>();
        SnapshotStore = Get<SnapshotStore>();
        SnapshotPublisher = Get<SnapshotPublisher>();
        ServerCertificate = Get<PlaceholderServerCertificate>();
        Certificates = Get<PlaceholderCertificates>();
        AdminCertificate = Get<AdminCertificateService>();
        AdminInternal = Get<AdminInternalListener>();
        ProofOfWork = Get<ProofOfWorkRuntime>();
        ProofOfWorkKey = Get<ProofOfWorkKeyStartup>();
        RateLimits = Get<RateLimitRuntime>();
        Metrics = Get<MetricsRegistry>();
        EdgeRanges = Get<CloudflareEdgeRangeSource>();
        RangeReport = Get<CloudflareRangeReport>();
        RangeRefresher = _registrations.FirstOrDefault(static r => r.Key == typeof(CloudflareRangeRefresher)).Value as CloudflareRangeRefresher;
        AccessLists = Get<AccessListRuntime>();
        LogFiles = Get<ServerLogFiles>();
        LogStore = Get<ILogFileStore>();
        Catalog = Get<ICertificateCatalog>();
        PasswordCredentials = Get<IPasswordCredentials>();
        KeyRing = keyRing;
        PublicListeners = Get<PublicListenerSetup>();
        Config = Get<ConfigService>();
        CertificateService = Get<CertificateService>();
        CertificateOperations = Get<CertificateOperations>();
        CertificateRecovery = Get<CertificateRecovery>();
        RecoveryCompletion = Get<CertificateRecoveryCompletion>();
        Challenges = Get<AcmeChallengeStore>();
        AcmeAccounts = Get<AcmeAccountService>();
        DnsProviders = Get<DnsProviderService>();
        AcmeIssuance = Get<AcmeIssuanceService>();
        AcmeScheduler = Get<AcmeRenewalScheduler>();
        Simulator = Get<RequestSimulator>();
        Notifications = Get<NotificationService>();
        HealthBoard = Get<UpstreamHealthBoard>();
        LastKnownGood = Get<LastKnownGoodFile>();
        Users = Get<UserService>();
        Sessions = Get<SessionService>();
        TwoFactor = Get<TwoFactorService>();
        Login = Get<LoginService>();
        ApiTokens = Get<ApiTokenService>();
        Setup = Get<SetupService>();
        Permissions = Get<IPermissionEvaluator>();
    }

    /// <summary>Notification channels: add, replace, delete and test (the admin API and the dashboard).</summary>
    public NotificationService Notifications { get; }

    /// <summary>Every upstream's live health, as the health watcher last read it.</summary>
    public UpstreamHealthBoard HealthBoard { get; }

    /// <summary>User management, the first of the identity services below that the admin app and the REST API use.</summary>
    public UserService Users { get; }

    public SessionService Sessions { get; }

    public TwoFactorService TwoFactor { get; }

    /// <summary>Sign-in with a password and, for users with one, a second factor.</summary>
    public LoginService Login { get; }

    public ApiTokenService ApiTokens { get; }

    /// <summary>First-run setup: whether an Owner exists, and the one-time setup token.</summary>
    public SetupService Setup { get; }

    /// <summary>What an actor may do; the real authorizers decide with it.</summary>
    public IPermissionEvaluator Permissions { get; }

    /// <summary>The one logger factory of the process. Each host sees it through a <see cref="RoleLoggerFactory"/>.</summary>
    public ILoggerFactory LoggerFactory { get; }

    public TimeProvider TimeProvider { get; }

    public PaduraksaOptions Options { get; }

    public HealthState Health { get; }

    public SnapshotStore SnapshotStore { get; }

    /// <summary>The single writer of <see cref="SnapshotStore"/>; attaches to the public app's YARP when it starts.</summary>
    public SnapshotPublisher SnapshotPublisher { get; }

    /// <summary>Silences the generic hosts' error about a start that was aborted on request.</summary>
    public StartupLogFilter StartupLogFilter { get; } = new();

    /// <summary>The in-memory placeholder certificate. The public listener's fallback default in tests, and the admin listener's last-resort certificate until the local CA's is ready.</summary>
    public PlaceholderServerCertificate ServerCertificate { get; }

    /// <summary>
    /// The placeholder's certificate source and built-in inventory entries (tests add certificates here). It is wired into the
    /// process (as a source after the vault, and as the built-in inventory) only when
    /// <see cref="Configuration.PaduraksaDevelopmentOptions.PlaceholderCertificate"/> is on; otherwise this object is inert.
    /// </summary>
    public PlaceholderCertificates Certificates { get; }

    /// <summary>
    /// The admin listener's certificate: the local-CA leaf the admin app serves, kept current by a background
    /// service of the core host. Its <see cref="AdminCertificateService.ServingOptions"/> are the listener's TLS options.
    /// </summary>
    public AdminCertificateService AdminCertificate { get; }

    /// <summary>
    /// The admin app's internal listener: a Unix socket (0600, in a 0700 directory) or the loopback fallback, through
    /// which the public proxy reaches the admin. Exists independent of the config, so creating an admin host needs no restart.
    /// </summary>
    public AdminInternalListener AdminInternal { get; }

    /// <summary>The proof-of-work gate's key, clock and counters; the compiler hands it to every protected route.</summary>
    public ProofOfWorkRuntime ProofOfWork { get; }

    /// <summary>Opens the proof-of-work key at startup, before the public listener binds (see <see cref="ProofOfWorkKeyStartup"/>).</summary>
    public ProofOfWorkKeyStartup ProofOfWorkKey { get; }

    /// <summary>Cloudflare's edge ranges in force: what the compiler reads, the refresh job replaces and the report describes.</summary>
    public CloudflareEdgeRangeSource EdgeRanges { get; }

    /// <summary>What the dashboard says about the edge ranges (public facts).</summary>
    public CloudflareRangeReport RangeReport { get; }

    /// <summary>The job that keeps the edge ranges current; null when <c>Paduraksa:Cloudflare:RangeRefresh:Enabled</c> is off. A
    /// hosted service of the core host (see <see cref="CoreHost"/>).</summary>
    public CloudflareRangeRefresher? RangeRefresher { get; }

    /// <summary>The rate-limit engine's table, slots and counters; the compiler, the publisher and the public app share it.</summary>
    public RateLimitRuntime RateLimits { get; }

    /// <summary>The request counters and latency histograms (PLAN §7.4); the compiler, the publisher and the public app share it, and the sampler and the exporters read it.</summary>
    public MetricsRegistry Metrics { get; }

    /// <summary>The access lists' verification line and counters, shared by every compile (the cache lives in the vault).</summary>
    public AccessListRuntime AccessLists { get; }

    /// <summary>The vault's password credentials: the access-list users' passwords, which only the config service and tests set.</summary>
    internal IPasswordCredentials PasswordCredentials { get; }

    /// <summary>
    /// The process's log files: the error log's writer and provider, and the access log's writer, which the access log's
    /// background drain writes into. Both are disposed after the hosts have stopped, which flushes them.
    /// </summary>
    public ServerLogFiles LogFiles { get; }

    /// <summary>The read side of the log files: what the dashboard's log page browses and the API's log endpoints serve.</summary>
    public ILogFileStore LogStore { get; }

    /// <summary>The vault describing the certificates it holds (public facts only); the CA download endpoints read the root from it.</summary>
    public ICertificateCatalog Catalog { get; }

    /// <summary>
    /// The admin app's Data Protection key ring in the vault: the only capability here that is <b>not</b> registered
    /// in any container, so nothing can resolve it from DI. <see cref="HostSetup.ApplySharedServices"/> hands it straight to the
    /// admin's <c>IXmlRepository</c> (its one sanctioned reader). The public app uses an in-memory ephemeral provider, and the
    /// core host gets no protector.
    /// </summary>
    public IDataProtectionKeyStore KeyRing { get; }

    /// <summary>The public listener layout, the TLS handshake selector and the compiler options derived from it.</summary>
    public PublicListenerSetup PublicListeners { get; }

    /// <summary>The config write path, started by the core host (<see cref="ConfigStartup"/>); see <see cref="ConfigStore"/>.</summary>
    public ConfigService Config { get; }

    /// <summary>The certificate inventory's service: shares <see cref="Config"/>'s writer; never calls the vault.</summary>
    public CertificateService CertificateService { get; }

    /// <summary>The certificate use cases that touch the vault: upload, self-signed, local CA, re-issue, delete.</summary>
    public CertificateOperations CertificateOperations { get; }

    /// <summary>Registers the certificates the vault holds that the inventory lost again; run once at startup by <see cref="CertificateRecoveryStartup"/>.</summary>
    public CertificateRecovery CertificateRecovery { get; }

    /// <summary>Completes when the startup recovery (<see cref="CertificateRecoveryStartup"/>) has finished its run.</summary>
    internal CertificateRecoveryCompletion RecoveryCompletion { get; }

    /// <summary>The HTTP-01 answers: the issuance service publishes to it through <see cref="IAcmeChallengeStore"/>, the public gate reads it.</summary>
    public AcmeChallengeStore Challenges { get; }

    public AcmeAccountService AcmeAccounts { get; }

    public DnsProviderService DnsProviders { get; }

    /// <summary>ACME issuance and renewal: request and renew certificates; the scheduler drives it.</summary>
    public AcmeIssuanceService AcmeIssuance { get; }

    /// <summary>The renewal scheduler. A hosted service of the core host only when <c>Paduraksa:Acme:Enabled</c> (see <see cref="CoreHost"/>).</summary>
    public AcmeRenewalScheduler AcmeScheduler { get; }
    /// <summary>The request simulator: what a request would get against the live config, a revision or a draft.</summary>
    public RequestSimulator Simulator { get; }

    /// <summary><c>&lt;data dir&gt;/last-good.json</c>, written after every successful publish.</summary>
    public LastKnownGoodFile LastKnownGood { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Builds every shared singleton. <paramref name="configure"/> may add more instances (tests).</summary>
    /// <remarks><paramref name="configureLogging"/> adds to the one logger factory of the process (tests capture logs with it).</remarks>
    public static SharedServices Create(
        IConfiguration configuration,
        PaduraksaOptions options,
        Action<Builder>? configure = null,
        Action<ILoggingBuilder>? configureLogging = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        Builder builder = new();
        ILoggerFactory? loggerFactory = null;
        try
        {
            // Created first => disposed last.
            loggerFactory = builder.Own(CreateLoggerFactory(configuration, options.Logs, configureLogging));
            var timeProvider = builder.Add(TimeProvider.System);
            // The validated options are the instance consumers get. IOptions/IOptionsMonitor/IOptionsSnapshot and
            // IOptionsFactory are pinned to it too; left alone they'd build their own unvalidated copies from
            // configuration (or unbound defaults).
            builder.Add(options);
            FixedOptions<PaduraksaOptions> fixedOptions = new(options);
            builder.Add<IOptions<PaduraksaOptions>>(fixedOptions);
            builder.Add<IOptionsMonitor<PaduraksaOptions>>(fixedOptions);
            builder.Add<IOptionsSnapshot<PaduraksaOptions>>(fixedOptions);
            builder.Add<IOptionsFactory<PaduraksaOptions>>(fixedOptions);

            // The log files, early among the shared services so they are disposed late: everything the hosts' shutdown logs
            // still reaches the error log before its writer is flushed. The writer itself is what the access log's drain will use, and
            // the store is the read side the dashboard and the API share.
            var logFileReports = loggerFactory.CreateLogger("Paduraksa.Server.Logging");
            var logFiles = builder.Add(new ServerLogFiles(options.Logs, options.DataDirectory, timeProvider, logFileReports));
            loggerFactory.AddProvider(logFiles.ErrorProvider);
            builder.Add<ILogFileStore>(new LogFileStore(logFiles.Directory));

            var health = builder.Add(new HealthState());
            builder.Add(new CertificateRecoveryCompletion());
            var snapshots = builder.Add(new SnapshotStore());
            RoleLoggerFactory coreLoggers = new(loggerFactory, HostRole.Core);
            // The proof-of-work gate's key is opened into this at startup; until then a protected route fails closed.
            var proofOfWork = builder.Add(new ProofOfWorkRuntime(timeProvider, coreLoggers.CreateLogger("Paduraksa.Proxy.ProofOfWork")));
            // The rate-limit table, slots and counters: the compiler assigns pairs slots in this registry, the publisher
            // makes a live snapshot's pairs the live parameters, and every compiled policy takes from this table.
            var rateLimits = builder.Add(RateLimitServiceCollectionExtensions.CreateRuntime(configuration));
            // The request counters and latency histograms: the compiler asks it for each object's slot, the publisher makes a live
            // snapshot's objects the live set, and the access-log completion adds to it for every request.
            var metrics = builder.Add(new MetricsRegistry(timeProvider: timeProvider));
            // The vault first among the services that use it, so it is disposed after them all: the config store loads certificates through
            // it, and the access-list gate asks its verifier for every password check.
            var vault = VaultStore.AddTo(builder, options.DataDirectory, coreLoggers);
            var accessLists = builder.Add(new AccessListRuntime(timeProvider, vault.PasswordVerifier));
            // The forward-auth decision caches, clients and counters: compiled policies hold their provider's state, which this keeps
            // across publishes while the provider's configuration is the same.
            var forwardAuth = builder.Add(new ForwardAuthRuntime(timeProvider, coreLoggers.CreateLogger("Paduraksa.Proxy.ForwardAuth")));
            // Built here so its start time is the process's: the counters it reports begin at zero with them.
            builder.Add(new SecurityOverviewService(snapshots, proofOfWork, rateLimits, timeProvider, accessLists));
            // Before the publisher, so disposed after it: once the publisher has stopped, no publish can go live behind a
            // last-known-good file that has stopped taking offers.
            var lastKnownGood = builder.Add(ConfigStore.CreateLastKnownGood(options.DataDirectory, coreLoggers));
            var publisher = builder.Add(new SnapshotPublisher(snapshots, coreLoggers, timeProvider, rateLimits: rateLimits, forwardAuth: forwardAuth, metrics: metrics));
            var placeholder = builder.Add(PlaceholderServerCertificate.Create(timeProvider));
            var certificates = builder.Add(new PlaceholderCertificates(placeholder));
            AdminInternalListener adminInternal;
            try
            {
                adminInternal = builder.Add(new AdminInternalListener(options.DataDirectory, options.Listen.AdminInternal));
            }
            catch (AdminInternalStartupException ex)
            {
                var adminLog = coreLoggers.CreateLogger("Paduraksa.Admin.Internal");
                AdminInternalLogs.StartupFailed(adminLog, ex);
                throw;
            }

            // Cloudflare's edge ranges: the list the compiler reads at every compile. A stored one (the last refresh) is in force from
            // the first compile, so a restart doesn't fall back to the built-in list; the refresh job (below) keeps it current.
            var edgeRanges = builder.Add(new CloudflareEdgeRangeSource(timeProvider));
            CloudflareRangeFile rangeFile = new(Path.Combine(options.DataDirectory, CloudflareRangeFile.FileName), coreLoggers.CreateLogger<CloudflareRangeFile>());
            if (rangeFile.Load() is { } storedRanges)
            {
                edgeRanges.Replace(storedRanges);
            }

            var originPullCa = LoadOriginPullCa(options.Cloudflare.OriginPullCaFile);
            var listeners = builder.Add(new PublicListenerSetup(options.Listen, snapshots, adminInternal.Enabled, proofOfWork, rateLimits, edgeRanges, originPullCa, accessLists, forwardAuth, metrics));
            // Created after the publisher, so disposed before it (the service publishes with it).
            DevelopmentWarnings.Check(coreLoggers, options);

            // The access log: the public app's hot path fills the ring, the core host's drain service is its
            // one reader, and it writes the lines into access.log through the writer above. Built after the log files, so
            // the writers outlive the drain's last flush (they are disposed after the hosts have stopped).
            builder.Add(new AccessLogRing(options.AccessLog.RingSize));
            var accessLogFormat = builder.Add(options.AccessLog.ToFormatOptions());
            builder.Add<IAccessLogSink>(new AccessLogFileSink(logFiles));
            // The dashboard's live tail is fed by that same drain, after the file; it keeps copies redacted with the file's options.
            var accessLogTail = builder.Add(new AccessLogTail(options.AccessLog.TailSize, accessLogFormat, snapshots));
            builder.Add(new LogTailService(accessLogTail));

            // The HTTP-01 answers: the issuance orchestrator publishes through the interface, the public gate and the
            // request simulator read the class.
            var challenges = builder.Add(new AcmeChallengeStore());
            builder.Add<IAcmeChallengeStore>(challenges);

            // The vault's own catalog is a shared instance too: the CA download endpoints read the root from it (public facts).
            builder.AddOwnedElsewhere(vault.Catalog);
            builder.AddOwnedElsewhere(vault.PasswordCredentials);
            var configStore = ConfigStore.AddTo(
                builder, options.DataDirectory, publisher, lastKnownGood, listeners, certificates, vault, challenges, options.Development.PlaceholderCertificate, options.Admin, timeProvider, proofOfWork, options.Notifications.ToOptions(), coreLoggers);

            // The metrics read side: the sampler turns the counters into rings and finished minutes once a second, the rollup job
            // persists the minutes (hosted services of the core host, below), and the meter, the service and the /metrics scrape read them.
            // After the config store, whose database holds the rollups.
            MetricRollupBacklog rollupQueue = new();
            var sampler = builder.Add(new MetricSampler(metrics, timeProvider, rollupQueue));
            var rollupStore = configStore.MetricRollups;
            builder.Add(new MetricSamplerService(sampler, timeProvider, coreLoggers.CreateLogger<MetricSamplerService>()));
            builder.Add(new MetricRollupJob(rollupQueue, rollupStore, options.Metrics.ToOptions(), timeProvider, coreLoggers.CreateLogger<MetricRollupJob>()));
            var processCounters = builder.Add(ProcessCounters.Create(rateLimits.Stats, proofOfWork.Stats, accessLists.Stats));
            builder.Add(new PaduraksaMeter(metrics, snapshots, processCounters));
            builder.Add(new MetricsService(metrics, snapshots, sampler, rollupStore, processCounters, timeProvider));

            // After the config store, which it asks to compile the live config again when Cloudflare's ranges change: disposed
            // before it. The core host runs it (CoreHost) only when it is enabled.
            builder.Add(new CloudflareRangeReport(edgeRanges));
            if (options.Cloudflare.RangeRefresh.Enabled)
            {
                var rangeClient = builder.Own(CreateRangeClient());
                builder.Add(new CloudflareRangeRefresher(
                    edgeRanges,
                    rangeFile,
                    rangeClient,
                    options.Cloudflare.RangeRefresh.ToOptions(),
                    snapshots,
                    configStore.Config.RepublishLiveAsync,
                    timeProvider,
                    coreLoggers.CreateLogger<CloudflareRangeRefresher>()));
            }

            // After the config store and the vault: disposed before them (the services call both), and the scheduler is a hosted service of the core host.
            IssuanceStore.AddTo(builder, options, configStore, vault, challenges, health, timeProvider, coreLoggers);

            // The admin API stores DNS credentials and ACME binding keys through it (write-only: there is no get).
            builder.AddOwnedElsewhere(vault.Secrets);

            // The admin listener's certificate: issued by the local CA, served through the vault and kept
            // current by a background service of the core host (registered in CoreHost). The CA root it publishes feeds the
            // download endpoints.
            var caRoot = builder.Add(new CaRootSource());
            builder.Add(new AdminCertificateService(
                certificates, vault.ServerCertificates, vault.Catalog, caRoot, configStore.Operations, configStore.Config, configStore.Setup, options, timeProvider, coreLoggers));
            // The proof-of-work HMAC key: ensured at its reserved ID and opened into the runtime before the public
            // listener binds (a core-host start step), so the first protected request has a key or fails closed.
            builder.Add(new ProofOfWorkKeyStartup(vault.Secrets, vault.MacKeys, proofOfWork, coreLoggers));
            configure?.Invoke(builder);
            builder.Freeze();
            return new SharedServices(builder, loggerFactory, vault.KeyRing);
        }
        catch
        {
            // The logger is created before the factory is disposed (last, in reverse creation order); the console
            // provider still writes synchronously after disposal, so a cleanup failure is reported, not lost.
            ILogger logger = loggerFactory is null ? NullLogger.Instance : loggerFactory.CreateLogger<SharedServices>();
            FailureCleanup.Run(() => DisposeAllAsync(builder.Owned).AsTask().GetAwaiter().GetResult(), logger, "disposing shared services");
            throw;
        }
    }

    /// <summary>
    /// Registers the shared instances into one host's container, replacing any default registration of the
    /// same service type, plus a per-host <see cref="ILoggerFactory"/> view tagging entries with the host's role.
    /// This is the <b>only</b> way shared services enter a container.
    /// </summary>
    public void RegisterInto(IServiceCollection services, HostRole role)
    {
        ArgumentNullException.ThrowIfNull(services);
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        foreach (var (serviceType, instance) in _registrations)
        {
            services.RemoveAll(serviceType);
            services.AddSingleton(serviceType, instance);
        }

        // Every host has its own LoggerFactory registration by default; swap it for a view of the shared one.
        services.RemoveAll<ILoggerFactory>();
        services.AddSingleton<ILoggerFactory>(new RoleLoggerFactory(LoggerFactory, role, StartupLogFilter));
    }

    /// <summary>Disposes every shared instance exactly once (idempotent, thread-safe).</summary>
    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _disposed, 1) != 0 ? ValueTask.CompletedTask : DisposeAllAsync(_owned);

    /// <summary>The origin-pull CA the operator supplies in place of the shipped one; null: the shipped one. A file that can't be
    /// read as certificates stops the start with the file named, like the ACME trust file.</summary>
    private static X509Certificate2Collection? LoadOriginPullCa(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        try
        {
            return OriginPullCa.FromPem(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            throw new OptionsValidationException(
                PaduraksaOptions.SectionName,
                typeof(PaduraksaOptions),
                [$"{PaduraksaOptions.SectionName}:Cloudflare:OriginPullCaFile '{file}' can't be read as certificates: {ex.Message}"]);
        }
    }

    /// <summary>The refresh job's client: no redirects (a list is fetched from the URL asked for, nowhere else) and its own timeouts.</summary>
    private static HttpClient CreateRangeClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        // Each request has its own timeout (CloudflareRangeRefreshOptions.RequestTimeout); this one only ends a stuck handler.
        Timeout = TimeSpan.FromMinutes(1),
    };

    private T Get<T>()
        where T : class =>
        (T)_registrations.First(static r => r.Key == typeof(T)).Value;

    private static ILoggerFactory CreateLoggerFactory(
        IConfiguration configuration,
        PaduraksaLogOptions logs,
        Action<ILoggingBuilder>? configureLogging) =>
        Microsoft.Extensions.Logging.LoggerFactory.Create(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddSimpleConsole(static console =>
            {
                console.SingleLine = true;
                console.IncludeScopes = true;
                console.UseUtcTimestamp = true;
                console.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z' ";
                console.ColorBehavior = LoggerColorBehavior.Default;
            });
            // The error log's own minimum level: a rule for this provider wins over the configuration's rules, which are
            // about the console. So `Logging:LogLevel:Default=Error` quiets the console without emptying error.log.
            var minimum = logs.ErrorLevel;
            logging.AddFilter<ErrorLogProvider>((_, level) => level >= minimum);
            configureLogging?.Invoke(logging);
        });

    private static async ValueTask DisposeAllAsync(IReadOnlyList<object> owned)
    {
        List<Exception>? errors = null;
        for (var i = owned.Count - 1; i >= 0; i--)
        {
            try
            {
                switch (owned[i])
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
#pragma warning disable CA1031 // Keep disposing the rest; failures are rethrown together below.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                (errors ??= []).Add(ex);
            }
        }

        if (errors is not null)
        {
            throw new AggregateException("Disposing shared services failed.", errors);
        }
    }

    /// <summary>Collects shared instances. Instances only: there is deliberately no factory overload.</summary>
    internal sealed class Builder
    {
        private bool _frozen;

        internal List<KeyValuePair<Type, object>> Registrations { get; } = [];

        internal List<object> Owned { get; } = [];

        /// <summary>
        /// Adds <paramref name="instance"/> as the one <typeparamref name="TService"/> of the process.
        /// If it's disposable, <see cref="SharedServices"/> disposes it after every host has stopped.
        /// </summary>
        public TService Add<TService>(TService instance)
            where TService : class =>
            Own(AddOwnedElsewhere(instance));

        /// <summary>
        /// Registers <paramref name="instance"/> like <see cref="Add{TService}"/>, but without taking ownership: whatever
        /// created it disposes it (a container this builder owns, say), so it is disposed exactly once.
        /// </summary>
        public TService AddOwnedElsewhere<TService>(TService instance)
            where TService : class
        {
            ArgumentNullException.ThrowIfNull(instance);
            if (_frozen)
            {
                throw new InvalidOperationException("Shared services are frozen once the hosts are built.");
            }

            if (Registrations.Exists(r => r.Key == typeof(TService)))
            {
                throw new InvalidOperationException($"A shared {typeof(TService)} is already registered.");
            }

            Registrations.Add(new(typeof(TService), instance));
            return instance;
        }

        /// <summary>
        /// Replaces the one shared <typeparamref name="TService"/> an earlier step registered (none: adds it), keeping that
        /// registration's ownership: the replacement is disposed exactly once, before or after its neighbours as the
        /// replaced one was. This is how a test swaps in a capturing sink, or a caller a default.
        /// </summary>
        /// <exception cref="InvalidOperationException">No <typeparamref name="TService"/> was registered.</exception>
        public TService Replace<TService>(TService instance)
            where TService : class
        {
            ArgumentNullException.ThrowIfNull(instance);
            if (_frozen)
            {
                throw new InvalidOperationException("Shared services are frozen once the hosts are built.");
            }

            var index = Registrations.FindIndex(r => r.Key == typeof(TService));
            if (index < 0)
            {
                throw new InvalidOperationException($"No shared {typeof(TService)} is registered to replace.");
            }

            var wasOwned = Owned.Exists(o => ReferenceEquals(o, Registrations[index].Value));
            var previous = Registrations[index].Value;
            Registrations[index] = new(typeof(TService), instance);
            if (wasOwned)
            {
                Owned[Owned.FindIndex(o => ReferenceEquals(o, previous))] = instance;
            }

            return instance;
        }

        /// <summary>Takes ownership (disposal) without registering the instance as a service.</summary>
        internal T Own<T>(T instance)
            where T : class
        {
            if ((instance is IDisposable or IAsyncDisposable) && !Owned.Exists(o => ReferenceEquals(o, instance)))
            {
                Owned.Add(instance);
            }

            return instance;
        }

        internal void Freeze() => _frozen = true;
    }
}
