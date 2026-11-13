using System.Net.Security;
using System.Security.Authentication;

using Microsoft.AspNetCore.Server.Kestrel.Https;
using Paduraksa.Admin.Certificates;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Placeholders;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// The admin listener's certificate: the process ensures the local CA root exists, ensures the admin leaf
/// is issued by it (<see cref="AdminCertificate.Id"/>) and current, then loads the serving context from the vault and hands
/// it to Kestrel. A once-per-process <see cref="BackgroundService"/> of the core host: its start is awaited (bounded by
/// <see cref="AdminOptions.CertificateStartupTimeout"/>), so the admin port serves the local-CA certificate from its first
/// request, and a daily loop re-checks it.
/// </summary>
/// <remarks>
/// <b>Serving.</b> The listener reads <see cref="ServingOptions"/> per handshake, so replacing it swaps the certificate for
/// the next connection. The options start over the in-memory placeholder and are only replaced by a context the vault hands
/// out (the sanctioned egress, like the public listener); the private key never leaves the vault's process boundary.
///
/// <b>Never a lock-out.</b> Whatever the inventory step does, the run still serves the certificate the vault holds and falls
/// back to the placeholder only when the vault has nothing usable. A failure must not discard a valid certificate: the admin
/// sends HSTS for non-loopback names, so a browser that trusted the root has pinned the name and the self-signed placeholder
/// would be a hard error with no click-through. Issuance is skipped while the config store refuses changes (a degraded
/// start), because a due re-issue would otherwise replace the vault's key and then fail to record it, every day.
/// <see cref="EnsureAsync"/> serialises on a gate, so the startup run, the daily run and a test can't issue two certificates
/// at once.
/// </remarks>
internal sealed partial class AdminCertificateService : BackgroundService
{
    /// <summary>How often the certificate is looked at after the startup run; a leaf's validity is months, so a day is plenty.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly CertificateOperations _operations;
    private readonly ConfigService _config;
    private readonly IServerCertificateSource _source;
    private readonly ICertificateCatalog _catalog;
    private readonly CaRootSource _caRoot;
    private readonly SetupService _setup;
    private readonly PaduraksaOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Cancels whatever run is in flight; cancelled by <see cref="StopAsync"/> before the hosts are disposed.</summary>
    private readonly CancellationTokenSource _stop = new();

    // Read on every handshake (volatile) and replaced whole, never mutated.
    private volatile SslServerAuthenticationOptions _serving;
    private SslStreamCertificateContext? _current;

    // The run in flight (the startup one, or a later daily one), for StopAsync to wait out; the token it runs under is
    // linked to the host's start token and to _stop, and is owned here so it outlives the start call.
    private CancellationTokenSource? _run;
    private Task? _running;
    private int _runs;

    public AdminCertificateService(
        PlaceholderCertificates placeholders,
        IServerCertificateSource source,
        ICertificateCatalog catalog,
        CaRootSource caRoot,
        CertificateOperations operations,
        ConfigService config,
        SetupService setup,
        PaduraksaOptions options,
        TimeProvider time,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(placeholders);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(caRoot);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _source = source;
        _catalog = catalog;
        _caRoot = caRoot;
        _operations = operations;
        _config = config;
        _setup = setup;
        _options = options;
        _time = time;
        _logger = loggerFactory.CreateLogger<AdminCertificateService>();

        // Until the vault's certificate is ready the admin port answers with the in-memory placeholder, so it is always up.
        _serving = CreateOptions(placeholders.Placeholder.Context);

        // A rotation issues the admin certificate again from the new root: serve it now, not at the next daily run.
        _operations.AdminCertificateReissued += OnAdminCertificateReissued;
        _operations.DesiredAdminNames = DesiredNames;
    }

    private void OnAdminCertificateReissued() => _ = ServeAfterRotationAsync();

    private async Task ServeAfterRotationAsync()
    {
        try
        {
            await EnsureAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Stopping.
        }
    }

    /// <summary>The TLS options the admin listener uses right now; never null. Read once per handshake.</summary>
    public SslServerAuthenticationOptions ServingOptions => _serving;

    /// <summary>
    /// A test seam: awaited at the start of the <b>first</b> run, so the integration tests can exercise a run that outlives
    /// <see cref="AdminOptions.CertificateStartupTimeout"/>. Null in production.
    /// </summary>
    internal Func<CancellationToken, Task>? FirstRunDelayForTests { get; set; }

