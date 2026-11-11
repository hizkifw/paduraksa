using System.Net;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Tests.Unit.Compilation;

/// <summary>
/// Runs requests through the real gate (<see cref="GateMiddleware.InvokeAsync"/>) over a compiled snapshot, bound to
/// stand-in endpoints. The outcome is the status the gate answered, <see cref="Forwarded"/> or <see cref="Aborted"/>.
/// </summary>
internal sealed class GateHarness : IDisposable
{
    public const int Forwarded = 0;
    public const int Aborted = -1;

    private readonly SnapshotStore _store = new();
    private readonly GateMiddleware _gate;

    public GateHarness(RuntimeSnapshot candidate)
    {
        var endpoints = new Endpoint[candidate.RouteIdBySlot.Length];
        for (var i = 0; i < endpoints.Length; i++)
        {
            endpoints[i] = new Endpoint(static _ => Task.CompletedTask, EndpointMetadataCollection.Empty, candidate.RouteIdBySlot[i].ToString());
        }

        _store.Publish(candidate.Bind(1, endpoints));
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
    }

    public void Dispose() => _store.Dispose();

    public async Task<int> SendAsync(string host, string scheme, string path, IPAddress client, string method = "GET", string? userAgent = null)
    {
        DefaultHttpContext context = new();
        Lifetime lifetime = new();
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.Request.Method = method;
        if (userAgent is not null)
        {
            context.Request.Headers.UserAgent = userAgent;
        }

        context.Request.Protocol = "HTTP/1.1";
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = client;

        var forwarded = false;
        await _gate.InvokeAsync(context, _ =>
        {
            forwarded = true;
            return Task.CompletedTask;
        });

        return forwarded ? Forwarded : lifetime.Aborted ? Aborted : context.Response.StatusCode;
    }

    private sealed class Lifetime : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => Aborted = true;
    }
}
