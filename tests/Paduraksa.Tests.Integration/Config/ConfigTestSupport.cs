using System.Net;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Server.Hosting;
using Paduraksa.Storage.Sqlite;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Config;

/// <summary>Config through the real <see cref="ConfigService"/> of a <see cref="PaduraksaHost"/>, and its files.</summary>
internal static class ConfigTestSupport
{
    /// <summary>An origin the real (permission-based) authorizers allow: the CLI's actor, here on the API channel. A made-up user would be refused.</summary>
    public static CommandOrigin Origin { get; } = new(Actor.Cli, Channel.Api, IPAddress.Loopback);

    /// <summary>Runs <paramref name="command"/> and asserts it committed and went live; returns the revision.</summary>
    public static async Task<RevisionRef> CommitAsync(PaduraksaHost host, ConfigCommand command, CancellationToken ct)
    {
        ConfigCommandResult result = await host.Shared.Config.ExecuteAsync(command, Origin, ct);
        Assert.True(
            result.Status == ConfigCommandStatus.Committed && result.Publish is { Succeeded: true },
            $"{result.Status}: {string.Join("; ", result.Errors.Concat(result.RelatedErrors).Select(e => $"{e.Path} {e.Code} {e.Message}"))}"
            + $" {string.Join("; ", result.Publish?.Errors ?? [])}");
        return result.Revision!;
    }

    /// <summary>
    /// Leaves <paramref name="document"/> committed (as one revision) in <paramref name="dataDirectory"/>, the way an
    /// admin would: a host starts on it, takes the command and stops again.
    /// </summary>
    public static async Task<RevisionRef> SeedAsync(string dataDirectory, ConfigDocument document, CancellationToken ct)
    {
        await using PaduraksaHost host = TestHost.Create(dataDirectory: dataDirectory);
        await host.StartAsync(ct);
        CurrentConfig current = await host.Shared.Config.GetCurrentAsync(ct);
        RevisionRef revision = await CommitAsync(host, new ReplaceConfigCommand(document, current.Revision?.Id), ct);
        await host.StopAsync(ct);
        return revision;
    }

    public static UpstreamConfig Upstream(string label, string address) =>
        new(TestIds.Upstream(label), label, Destinations: [new DestinationConfig(TestIds.Destination(label + "-1"), address)]);

    public static HostConfig ProxyHost(string label, string name, string upstream) =>
        new(TestIds.Host(label), [name], Routes: [new RouteConfig(TestIds.Route(label + "-root"), TestIds.Upstream(upstream))]);

    public static HostConfig RespondHost(string label, string name, string body) =>
        new(TestIds.Host(label), [name], Kind: HostKind.Respond, Respond: new RespondConfig(200, body));

    /// <summary>A request to the host's public listener with the given <c>Host</c> header.</summary>
    public static HttpRequestMessage Request(PaduraksaHost host, string scheme, string hostHeader, string pathAndQuery)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(TestHost.Endpoint(host, HostRole.Public, scheme), pathAndQuery));
        request.Headers.Host = hostHeader;
        return request;
    }

    /// <summary>The body of a plain-HTTP GET for <paramref name="hostHeader"/>, with its status.</summary>
    public static async Task<(HttpStatusCode Status, string Body)> GetAsync(HttpClient client, PaduraksaHost host, string hostHeader, string path, CancellationToken ct)
    {
        using HttpRequestMessage request = Request(host, "http", hostHeader, path);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary><c>/readyz</c> on the admin listener: status and body.</summary>
    public static async Task<(HttpStatusCode Status, string Body)> ReadyzAsync(HttpClient client, PaduraksaHost host, CancellationToken ct)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri(TestHost.Endpoint(host, HostRole.Admin, "https"), "/readyz"), ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    public static string DatabasePath(string dataDirectory) => Path.Combine(dataDirectory, ConfigDatabase.FileName);

    /// <summary>A context on the data directory's config database, outside any host.</summary>
    public static ConfigDbContext OpenDatabase(string dataDirectory)
    {
        DbContextOptionsBuilder<ConfigDbContext> options = new();
        options.UsePaduraksaSqlite(PaduraksaSqlite.ConnectionString(DatabasePath(dataDirectory)));
        return new ConfigDbContext(options.Options);
    }

    /// <summary>Runs raw SQL on the data directory's config database (tampering); the pool is cleared first and after, so
    /// no host reuses a connection that saw the old state.</summary>
    public static async Task ExecuteSqlAsync(string dataDirectory, string sql, CancellationToken ct)
    {
        ClearPool(dataDirectory);
        await using (SqliteConnection connection = new(PaduraksaSqlite.ConnectionString(DatabasePath(dataDirectory))))
        {
            await connection.OpenAsync(ct);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }

        ClearPool(dataDirectory);
    }

    /// <summary>The first column of the first row of <paramref name="sql"/>, on a connection of its own.</summary>
    public static async Task<object?> ScalarAsync(string dataDirectory, string sql, CancellationToken ct)
    {
        await using SqliteConnection connection = new(PaduraksaSqlite.ConnectionString(DatabasePath(dataDirectory)));
        await connection.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct);
    }

    /// <summary>Closes the pooled connections to the data directory's database (only those: other tests run alongside).</summary>
    public static void ClearPool(string dataDirectory)
    {
        using SqliteConnection connection = new(PaduraksaSqlite.ConnectionString(DatabasePath(dataDirectory)));
        SqliteConnection.ClearPool(connection);
    }
}