    /// <summary>Per-handshake certificate selection for <c>ListenOptions.UseHttps</c> (the vault's context, or the fallback).</summary>
    public TlsHandshakeCallbackOptions CreateTlsOptions() => new()
    {
        OnConnection = static context => ValueTask.FromResult(((AdminCertificateService)context.State!).ServingOptions),
        OnConnectionState = this,
    };

    /// <summary>
    /// The startup run, awaited by the core host so the certificate is ready before the web apps bind - but only up to
    /// <see cref="AdminOptions.CertificateStartupTimeout"/>: a slow vault must not hold the public proxy off 443. On timeout
    /// the run continues in the background under a token this service owns (so it can be cancelled and waited for on stop)
    /// and the admin port serves what the vault already has meanwhile.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var ensure = EnsureAsync(_run.Token);
        _running = ensure;

        var timeout = _options.Admin.CertificateStartupTimeout;
        using CancellationTokenSource timer = new();
        var elapsed = Task.Delay(timeout, _time, timer.Token);
        var finished = await Task.WhenAny(ensure, elapsed).ConfigureAwait(false);
        await timer.CancelAsync().ConfigureAwait(false); // the timer is not needed either way

        if (finished == ensure || _run.IsCancellationRequested)
        {
            // The run finished, or the start was cancelled while waiting: its own cancellation surfaces here.
            await ensure.ConfigureAwait(false);
        }
        else
        {
            LogStartupTimedOut(_logger, timeout);
            _ = RunQuietlyAsync(ensure);
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops whatever run is in flight before the hosts stop: the vault and the config store are disposed right after, so a
    /// run that outlived <see cref="StartAsync"/> must not still be reading them. Bounded by <paramref name="cancellationToken"/>
    /// (the shutdown budget): past it the run is left cancelled and the stop goes on.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_running is { } running)
        {
            try
            {
                await running.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Cancelled (by _stop or by the budget): nothing else to wait for.
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The daily renewal check, in the background (the host's start has already run once).</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runToken = _run?.Token ?? stoppingToken;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                _running = EnsureAsync(runToken);
                await _running.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Ensures the local CA root and the admin certificate, then loads and serves what the vault holds. Never throws for a
    /// failure (except cancellation): it is logged and what is serving stays. Serialised, so two callers can't race.
    /// </summary>
    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // The host disposed the service while this run was finishing; nothing left to release.
            }
        }
    }

    private async Task EnsureCoreAsync(CancellationToken cancellationToken)
    {
        // Serve what the vault already holds *before* any inventory work. The startup run is bounded, and an admin port that
        // shows the placeholder while a slow inventory step runs is the HSTS hard-fail this service exists to avoid; the vault
        // caches the context, so the second call after the step costs nothing.
        await ServeFromVaultAsync(cancellationToken).ConfigureAwait(false);

        if (Interlocked.Increment(ref _runs) == 1 && FirstRunDelayForTests is { } delay)
        {
            await delay(cancellationToken).ConfigureAwait(false);
        }

        // Issuing or re-issuing needs a store that accepts changes (a degraded start refuses them): a due re-issue would
        // otherwise replace the vault's key and then fail to record it, every day.
        if (!_config.Status.AcceptsCommands)
        {
            LogIssuanceSkipped(_logger);
        }
        else
        {
            try
            {
                var leaf = await _operations.EnsureAdminCertificateAsync(DesiredNames(), CommandOrigin.System, cancellationToken).ConfigureAwait(false);
                if (leaf.Status is not (CertificateOperationStatus.Completed or CertificateOperationStatus.Unchanged) || leaf.Certificate is null)
                {
                    throw new InvalidOperationException($"the inventory refused the admin certificate: {Problems(leaf)}");
                }

                if (leaf.Status == CertificateOperationStatus.Completed)
                {
                    LogIssued(_logger, leaf.Certificate.Id, leaf.Certificate.Sha256Fingerprint, leaf.Certificate.NotAfter);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(_logger, ex);
            }
        }

        await ServeFromVaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Serves the certificate the vault holds, whatever the inventory step did, and publishes the root for the download
    /// endpoints. The placeholder stays only when the vault has nothing servable.
    /// </summary>
    private async Task ServeFromVaultAsync(CancellationToken cancellationToken)
    {
        try
        {
            var held = await _catalog.ListAsync(cancellationToken).ConfigureAwait(false);
            var root = held.FirstOrDefault(static certificate => certificate.IsLocalCaRoot);
            _caRoot.Publish(root is null ? null : CaRoot.Of(root));

            var leaf = held.FirstOrDefault(certificate => certificate.Id == AdminCertificate.Id);
            if (leaf is null)
            {
                return;
            }

            if (!AdminCertificatePolicy.IsServable(leaf.Info, _time.GetUtcNow()))
            {
                LogNotServable(_logger, AdminCertificate.Id, leaf.Info.NotBefore, leaf.Info.NotAfter);
                return;
            }

            var context = await _source.LoadForServingAsync(AdminCertificate.Id, cancellationToken).ConfigureAwait(false);
            if (context is null)
            {
                throw new InvalidOperationException("the vault holds no usable key and chain for the admin certificate");
            }

            if (ReferenceEquals(context, _current))
            {
                return;
            }

            _current = context;
            _serving = CreateOptions(context);
            await AnnounceTrustAsync(leaf.Info, root?.Info, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(_logger, ex);
        }
    }

    /// <summary>While setup is incomplete, says where the admin UI is and which root to trust, once per served certificate.</summary>
    private async Task AnnounceTrustAsync(CertificateInfo leaf, CertificateInfo? root, CancellationToken cancellationToken)
    {
        if (root is null)
        {
            return;
        }

        bool setupComplete;
        try
        {
            setupComplete = await _setup.IsCompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The wizard's own status endpoint reports this; the startup log just skips the "trust this" line.
            LogSetupUnknown(_logger, ex);
            return;
        }

        if (!setupComplete && _logger.IsEnabled(LogLevel.Information))
        {
            var urls = string.Join(", ", AdminCertificateNames.Urls(leaf.SubjectAlternativeNames, _options.Listen.Admin));
            LogTrust(_logger, urls, root.Sha256Fingerprint);
        }
    }

    private IReadOnlyList<string> DesiredNames() =>
        AdminCertificateNames.Compose(_options.Admin.CertificateNames, Environment.MachineName, _options.Listen.AdminBind);

    private static SslServerAuthenticationOptions CreateOptions(SslStreamCertificateContext context) => new()
    {
        ServerCertificateContext = context,
        // Set explicitly: Kestrel writes its own ALPN into a returned options object whose list is null, which would be wrong
        // for a shared instance (the public listener's CertificateTableBuilder makes the same choice).
        ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        AllowRenegotiation = false,
    };

    private static string Problems(CertificateOperationResult result) =>
        result.Errors.Count == 0 ? result.Status.ToString() : string.Join("; ", result.Errors.Select(static error => error.Message));

    private static async Task RunQuietlyAsync(Task ensure)
    {
        try
        {
            await ensure.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The process is stopping; the run's failures are logged where they happen.
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();
        _operations.AdminCertificateReissued -= OnAdminCertificateReissued;
        _operations.DesiredAdminNames = null;
        _stop.Dispose();
        if (_running is null or { IsCompleted: true })
        {
            // Still running only when the stop budget ran out; its token must stay usable until it ends.
            _run?.Dispose();
        }

        _gate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The admin certificate was issued by the local CA ({Certificate}, SHA-256 {Fingerprint}, until {NotAfter:u}).")]
    private static partial void LogIssued(ILogger logger, CertificateId certificate, string fingerprint, DateTimeOffset notAfter);

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin is served at {Urls} with a certificate issued by the internal CA. Trust its root once (SHA-256 {Fingerprint}); download it at /api/v1/ca/root.crt or /api/v1/ca/root.pem.")]
    private static partial void LogTrust(ILogger logger, string urls, string fingerprint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The config store refuses changes (a degraded start), so the admin certificate was not checked or issued; the admin port serves what the vault holds.")]
    private static partial void LogIssuanceSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The admin certificate was not ready within {Timeout}; the listeners start now and the admin port serves what it has until it is (the vault's existing certificate, else the placeholder).")]
    private static partial void LogStartupTimedOut(ILogger logger, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The vault's admin certificate {Certificate} can't be served yet (it is valid from {NotBefore:u} to {NotAfter:u}); the admin port keeps what it has.")]
    private static partial void LogNotServable(ILogger logger, CertificateId certificate, DateTimeOffset notBefore, DateTimeOffset notAfter);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't prepare the admin certificate; the admin port keeps serving what it has (the vault's existing certificate, else the placeholder).")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read whether first-run setup is complete, so the admin URL and root fingerprint were not logged.")]
    private static partial void LogSetupUnknown(ILogger logger, Exception exception);
}
