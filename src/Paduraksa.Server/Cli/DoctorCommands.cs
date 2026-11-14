using System.CommandLine;
using System.Net;
using System.Security.Authentication;
using Microsoft.Extensions.Options;
using Paduraksa.Server.Cli.Doctor;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;

namespace Paduraksa.Server.Cli;

/// <summary>
/// <c>paduraksa doctor</c>: a read-only check of the host (<see cref="DoctorRunner.DefaultChecks"/>), and <c>--liveness</c>, the one check the
/// container's <c>HEALTHCHECK</c> can run without curl. Neither changes anything, so both are safe beside a running server.
/// </summary>
/// <remarks>
/// <b>Which listener.</b> The opt-in loopback plain-HTTP listener when it is enabled, else HTTPS to the admin port: a
/// wildcard bind becomes loopback, a specific one is used as given, because a listener bound to one address does not listen
/// on the others. The full doctor probes the same candidates.
///
/// <b>Certificate validation is off for these loopback calls.</b> The admin certificate is the local CA's (or the placeholder),
/// which this short-lived process has not loaded: trusting it would mean starting the vault, reading the master key and the
/// database, to ask one loopback question. The connection never leaves the machine, and the handler follows no redirects and
/// uses no proxy, so it cannot be pointed elsewhere.
///
/// <b>Exit codes.</b> The full doctor exits 0 when nothing failed (warnings pass) and 1 when a check failed, an invalid configuration
/// included: it reports that as a failed check rather than exiting 3, so the rest of the report still prints.
/// </remarks>
internal static class DoctorCommands
{
    /// <summary>The whole probe's budget per candidate: well under Docker's 30 s interval, so a hung admin cannot pile probes up.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static Command Build()
    {
        Command doctor = new("doctor", "Check the host: data directory, master key, databases, ports, HTTP/3 and the clock. Changes nothing, so it is safe while the server runs. Exit 0 unless a check failed.");
        Option<bool> liveness = new("--liveness")
        {
            Description = "Only ask the admin listener's /healthz over loopback: exit 0 when it answers 200, 1 otherwise. Used by the image's HEALTHCHECK.",
        };
        Option<bool> json = new("--json") { Description = "Print the report as JSON (version, passed, summary, checks) instead of lines." };
        Option<bool> checkClock = new("--check-clock") { Description = "Also compare the system clock with the Date header of a public server (www.cloudflare.com). Off by default: doctor makes no outward request unless asked." };
        Option<string> tokenFile = new("--token-file")
        {
            Description = $"A file whose first line is an API token (default: the ${ConfigCommands.TokenVariable} variable), so the running server's version can be compared with this binary's.",
        };
        doctor.Options.Add(liveness);
        doctor.Options.Add(json);
        doctor.Options.Add(checkClock);
        doctor.Options.Add(tokenFile);
        doctor.SetAction((parseResult, cancellationToken) => parseResult.GetValue(liveness)
            ? LivenessAsync(null, parseResult.InvocationConfiguration.Output, parseResult.InvocationConfiguration.Error, cancellationToken)
            : DiagnoseAsync(
                null,
                new DoctorRequest(parseResult.GetValue(json), parseResult.GetValue(checkClock), parseResult.GetValue(tokenFile)),
                parseResult.InvocationConfiguration.Output,
                parseResult.InvocationConfiguration.Error,
                cancellationToken));
        return doctor;
    }

    /// <summary>What <c>paduraksa doctor</c> was asked.</summary>
    internal sealed record DoctorRequest(bool Json = false, bool CheckClock = false, string? TokenFile = null);

    /// <summary>
    /// The full doctor. <paramref name="configure"/> replaces seams (a test's fake file system, port or clock). Returns 0, or 1 when a check failed.
    /// </summary>
    internal static async Task<int> DiagnoseAsync(
        PaduraksaHostOptions? hostOptions,
        DoctorRequest request,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        Func<DoctorContext, DoctorContext>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        PaduraksaOptions? options = null;
        IReadOnlyList<string> problems = [];
        try
        {
            options = PaduraksaHost.LoadOptions(hostOptions);
        }
        catch (OptionsValidationException ex)
        {
            problems = [.. ex.Failures];
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            problems = [$"The configuration could not be read: {ex.Message}"];
        }

        var context = new DoctorContext
        {
            Options = options,
            OptionsProblems = problems,
            CheckClock = request.CheckClock,
            ApiToken = await ReadTokenAsync(request.TokenFile, error, cancellationToken).ConfigureAwait(false),
        };
        if (configure is not null)
        {
            context = configure(context);
        }

        var report = await DoctorRunner.RunAsync(context, DoctorRunner.DefaultChecks, cancellationToken).ConfigureAwait(false);
        await (request.Json ? DoctorRunner.WriteJsonAsync(report, output) : DoctorRunner.WriteTextAsync(report, output)).ConfigureAwait(false);
        return report.Passed ? ExitCodes.Success : ExitCodes.Failure;
    }

