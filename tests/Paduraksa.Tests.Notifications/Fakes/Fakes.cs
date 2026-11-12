using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Tests.Notifications.Fakes;

/// <summary>The vault's notification secrets, in memory; <see cref="Reads"/> counts how often one was opened.</summary>
internal sealed class FakeSecrets : INotificationSecretSource
{
    private readonly Dictionary<SecretId, byte[]> _values = [];

    public int Reads { get; private set; }

    public SecretId Add(string value)
    {
        var id = SecretId.New();
        _values[id] = Encoding.UTF8.GetBytes(value);
        return id;
    }

    public async ValueTask<bool> UseAsync(SecretId id, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> use, CancellationToken cancellationToken)
    {
        if (!_values.TryGetValue(id, out var value))
        {
            return false;
        }

        Reads++;
        var copy = value.ToArray();
        try
        {
            await use(copy, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }

        return true;
    }
}

/// <summary>HMAC-SHA-256 keys by secret ID, like the vault's <see cref="IMacKeySource"/>.</summary>
internal sealed class FakeMacKeys : IMacKeySource
{
    private readonly Dictionary<SecretId, byte[]> _keys = [];

    public SecretId Add(string key)
    {
        var id = SecretId.New();
        _keys[id] = Encoding.UTF8.GetBytes(key);
        return id;
    }

    public ValueTask<IMacKey?> OpenAsync(SecretId id, SecretKind expected, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IMacKey?>(expected == SecretKind.HmacSigningKey && _keys.TryGetValue(id, out var key) ? new Key(key) : null);

    private sealed class Key(byte[] key) : IMacKey
    {
        public MacAlgorithm Algorithm => MacAlgorithm.HmacSha256;

        public int TagLength => 32;

        public int Sign(ReadOnlySpan<byte> data, Span<byte> tag) => HMACSHA256.HashData(key, data, tag);

        public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag) => false;

        public bool VerifyTruncated(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag) => false;
    }
}

internal sealed record Recorded(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string Text => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// A loopback HTTP server that records what is POSTed to it and answers with the next queued status (200 when none).
/// It counts only POSTs: on this machine a Node process sends <c>GET /</c> to every new localhost port.
/// </summary>
internal sealed class FakeEndpoint : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly ConcurrentQueue<Recorded> _requests = new();
    private readonly ConcurrentQueue<(int Status, string? Location)> _answers = new();

    private FakeEndpoint(IHost host, Uri address)
    {
        _host = host;
        Address = address;
    }

    public Uri Address { get; }

    public IReadOnlyList<Recorded> Requests => [.. _requests];

    public void Answer(int status, string? location = null) => _answers.Enqueue((status, location));

    public static async Task<FakeEndpoint> StartAsync()
    {
        FakeEndpoint? endpoint = null;
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0))
                .Configure(app => app.Run(async context =>
                {
                    if (context.Request.Method != "POST")
                    {
                        return;
                    }

                    using MemoryStream body = new();
                    await context.Request.Body.CopyToAsync(body);
                    endpoint!._requests.Enqueue(new Recorded(
                        context.Request.Method,
                        context.Request.Path.Value + context.Request.QueryString.Value,
                        context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                        body.ToArray()));
                    var (status, location) = endpoint._answers.TryDequeue(out var next) ? next : (200, null);
                    context.Response.StatusCode = status;
                    if (location is not null)
                    {
                        context.Response.Headers.Location = location;
                    }
                })))
            .Build();
        await host.StartAsync();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        endpoint = new FakeEndpoint(host, new Uri(address));
        return endpoint;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
    }
}
