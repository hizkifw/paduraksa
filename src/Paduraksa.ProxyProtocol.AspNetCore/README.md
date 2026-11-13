# Paduraksa.ProxyProtocol.AspNetCore

PROXY protocol (HAProxy v1 and v2) support for Kestrel. A connection middleware reads the header from a load balancer, then makes the connection's remote and
local endpoints the client's, so `HttpContext.Connection.RemoteIpAddress` and everything built on it (rate limits, logging, IP rules) sees the real client.
The parsing is [Paduraksa.ProxyProtocol](https://www.nuget.org/packages/Paduraksa.ProxyProtocol): strict, fuzzed and allocation-free.

```csharp
using System.Net;
using Paduraksa.ProxyProtocol.AspNetCore;

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(8080, listen =>
    {
        listen.UseProxyProtocol(options =>
        {
            options.TrustedProxies = [IPNetwork.Parse("10.0.0.0/8")]; // your load balancers
            options.HeaderTimeout = TimeSpan.FromSeconds(3);
            options.OnOutcome = outcome => metrics.Count(outcome);    // optional, for counters; an exception it throws is swallowed
        });
        listen.UseHttps(certificate); // after UseProxyProtocol: the header comes before the TLS handshake
    });
});
```

> [!WARNING]
> **Call `UseProxyProtocol` before `UseHttps`.** The load balancer's header comes first on the wire, so it has to be read before the TLS handshake. In the other order every HTTPS connection
> through the load balancer is closed during the TLS handshake and the app never sees a request: it looks like the proxy refusing everyone, health checks included. Kestrel doesn't let the
> middleware see whether `UseHttps` already ran, so this isn't detected for you. If every connection from the load balancer fails, check the order first.

## Behaviour

- **Secure by default.** `TrustedProxies` is empty, which refuses every connection until you add your load balancers. A connection from anywhere else is aborted without reading a byte.
- **Required by default.** A trusted peer that sends no valid header within `HeaderTimeout` (default 3 s), or whose header is malformed or over `MaxV2Length` (default 4096), is aborted.
- **`Required = false`** makes the header optional: a trusted peer whose first bytes don't begin a header goes on unchanged, with the socket's endpoints. Untrusted peers go on unchanged too, and a header they send is *not* honoured. A header that begins correctly and then breaks still aborts.
- **On success** the connection's `RemoteEndPoint` and `LocalEndPoint` become the header's source and destination (when it carries addresses; `LOCAL` and `UNKNOWN` keep the socket's), and `IProxyProtocolFeature` exposes the parsed header and the proxy's own endpoint.
- **Trust matching** treats an IPv4-mapped IPv6 peer (`::ffff:10.0.0.1`, what a dual-mode listener reports) as IPv4.
- **QUIC / HTTP/3** has no connection middleware, so the PROXY protocol doesn't apply there.

Keep the listener reachable by the load balancer alone: anything that can connect from a trusted address can claim any client address.

Targets .NET 10 and .NET 8.