    private static async Task<string?> ReadTokenAsync(string? tokenFile, TextWriter error, CancellationToken cancellationToken)
    {
        if (tokenFile is null)
        {
            return Environment.GetEnvironmentVariable(ConfigCommands.TokenVariable) is { Length: > 0 } token ? token.Trim() : null;
        }

        try
        {
            return (await File.ReadAllLinesAsync(tokenFile, cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.Trim() is { Length: > 0 } line ? line : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Couldn't read the token file {tokenFile}: {ex.Message} The running server's version is not compared.").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Runs <c>doctor --liveness</c>. Returns 0 or 1 (never another code): its one caller is a healthcheck. <paramref name="timeout"/> is for tests, which
    /// share a loaded machine with the server they probe: the first TLS handshake of a fresh process can take more than <see cref="Timeout"/> there.
    /// </summary>
    internal static async Task<int> LivenessAsync(
        PaduraksaHostOptions? hostOptions,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        PaduraksaOptions options;
        try
        {
            options = PaduraksaHost.LoadOptions(hostOptions);
        }
        catch (OptionsValidationException ex)
        {
            await error.WriteLineAsync("Unhealthy: invalid configuration: " + string.Join("; ", ex.Failures)).ConfigureAwait(false);
            return ExitCodes.Failure;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync("Unhealthy: the configuration could not be read: " + ex.Message).ConfigureAwait(false);
            return ExitCodes.Failure;
        }

        var uris = HealthUris(options);
        var plainHttp = uris[0].Scheme == Uri.UriSchemeHttp;
        using var client = CreateClient(plainHttp, timeout ?? Timeout);

        string? answerFailure = null;
        string? transportFailure = null;
        foreach (var uri in uris)
        {
            try
            {
                using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    await output.WriteLineAsync("healthy").ConfigureAwait(false);
                    return ExitCodes.Success;
                }

                // A listener that answered with the wrong status is more informative than a later candidate that refused.
                answerFailure ??= $"{uri} answered {(int)response.StatusCode}";
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                transportFailure = $"{uri} did not answer: {ex.Message}";
            }
        }

        await error.WriteLineAsync($"Unhealthy: {answerFailure ?? transportFailure}.").ConfigureAwait(false);
        return ExitCodes.Failure;
    }

    /// <summary>
    /// The candidates the probe tries, in order. The plain-HTTP fallback is loopback-only by construction; the HTTPS admin
    /// port follows <c>AdminBind</c> (see the class remarks).
    /// </summary>
    internal static IReadOnlyList<Uri> HealthUris(PaduraksaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var listen = options.Listen;
        if (listen.AdminLoopbackHttp > 0)
        {
            // ListenLocalhost binds 127.0.0.1 and ::1; AdminBind is irrelevant here.
            return
            [
                HealthUri(Uri.UriSchemeHttp, "127.0.0.1", listen.AdminLoopbackHttp),
                HealthUri(Uri.UriSchemeHttp, "[::1]", listen.AdminLoopbackHttp),
            ];
        }

        if (!IPAddress.TryParse(listen.AdminBind, out var bind) || bind.Equals(IPAddress.Any))
        {
            return [HealthUri(Uri.UriSchemeHttps, "127.0.0.1", listen.Admin)];
        }

        if (bind.Equals(IPAddress.IPv6Any))
        {
            // A [::] listener is dual-mode on Linux; try IPv6 first, then IPv4.
            return
            [
                HealthUri(Uri.UriSchemeHttps, "[::1]", listen.Admin),
                HealthUri(Uri.UriSchemeHttps, "127.0.0.1", listen.Admin),
            ];
        }

        return [HealthUri(Uri.UriSchemeHttps, bind.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{bind}]" : bind.ToString(), listen.Admin)];
    }

    private static Uri HealthUri(string scheme, string host, int port) =>
        new UriBuilder(scheme, host, port, "/healthz").Uri;

    private static HttpClient CreateClient(bool plainHttp, TimeSpan timeout)
    {
        SocketsHttpHandler handler = new()
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = timeout,
            PooledConnectionLifetime = TimeSpan.Zero,
        };

        if (!plainHttp)
        {
            // See the remarks: the admin's certificate is the local CA's (or the placeholder), which this probe deliberately
            // does not load. The socket is loopback, there is no proxy and no redirect, and only a status code is read.
#pragma warning disable CA5359 // Do not disable certificate validation: deliberate, loopback-only, documented above.
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
            handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        }

        return new HttpClient(handler) { Timeout = timeout };
    }
}
