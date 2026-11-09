using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Paduraksa.Admin.Certificates;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Issuance;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Application.Logging;
using Paduraksa.Application.Simulation;
using Paduraksa.Core.Acme;
using Paduraksa.Core.Hosting;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Logging;
using Paduraksa.Server.Placeholders;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The process-lifetime singletons shared by the core host, the public app and the admin app (PLAN §3).
/// <para>
/// Rules this type enforces:
/// <list type="bullet">
/// <item>Every shared service is <b>constructed here, exactly once</b>. <see cref="Builder.Add{TService}"/>
/// only accepts ready-made instances: there's no factory overload, so no container can ever build its own copy.</item>
/// <item><see cref="RegisterInto"/> registers those <b>same instances</b> into a container. Microsoft.Extensions.DependencyInjection
/// never disposes instances it didn't create, so disposing the three containers leaves them alone.</item>
/// <item><see cref="SharedServices"/> owns them: <see cref="DisposeAsync"/> disposes each one exactly once, in reverse
/// creation order, and only after every host has stopped (<see cref="PaduraksaHost"/> guarantees the ordering).
/// The logger factory is created first, so it's disposed last and shutdown logs still get flushed.</item>
/// </list>
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
        LogFiles = Get<ServerLogFiles>();
        LogStore = Get<ILogFileStore>();
        Catalog = Get<ICertificateCatalog>();
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
        LastKnownGood = Get<LastKnownGoodFile>();
        Users = Get<UserService>();
        Sessions = Get<SessionService>();
        TwoFactor = Get<TwoFactorService>();
        Login = Get<LoginService>();
        ApiTokens = Get<ApiTokenService>();
        Setup = Get<SetupService>();
        Permissions = Get<IPermissionEvaluator>();
    }

    /// <summary>User management (PLAN §10.1), the first of the identity services below that the admin app and the REST API use.</summary>
    public UserService Users { get; }

    /// <summary>Admin sessions: cookie validation, listing, revoking.</summary>
    public SessionService Sessions { get; }

    /// <summary>TOTP enrolment and recovery codes.</summary>
    public TwoFactorService TwoFactor { get; }

    /// <summary>Sign-in with a password and, for users with one, a second factor.</summary>
    public LoginService Login { get; }

    /// <summary>API tokens: issue, list, revoke, verify.</summary>
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
    /// The admin listener's certificate (PLAN §3, §10.1): the local-CA leaf the admin app serves, kept current by a background
    /// service of the core host. Its <see cref="AdminCertificateService.ServingOptions"/> are the listener's TLS options.
    /// </summary>
    public AdminCertificateService AdminCertificate { get; }

    /// <summary>
    /// The admin app's internal listener (PLAN §3.1): a Unix socket (0600, in a 0700 directory) or the loopback fallback, through
    /// which the public proxy reaches the admin. Exists independent of the config, so creating an admin host needs no restart.
    /// </summary>
    public AdminInternalListener AdminInternal { get; }

    /// <summary>
    /// The process's log files (PLAN §7.4): the error log's writer and provider, and the access log's writer, which the access log's
    /// background drain writes into. Both are disposed after the hosts have stopped, which flushes them.
    /// </summary>
    public ServerLogFiles LogFiles { get; }

    /// <summary>The read side of the log files: what the dashboard's log page browses and the API's log endpoints serve.</summary>
    public ILogFileStore LogStore { get; }

    /// <summary>The vault describing the certificates it holds (public facts only); the CA download endpoints read the root from it.</summary>
    public ICertificateCatalog Catalog { get; }

    /// <summary>
    /// The admin app's Data Protection key ring in the vault (PLAN §8.1): the only capability here that is <b>not</b> registered
    /// in any container, so nothing can resolve it from DI. <see cref="HostSetup.ApplySharedServices"/> hands it straight to the
    /// admin's <c>IXmlRepository</c> (its one sanctioned reader). The public app uses an in-memory ephemeral provider, and the
    /// core host gets no protector.
    /// </summary>
    public IDataProtectionKeyStore KeyRing { get; }

    /// <summary>The public listener layout, the TLS handshake selector and the compiler options derived from it.</summary>
    public PublicListenerSetup PublicListeners { get; }

    /// <summary>The config write path, started by the core host (<see cref="ConfigStartup"/>); see <see cref="ConfigStore"/>.</summary>
    public ConfigService Config { get; }

    /// <summary>The certificate inventory's service (PLAN §9.2): shares <see cref="Config"/>'s writer; never calls the vault.</summary>
    public CertificateService CertificateService { get; }

    /// <summary>The certificate use cases that touch the vault (PLAN §9): upload, self-signed, local CA, re-issue, delete.</summary>
    public CertificateOperations CertificateOperations { get; }

    /// <summary>Registers the certificates the vault holds that the inventory lost again (PLAN §9.2); run once at startup by <see cref="CertificateRecoveryStartup"/>.</summary>
    public CertificateRecovery CertificateRecovery { get; }

    /// <summary>Completes when the startup recovery (<see cref="CertificateRecoveryStartup"/>) has finished its run.</summary>
    internal CertificateRecoveryCompletion RecoveryCompletion { get; }

    /// <summary>The HTTP-01 answers: the issuance service publishes to it through <see cref="IAcmeChallengeStore"/>, the public gate reads it.</summary>
    public AcmeChallengeStore Challenges { get; }

    /// <summary>ACME accounts (PLAN §9.3); built by <see cref="IssuanceStore"/> over the vault's account keys.</summary>
    public AcmeAccountService AcmeAccounts { get; }

    /// <summary>DNS providers for DNS-01 (PLAN §9.3).</summary>
    public DnsProviderService DnsProviders { get; }

    /// <summary>ACME issuance and renewal (PLAN §9.3): request and renew certificates; the scheduler drives it.</summary>
    public AcmeIssuanceService AcmeIssuance { get; }

    /// <summary>The renewal scheduler. A hosted service of the core host only when <c>Paduraksa:Acme:Enabled</c> (see <see cref="CoreHost"/>).</summary>
    public AcmeRenewalScheduler AcmeScheduler { get; }
    /// <summary>The request simulator (PLAN §7.7): what a request would get against the live config, a revision or a draft.</summary>
    public RequestSimulator Simulator { get; }

    /// <summary><c>&lt;data dir&gt;/last-good.json</c>, written after every successful publish.</summary>
    public LastKnownGoodFile LastKnownGood { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Builds every shared singleton. <paramref name="configure"/> may add more instances (tests, later milestones).</summary>
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
            TimeProvider timeProvider = builder.Add(TimeProvider.System);
            // The validated options are the instance consumers get. IOptions/IOptionsMonitor/IOptionsSnapshot and
            // IOptionsFactory are pinned to it too; left alone they'd build their own unvalidated copies from
            // configuration (or unbound defaults).
            builder.Add(options);
            FixedOptions<PaduraksaOptions> fixedOptions = new(options);
            builder.Add<IOptions<PaduraksaOptions>>(fixedOptions);
            builder.Add<IOptionsMonitor<PaduraksaOptions>>(fixedOptions);
            builder.Add<IOptionsSnapshot<PaduraksaOptions>>(fixedOptions);
            builder.Add<IOptionsFactory<PaduraksaOptions>>(fixedOptions);

            // The log files (PLAN §7.4), early among the shared services so they are disposed late: everything the hosts' shutdown logs
            // still reaches the error log before its writer is flushed. The writer itself is what the access log's drain will use, and
            // the store is the read side the dashboard and the API share.
            ILogger logFileReports = loggerFactory.CreateLogger("Paduraksa.Server.Logging");
            ServerLogFiles logFiles = builder.Add(new ServerLogFiles(options.Logs, options.DataDirectory, timeProvider, logFileReports));
            loggerFactory.AddProvider(logFiles.ErrorProvider);
            builder.Add<ILogFileStore>(new LogFileStore(logFiles.Directory));

            HealthState health = builder.Add(new HealthState());
            builder.Add(new CertificateRecoveryCompletion());
            SnapshotStore snapshots = builder.Add(new SnapshotStore());
            RoleLoggerFactory coreLoggers = new(loggerFactory, HostRole.Core);
            // Before the publisher, so disposed after it: once the publisher has stopped, no publish can go live behind a
            // last-known-good file that has stopped taking offers.
            LastKnownGoodFile lastKnownGood = builder.Add(ConfigStore.CreateLastKnownGood(options.DataDirectory, coreLoggers));
            SnapshotPublisher publisher = builder.Add(new SnapshotPublisher(snapshots, coreLoggers, timeProvider));
            PlaceholderServerCertificate placeholder = builder.Add(PlaceholderServerCertificate.Create(timeProvider));
            PlaceholderCertificates certificates = builder.Add(new PlaceholderCertificates(placeholder));
            AdminInternalListener adminInternal;
            try
            {
                adminInternal = builder.Add(new AdminInternalListener(options.DataDirectory, options.Listen.AdminInternal));
            }
            catch (AdminInternalStartupException ex)
            {
                ILogger adminLog = coreLoggers.CreateLogger("Paduraksa.Admin.Internal");
                AdminInternalLogs.StartupFailed(adminLog, ex);
                throw;
            }

            PublicListenerSetup listeners = builder.Add(new PublicListenerSetup(options.Listen, snapshots, adminInternal.Enabled));
            // Created after the publisher, so disposed before it (the service publishes with it).
            DevelopmentWarnings.Check(coreLoggers, options);

            // The access log (PLAN §7.4): the public app's hot path fills the ring, the core host's drain service is its
            // one reader, and it writes the lines into access.log through the writer above. Built after the log files, so
            // the writers outlive the drain's last flush (they are disposed after the hosts have stopped).
            builder.Add(new AccessLogRing(options.AccessLog.RingSize));
            builder.Add(options.AccessLog.ToFormatOptions());
            builder.Add<IAccessLogSink>(new AccessLogFileSink(logFiles));

            // The HTTP-01 answers: the issuance orchestrator publishes through the interface, the public gate and the
            // request simulator read the class.
            AcmeChallengeStore challenges = builder.Add(new AcmeChallengeStore());
            builder.Add<IAcmeChallengeStore>(challenges);

            // The vault before the config store, so it is disposed after it (the store loads certificates through it).
            VaultCapabilities vault = VaultStore.AddTo(builder, options.DataDirectory, coreLoggers);
            // The vault's own catalog is a shared instance too: the CA download endpoints read the root from it (public facts).
            builder.AddOwnedElsewhere(vault.Catalog);
            ConfigStoreServices configStore = ConfigStore.AddTo(
                builder, options.DataDirectory, publisher, lastKnownGood, listeners, certificates, vault, challenges, options.Development.PlaceholderCertificate, options.Admin, timeProvider, coreLoggers);

            // After the config store and the vault: disposed before them (the services call both), and the scheduler is a hosted service of the core host.
            IssuanceStore.AddTo(builder, options, configStore, vault, challenges, health, timeProvider, coreLoggers);

            // The admin API stores DNS credentials and ACME binding keys through it (write-only: there is no get).
            builder.AddOwnedElsewhere(vault.Secrets);

            // The admin listener's certificate (PLAN §3, §10.1): issued by the local CA, served through the vault and kept
            // current by a background service of the core host (registered in CoreHost). The CA root it publishes feeds the
            // download endpoints.
            CaRootSource caRoot = builder.Add(new CaRootSource());
            builder.Add(new AdminCertificateService(
                certificates, vault.ServerCertificates, vault.Catalog, caRoot, configStore.Operations, configStore.Config, configStore.Setup, options, timeProvider, coreLoggers));
            // TODO(M9): MetricsRegistry.
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

        foreach ((Type serviceType, object instance) in _registrations)
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
            // The error log's own minimum level (PLAN §7.4): a rule for this provider wins over the configuration's rules, which are
            // about the console. So `Logging:LogLevel:Default=Error` quiets the console without emptying error.log.
            LogLevel minimum = logs.ErrorLevel;
            logging.AddFilter<ErrorLogProvider>((_, level) => level >= minimum);
            configureLogging?.Invoke(logging);
        });

    private static async ValueTask DisposeAllAsync(IReadOnlyList<object> owned)
    {
        List<Exception>? errors = null;
        for (int i = owned.Count - 1; i >= 0; i--)
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
        /// replaced one was. This is how a test swaps in a capturing sink, and how a later milestone upgrades a default.
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

            int index = Registrations.FindIndex(r => r.Key == typeof(TService));
            if (index < 0)
            {
                throw new InvalidOperationException($"No shared {typeof(TService)} is registered to replace.");
            }

            bool wasOwned = Owned.Exists(o => ReferenceEquals(o, Registrations[index].Value));
            object? previous = Registrations[index].Value;
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
