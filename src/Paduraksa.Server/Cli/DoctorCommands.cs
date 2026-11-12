using System.CommandLine;
using System.Net;
using System.Security.Authentication;

using Microsoft.Extensions.Options;

using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;

namespace Paduraksa.Server.Cli;

/// <summary>
/// <c>paduraksa doctor</c>: <c>--liveness</c>, the one check the container's <c>HEALTHCHECK</c> can run without curl. It
/// asks the admin listener's <c>/healthz</c> over loopback and exits 0 or 1. TODO: the full doctor (ports, permissions,
/// libmsquic, DNS, clock skew).
/// </summary>
/// <remarks>
/// <b>Which listener.</b> The opt-in loopback plain-HTTP listener when it is enabled, else HTTPS to the admin port: a
/// wildcard bind becomes loopback, a specific one is used as given, because a listener bound to one address does not listen
/// on the others.
///
/// <b>Certificate validation is off for this one call.</b> The admin certificate is the local CA's (or the placeholder),
/// which this short-lived process has not loaded: trusting it would mean starting the vault, reading the master key and the
/// database, to ask one loopback question. The connection never leaves the machine, and the handler follows no redirects and
/// uses no proxy, so it cannot be pointed elsewhere.
/// </remarks>
internal static class DoctorCommands
{
    /// <summary>The whole probe's budget per candidate: well under Docker's 30 s interval, so a hung admin cannot pile probes up.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static Command Build()
    {
        Command doctor = new("doctor", "Check the process's environment. M5 implements --liveness (the container healthcheck).");
        Option<bool> liveness = new("--liveness")
        {
            Description = "Exit 0 when the admin listener's /healthz answers 200 over loopback, 1 otherwise. Used by the image's HEALTHCHECK.",
        };
        doctor.Options.Add(liveness);
        doctor.SetAction((parseResult, cancellationToken) => LivenessAsync(
            hostOptions: null,
            parseResult.GetValue(liveness),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            cancellationToken));
        return doctor;
    }

    /// <summary>
    /// Runs <c>doctor</c>. Returns 0 or 1 (never another code): its one caller is a healthcheck. <paramref name="timeout"/> is for tests, which
    /// share a loaded machine with the server they probe: the first TLS handshake of a fresh process can take more than <see cref="Timeout"/> there.
    /// </summary>
    internal static async Task<int> LivenessAsync(
        PaduraksaHostOptions? hostOptions,
        bool liveness,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!liveness)
        {
            await error.WriteLineAsync("Only --liveness is implemented so far (the full doctor arrives in M11). Usage: paduraksa doctor --liveness").ConfigureAwait(false);
            return ExitCodes.Failure;
        }

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
