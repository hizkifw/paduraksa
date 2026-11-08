using System.Net;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

using Paduraksa.Server.Hosting;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// A real <see cref="PaduraksaHost"/> for the browser tests: its own data directory, OS-assigned ports, the admin on loopback HTTPS (served
/// with the process's local-CA certificate, which the test browser is told to accept), and a short session re-check so a revocation reaches
/// an open page within seconds.
/// </summary>
internal sealed class DashboardHost : IAsyncDisposable
{
    public const string OwnerName = "owner";
    public const string OwnerPassword = "correct horse battery staple";

    private readonly string _dataDirectory;

    private DashboardHost(PaduraksaHost host, string dataDirectory)
    {
        Host = host;
        _dataDirectory = dataDirectory;
        string address = host.GetListenAddresses(HostRole.Admin).First(a => a.StartsWith("https://", StringComparison.Ordinal));
        Admin = new UriBuilder("https", IPAddress.Loopback.ToString(), new Uri(address).Port).Uri;
    }

    public PaduraksaHost Host { get; }

    /// <summary>The admin app, e.g. <c>https://127.0.0.1:41234/</c>.</summary>
    public Uri Admin { get; }

    public static async Task<DashboardHost> StartAsync(CancellationToken cancellationToken)
    {
        string dataDirectory = Directory.CreateTempSubdirectory("paduraksa-dashboard-").FullName;
        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Paduraksa:DataDirectory"] = Path.Combine(dataDirectory, "data"),
            ["Paduraksa:Listen:Http"] = "0",
            ["Paduraksa:Listen:Https"] = "0",
            ["Paduraksa:Listen:Http3"] = "false",
            ["Paduraksa:Listen:Admin"] = "0",
            ["Paduraksa:Listen:AdminBind"] = "127.0.0.1",
            ["Paduraksa:ShutdownTimeout"] = "00:00:05",
            ["Paduraksa:Development:PlaceholderCertificate"] = "true",
            ["Paduraksa:Admin:PasswordIterations"] = "100000",
            ["Paduraksa:Admin:SessionRecheckInterval"] = "00:00:01",
            ["Paduraksa:Admin:InstanceName"] = "homelab",
            ["Logging:LogLevel:Default"] = "Warning",
        };

        PaduraksaHost host = PaduraksaHost.Create(new PaduraksaHostOptions
        {
            EnvironmentName = "Testing",
            AllowEphemeralPorts = true,
            ContentRootPath = AppContext.BaseDirectory,
            ConfigureConfiguration = builder => builder.AddInMemoryCollection(settings),
        });
        try
        {
            await host.StartAsync(cancellationToken);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }

        return new DashboardHost(host, dataDirectory);
    }

    public async Task<string> SetupTokenAsync(CancellationToken cancellationToken) =>
        await Host.Shared.Setup.EnsureTokenAsync(cancellationToken) ?? throw new InvalidOperationException("Setup is already complete.");

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the system's temp cleaner gets the rest.
        }
    }
}
