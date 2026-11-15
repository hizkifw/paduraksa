# Connections, limits and client addresses

This page covers what Paduraksa does before it knows which host a request is for: who the client is, how many connections it accepts and how slowly it will wait. The per-host request size limits are at the end.

## Client addresses

Access lists, rate limits, the access log, analytics and the `X-Forwarded-For` header your upstreams read all use the **client address**. By default that is the address of whoever connected. If a proxy or load balancer sits in front of Paduraksa, every visitor looks like the proxy until you say who may tell Paduraksa the real address.

Set it under **Global defaults**, **Client addresses**. It is global, because a connection has no host yet.

| Mode | Use it when | Where the client comes from |
|---|---|---|
| Direct (the default) | Visitors connect directly | The connection's own address |
| Behind a proxy | A proxy or load balancer you run is in front | A header from the proxies you list (`X-Forwarded-For`, right to left, by default) |
| Cloudflare proxy | Cloudflare connects to you | `CF-Connecting-IP`, from Cloudflare's edge ranges only |
| Cloudflare tunnel | A local `cloudflared` connects to you | `CF-Connecting-IP`, from the `cloudflared` addresses you list |

- **Behind a proxy** needs the **Trusted proxies**: addresses or CIDRs, one per line.
    - The header (**Client address header**, `X-Forwarded-For` by default) is read from the right. The first address that is not a trusted proxy is the client.
    - **Take the scheme from X-Forwarded-Proto** also takes the request scheme from the trusted proxies. Off by default.
- **Cloudflare tunnel** needs **Where cloudflared connects from**: loopback, a Docker network or a specific IP.
- In both Cloudflare modes you can also accept `True-Client-IP` when `CF-Connecting-IP` is missing, and fall back to `X-Forwarded-For`.
- A header from a peer that is not trusted is ignored.
- Every `cf-*` header and `True-Client-IP` is stripped before the request is forwarded, so a LAN client cannot forge Cloudflare Access's identity headers.
- Upstreams always get a clean `X-Forwarded-For` and `X-Real-IP` with exactly one address: the client.

More on Cloudflare, including its edge ranges and locking an origin to Cloudflare, is on [Behind Cloudflare](cloudflare.md).

### PROXY protocol

If a load balancer in front of Paduraksa sends the PROXY protocol (v1 or v2), turn it on per TCP listener. The address in the header becomes the connection's address, before TLS.

```sh
Paduraksa__Listen__HttpsProxyProtocol__Enabled=true
Paduraksa__Listen__HttpsProxyProtocol__TrustedSources=192.0.2.10
Paduraksa__Listen__HttpProxyProtocol__Enabled=true
Paduraksa__Listen__HttpProxyProtocol__TrustedSources=192.0.2.10
```

- Turning it on makes the header **mandatory** on that listener. It needs at least one trusted source (an address or CIDR), and a connection from anywhere else is refused.
- `HeaderTimeout` is how long a trusted source has to send the header: 3 seconds by default, at most a minute.
- QUIC (HTTP/3) has no PROXY protocol.
- The per-address connection cap below counts the address in the header, so each of the load balancer's visitors has a cap of their own.

The parser is also published as [NuGet packages](../packages.md).

## Connection floods

Before TLS, the public listeners (80/tcp, 443/tcp and 443/udp) refuse two kinds of connection. Set them under **Global defaults**, **Connections**. The **Security overview**'s Connections card shows what they refuse.

### Open connections per address

- Built-in **256**. `0` turns it off, and the most is 100 000.
- An address over the cap has its next connection closed at once, before TLS. An IPv6 /64 counts as one address.
- **Trusted proxies are never counted**, because every visitor behind one shares its address. That means Cloudflare's edge ranges in the Cloudflare proxy mode, the `cloudflared` address in the tunnel mode, and the proxies you list in the Behind a proxy mode.
- If a proxy sits in front and is not listed as trusted, all its visitors look like one address and share the cap. List it.
- The cap does not stop a flood from very many addresses. The global `MaxConnections` is what remains. Behind Cloudflare, use origin lockdown.

### Connection deny list

An [access list](security.md#access-lists) whose **denied addresses** are refused before TLS, with no reply and no handshake. Use it for addresses you never want to talk to, such as a scanner's range.

- It judges the address that connects. A visitor behind Cloudflare or another trusted proxy is judged later, at the gate, by the access lists and rate limits of the host.
- Only the list's denied addresses apply here. Its allowed addresses, countries and users are for the hosts that name it (the dashboard warns when the list has some).
- The admin port (9443) is a separate listener and is not affected.

Refusals show in `/metrics` as `paduraksa_connections_events_total{event="refused_deny_list"|"refused_per_ip_limit"|"table_full"}`. The log gets one Information line per reason at most every ten seconds, with the count and the latest address.

## Slow clients and connection limits

These limits apply to the public listeners, not the admin listener. They are [server settings](server-settings.md). Under Settings, Server, Connections, the timeouts and data rates change live, for new connections. The TLS handshake timeout and the two connection caps apply after a restart. The full list is in [Server settings](../operating/settings.md#connection-and-timeout-limits). In short:

- A client that takes more than 15 seconds to send its request headers gets a `408` and is closed. This stops slow-header attacks (slowloris).
- After a 10-second grace, a request body slower than 120 bytes a second, or a response slower than 240, is cut off. The rates are forgiving, so a phone on a poor link still works.
- At most 10 000 connections are open on the public listeners together, and 2 000 of those may be WebSockets.
- None of the rates cuts a WebSocket or an event stream that sits idle between messages. Idle time is a per-upstream setting: see [Upstreams](upstreams.md#timeouts-websockets-and-event-streams).

## Request limits

How much of a request a host takes. A request over a limit is refused before the upstream sees it. Set them under **Request limits** in host settings (global defaults, a profile or a host).

| Setting | Built-in | Range | Over it |
|---|---|---|---|
| Body size | 100 MiB | none (`0`), or 1 byte to 1 TiB | `413` |
| Header lines | 100 | 8 to 128 | `431` |
| Header size | 32 KiB | 1 KiB to 64 KiB | `431` |
| URL length | 8 KiB | 256 bytes to 16 KiB | `414` |

- **Body size** can also be set on a route ("Body size on this route"), so an upload route can take more or less than the rest of the host. The header and URL limits are judged before a route is known, so they belong to the host.
- A request that declares its size over the limit is refused at once. One that does not (chunked) is cut off mid-upload, and the client may see a reset instead of the `413`. Sending `Expect: 100-continue` gets the `413` first.
- The header and URL limits count raw bytes. The URL is the target as sent, path and query.
- Large cookies are the usual cause of a `431`.
- Set body size `0` in the global defaults for no limit anywhere.
- The limits do not apply to a request answered before its host is known (an ACME challenge, an unknown host's answer), or to an upgraded WebSocket.
- Refusals use the built-in error pages, and the access log shows them.
