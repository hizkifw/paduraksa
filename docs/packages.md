# Packages

Two pieces of Paduraksa are also published on their own, as four NuGet packages for other .NET projects. Nothing else in this repository is built as a package.

The PROXY protocol support targets .NET 10 and .NET 8:

| Package | What it is |
|---|---|
| `Paduraksa.ProxyProtocol` | A strict parser for the HAProxy PROXY protocol (v1 text and v2 binary). It has no dependency beyond the .NET base class library |
| `Paduraksa.ProxyProtocol.AspNetCore` | A Kestrel `UseProxyProtocol` connection middleware built on the parser. It sets the connection's remote address to the client behind a load balancer. With no trusted proxies configured, it refuses every connection |

The typed IDs Paduraksa uses for every object:

| Package | What it is |
|---|---|
| `Paduraksa.TypedIds` | A source generator that makes one ID struct per entity, with a prefix (`usr_` and 22 base62 characters). Three kinds: UUIDv7, UUIDv4 and an ordered Int64. Allocation-free formatting and parsing, JSON and `TypeConverter` support. The generator is inside the package. Targets .NET 10 and .NET 8 |
| `Paduraksa.TypedIds.EntityFrameworkCore` | One EF Core model convention that maps every typed ID to a binary or integer column, plus seeding of the Int64 generator. Targets .NET 10 |

Their READMEs are the package documentation:

- [`Paduraksa.ProxyProtocol` README](../src/Paduraksa.ProxyProtocol/README.md): parsing a buffer, reading a header off a pipe, what it accepts, and trust.
- [`Paduraksa.ProxyProtocol.AspNetCore` README](../src/Paduraksa.ProxyProtocol.AspNetCore/README.md): `UseProxyProtocol`, its options and behaviour, and the rule to call it before `UseHttps`.
- [`Paduraksa.TypedIds` README](../src/Paduraksa.TypedIds/README.md): declaring IDs, the three kinds and the enumerability warning of Int64, and source-generated JSON.
- [`Paduraksa.TypedIds.EntityFrameworkCore` README](../src/Paduraksa.TypedIds.EntityFrameworkCore/README.md): the convention, ordering, JSON columns, Int64 seeding and query rules.

Each family is released separately from the app, on its own tag: `proxyprotocol-v*` and `typedids-v*`. [Working on the code](development.md#publishing-the-nuget-packages) says how.

Paduraksa itself uses the same parser for its PROXY protocol listeners: see [Client addresses](guide/connections-and-limits.md#proxy-protocol).
