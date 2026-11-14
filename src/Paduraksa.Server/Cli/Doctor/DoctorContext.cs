using System.Net;

using Paduraksa.Application.Settings;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Server.Configuration;
using Paduraksa.Vault;

namespace Paduraksa.Server.Cli.Doctor;

/// <summary>
/// What the doctor's checks read: the effective options and the seams to the host. Real ones in production; a test replaces any with <c>with</c>.
/// The two cached probes are asked once per run, so a copy is made before the first check.
/// </summary>
internal sealed record DoctorContext
{
    private Task<AdminAnswer>? _admin;
    private Task<VaultInspection>? _vault;

    /// <summary>The effective options as the server would bind them, or null when they fail validation (<see cref="OptionsProblems"/>).</summary>
    public PaduraksaOptions? Options { get; init; }

    public IReadOnlyList<string> OptionsProblems { get; init; } = [];

    /// <summary>Environment access: only through here, so a test can set a variable without setting it for the process.</summary>
    public Func<string, string?> Env { get; init; } = Environment.GetEnvironmentVariable;

    public IDoctorFileSystem Files { get; init; } = new SystemFileSystem();

    public IPortProbe Ports { get; init; } = new SystemPortProbe();

    public IDoctorHttp Http { get; init; } = new SystemDoctorHttp();

    public IQuicProbe Quic { get; init; } = new SystemQuicProbe();

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The vault's read-only look at a data directory (<see cref="IVaultInspection"/>).</summary>
    public Func<string, IVaultInspection> Vault { get; init; } = DoctorVault.Create;

    public Func<string, CancellationToken, Task<ConfigDatabaseInspection>> InspectConfigDatabase { get; init; } = ConfigDatabase.InspectAsync;

    /// <summary>The server settings the config database stores (read-only), null when it stores none.</summary>
    public Func<string, CancellationToken, Task<ServerSettings?>> ReadStoredSettings { get; init; } = StoredServerSettings.ReadAsync;

    /// <summary>Check the clock against a public server: the one outward request doctor can make, so only when asked (<c>--check-clock</c>).</summary>
    public bool CheckClock { get; init; }

    /// <summary>The URL whose <c>Date</c> header is the clock's reference.</summary>
    public Uri ClockUrl { get; init; } = new("https://www.cloudflare.com/cdn-cgi/trace");

    /// <summary>An API token, for asking the running server its version. Null when none was given.</summary>
    public string? ApiToken { get; init; }

    public string Version { get; init; } = OwnBuild.Version;

    public string? Commit { get; init; } = OwnBuild.Commit;

    /// <summary>The vault's look at the data directory, taken once for the checks that read it.</summary>
    public Task<VaultInspection> InspectVaultAsync(CancellationToken cancellationToken) =>
        _vault ??= Vault(Options!.DataDirectory).InspectAsync(cancellationToken).AsTask();

    /// <summary>
    /// What answers on this installation's admin listener over loopback, asked once for all the checks that want to know. A Paduraksa answers
    /// <c>/healthz</c> with <c>ok</c> or <c>unavailable</c>; anything else on the port is another service.
    /// </summary>
    public Task<AdminAnswer> RunningAdminAsync(CancellationToken cancellationToken) =>
        _admin ??= ProbeAdminAsync(cancellationToken);

    private async Task<AdminAnswer> ProbeAdminAsync(CancellationToken cancellationToken)
    {
        if (Options is null)
        {
            return AdminAnswer.None;
        }

        HttpAnswer? other = null;
        Uri? otherUri = null;
        foreach (var uri in DoctorCommands.HealthUris(Options))
        {
            var answer = await Http.GetAsync(uri, trustAnyCertificate: true, bearerToken: null, DoctorCommands.Timeout, cancellationToken).ConfigureAwait(false);
            if (!answer.Answered)
            {
                continue;
            }

            if (answer.Body?.Trim() is "ok" or "unavailable")
            {
                return new AdminAnswer(AdminKind.Paduraksa, uri, answer.Status);
            }

            other ??= answer;
            otherUri ??= uri;
        }

        return other is null ? AdminAnswer.None : new AdminAnswer(AdminKind.Other, otherUri, other.Status);
    }
}

internal enum AdminKind
{
    None,
    Paduraksa,
    Other,
}

/// <param name="Kind">What answered.</param>
/// <param name="Uri">The <c>/healthz</c> address that did.</param>
/// <param name="Status">Its status.</param>
internal sealed record AdminAnswer(AdminKind Kind, Uri? Uri, int? Status)
{
    public static AdminAnswer None { get; } = new(AdminKind.None, null, null);

    public bool IsPaduraksa => Kind == AdminKind.Paduraksa;
}

/// <summary>What this binary is.</summary>
internal static class OwnBuild
{
    private static readonly string Informational =
        System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(OwnBuild).Assembly)?.InformationalVersion
        ?? typeof(OwnBuild).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    public static string Version { get; } = Informational.Split('+')[0];

    public static string? Commit { get; } = Informational.Contains('+', StringComparison.Ordinal) ? Informational[(Informational.IndexOf('+', StringComparison.Ordinal) + 1)..] : null;
}

/// <summary>Builds the vault's inspection for a data directory, the way the server registers the vault.</summary>
internal static class DoctorVault
{
    public static IVaultInspection Create(string dataDirectory)
    {
        var provider = new ServiceCollection()
            .AddPaduraksaVault(dataDirectory)
            .BuildServiceProvider();
        return new Owned(provider, provider.GetRequiredService<IVaultInspection>());
    }

    /// <summary>The inspection, disposing the provider that holds the vault when it has looked.</summary>
    private sealed class Owned(ServiceProvider provider, IVaultInspection inner) : IVaultInspection
    {
        public async ValueTask<VaultInspection> InspectAsync(CancellationToken cancellationToken)
        {
            await using (provider.ConfigureAwait(false))
            {
                return await inner.InspectAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
