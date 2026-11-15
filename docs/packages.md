# Packages

Paduraksa's PROXY protocol support is also published on its own, for other .NET projects. They are the only parts of this repository built as NuGet packages. Both target .NET 10 and .NET 8.

| Package | What it is |
|---|---|
| `Paduraksa.ProxyProtocol` | A strict parser for the HAProxy PROXY protocol (v1 text and v2 binary). It has no dependency beyond the .NET base class library |
| `Paduraksa.ProxyProtocol.AspNetCore` | A Kestrel `UseProxyProtocol` connection middleware over it. It makes the client behind a load balancer the connection's remote address. With no trusted proxies configured, every connection is refused |

Their READMEs are the package documentation:

- [`Paduraksa.ProxyProtocol` README](../src/Paduraksa.ProxyProtocol/README.md): parsing a buffer, reading a header off a pipe, what it accepts, and trust.
- [`Paduraksa.ProxyProtocol.AspNetCore` README](../src/Paduraksa.ProxyProtocol.AspNetCore/README.md): `UseProxyProtocol`, its options and behaviour, and the rule to call it before `UseHttps`.

The packages are released separately from the app, on their own `proxyprotocol-v*` tag. [Working on the code](development.md#publishing-the-nuget-packages) says how.

Paduraksa itself uses the same parser for its PROXY protocol listeners: see [Client addresses](guide/connections-and-limits.md#proxy-protocol).
