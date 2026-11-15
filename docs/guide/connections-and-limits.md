# Connections, limits and client addresses

This page covers what Paduraksa does before it knows which host a request is for: identifying the client, limiting connections and waiting on slow clients. The per-host request size limits are at the end.

## Client addresses

Access lists, rate limits, the access log, analytics and the `X-Forwarded-For` header your upstreams read all use the **client address**. By default that is the address of whoever connected. If a proxy or load balancer sits in front of Paduraksa, every visitor looks like the proxy until you list who may tell Paduraksa the real address.

Set it under **Global defaults**, **Client addresses**. It is global because a connection has no host yet.

| Mode | Use it when | Where the client comes from |
|---|---|---|
| Direct (the default) | Visitors connect directly | The connection's own address |
| Behind a proxy | A proxy or load balancer you run is in front | A header from the proxies you list (`X-Forwarded-For`, right to left, by default) |
| Cloudflare proxy | Cloudflare connects to you | `CF-Connecting-IP`, from Cloudflare's edge ranges only |
| Cloudflare tunnel | A local `cloudflared` connects to you | `CF-Connecting-IP`, from the `cloudflared` addresses you list |

- **Behind a proxy** needs the **Trusted proxies**: addresses or CIDRs, one per line.
    - The header (**Client address header**, `X-Forwarded-For` by default) is read from the right. The first address that is not a trusted proxy is the client.
    - **Take the scheme from X-Forwarded-Proto** also takes the request scheme from the trusted proxies. It is off by default.
- **Cloudflare tunnel** needs **Where cloudflared connects from**: loopback, a Docker network or a specific IP.
- In both Cloudflare modes you can also accept `True-Client-IP` when `CF-Connecting-IP` is missing, and fall back to `X-Forwarded-For`.
- Paduraksa ignores these headers from a peer that is not trusted.
- Paduraksa strips every `cf-*` header and `True-Client-IP` before forwarding the request, so a LAN client cannot forge Cloudflare Access's identity headers.
- Upstreams always get a clean `X-Forwarded-For` and `X-Real-IP` with exactly one address: the client.

[Behind Cloudflare](cloudflare.md) covers Cloudflare in more detail, including its edge ranges and locking an origin to Cloudflare.

### PROXY protocol

If a load balancer in front of Paduraksa sends the PROXY protocol (v1 or v2), turn it on per TCP listener. The address in the header becomes the connection's address, before TLS.

```sh
Paduraksa__Listen__HttpsProxyProtocol__Enabled=true
Paduraksa__Listen__HttpsProxyProtocol__TrustedSources=192.0.2.10
Paduraksa__Listen__HttpProxyProtocol__Enabled=true
Paduraksa__Listen__HttpProxyProtocol__TrustedSources=192.0.2.10
```

- Turning it on makes the header **mandatory** on that listener. It needs at least one trusted source (an address or CIDR), and Paduraksa refuses a connection from anywhere else.
- `HeaderTimeout` is how long a trusted source has to send the header: 3 seconds by default, at most a minute.
- QUIC (HTTP/3) has no PROXY protocol.
- The per-address connection cap below counts the address in the header, so each of the load balancer's visitors has their own cap.

The parser is also published as [NuGet packages](../packages.md).

## Connection floods

Before TLS, the public listeners (80/tcp, 443/tcp and 443/udp) refuse two kinds of connection. Both are set under **Global defaults**, **Connections**, and the **Security overview**'s Connections card shows what they refuse.

### Open connections per address

- The built-in value is **256**, `0` turns it off, and the maximum is 100 000.
- Paduraksa closes an over-cap address's next connection at once, before TLS. An IPv6 /64 counts as one address.
- **Trusted proxies are never counted**, because every visitor behind one shares its address. They are Cloudflare's edge ranges in Cloudflare proxy mode, the `cloudflared` address in tunnel mode, and the proxies you list in Behind a proxy mode.
- If a proxy sits in front and is not listed as trusted, all its visitors look like one address and share the cap. Add it to the trusted proxies.
- The cap does not stop a flood from very many addresses. Only the global `MaxConnections` limits that. Behind Cloudflare, use origin lockdown.

### Connection deny list

Paduraksa refuses the **denied addresses** of an [access list](security.md#access-lists) before TLS, with no reply and no handshake. Use it for addresses you never want to talk to, such as a scanner's range.

- It judges the address that connects. A visitor behind Cloudflare or another trusted proxy is judged later, at the gate, by the host's access lists and rate limits.
- Only the list's denied addresses apply here. Its allowed addresses, countries and users apply to the hosts that name it (the dashboard warns if the list has any).
- The admin port (9443) is a separate listener and is not affected.

Refusals show in `/metrics` as `paduraksa_connections_events_total{event="refused_deny_list"|"refused_per_ip_limit"|"table_full"}`. The log gets at most one Information line per reason every ten seconds, with the count and the latest address.

## Slow clients and connection limits

These [server settings](server-settings.md) limit the public listeners, not the admin listener. Under Settings, Server, Connections, the timeouts and data rates change live and apply to new connections. The TLS handshake timeout and the two connection caps apply after a restart. The full list is in [Server settings](../operating/settings.md#connection-and-timeout-limits). In short:

- A client that takes more than 15 seconds to send its request headers gets a `408` and is closed, which stops slow-header attacks (slowloris).
- After a 10-second grace, Paduraksa cuts off a request body slower than 120 bytes a second or a response slower than 240. The rates are low enough that a phone on a poor link still works.
- The public listeners together allow at most 10 000 open connections, of which 2 000 may be WebSockets.
- The rates never cut a WebSocket or an event stream that sits idle between messages. Idle time is a per-upstream setting: see [Upstreams](upstreams.md#timeouts-websockets-and-event-streams).

## Request limits

How much of a request a host takes. Paduraksa refuses a request over a limit before the upstream sees it. The limits are under **Request limits** in host settings (global defaults, a profile or a host).

| Setting | Built-in | Range | Over it |
|---|---|---|---|
| Body size | 100 MiB | none (`0`), or 1 byte to 1 TiB | `413` |
| Header lines | 100 | 8 to 128 | `431` |
| Header size | 32 KiB | 1 KiB to 64 KiB | `431` |
| URL length | 8 KiB | 256 bytes to 16 KiB | `414` |

- **Body size** can also be set on a route ("Body size on this route"), so an upload route can take more or less than the rest of the host. The header and URL limits are checked before a route is known, so they belong to the host.
- If a request declares a size over the limit, it is refused at once. If it does not declare one (chunked), it is cut off mid-upload, and the client may see a reset instead of the `413`. A request that sends `Expect: 100-continue` gets the `413` first.
- The header and URL limits count raw bytes. The URL is the target as sent, path and query.
- Large cookies are the usual cause of a `431`.
- Body size `0` in the global defaults means no limit anywhere.
- The limits do not apply to an upgraded WebSocket, or to a request answered before its host is known (an ACME challenge, an unknown host's answer).
- Refusals use the built-in error pages and show in the access log.
