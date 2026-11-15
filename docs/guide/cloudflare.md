# Behind Cloudflare

If Cloudflare (or a `cloudflared` tunnel) sits in front of Paduraksa, three things need attention: the real visitor address, requests that skip Cloudflare, and certificates.

## Tell Paduraksa about Cloudflare

Under **Global defaults**, **Client addresses**, pick the mode that matches:

- **Cloudflare proxy:** Cloudflare connects to Paduraksa directly. Only connections from Cloudflare's published edge ranges are trusted. The client address is `CF-Connecting-IP`.
- **Cloudflare tunnel:** a local `cloudflared` connects to Paduraksa. List the address it connects from (loopback, a Docker network CIDR or an IP). The edge ranges do not apply, because the peer is `cloudflared`.

Access lists, rate limits, the access log, analytics and the `X-Forwarded-For` your upstreams read then all use the visitor's address. See [Connections, limits and client addresses](connections-and-limits.md#client-addresses).

Also in these modes:

- **Country rules** work, with the country taken from `CF-IPCountry`. See [Access lists](security.md#countries).
- **The scheme** comes from `CF-Visitor`, so Cloudflare's "Flexible" SSL (plain HTTP to the origin) does not cause a redirect loop when you force HTTPS. Flexible leaves the Cloudflare-to-Paduraksa leg unencrypted: use Full (strict).
- **`CF-Ray`** is recorded in the access log and available as `{cfRay}` on error pages, next to Paduraksa's request ID.
- Paduraksa removes every `cf-*` header and `True-Client-IP` that comes from a peer other than Cloudflare, so a LAN client cannot forge Cloudflare Access's identity headers.

### Edge ranges

In **Cloudflare proxy** mode, Paduraksa trusts Cloudflare's edge ranges. A list ships with each build, and a daily job refreshes it from `https://api.cloudflare.com/client/v4/ips`, falling back to `https://www.cloudflare.com/ips-v4` and `ips-v6`. The server needs outbound HTTPS to Cloudflare for that.

- Paduraksa refuses a refresh that looks wrong, and keeps the last good list after a failed one.
- The review and the Overview warn when the list in use is more than 30 days old, or three refreshes in a row failed.
- The list is stored in `<data dir>/cloudflare-ranges.json`.
- Turn the job off, or change how often it runs, under Settings, Server, Integrations, Cloudflare (live; see [Server settings](server-settings.md)). The same keys in the environment (`Paduraksa__Cloudflare__RangeRefresh__Enabled`, `__Interval`) win and lock the dashboard's fields.
- The URLs and the start delay are environment-only (`Paduraksa__Cloudflare__RangeRefresh__*`).

## Origin lockdown

A host can refuse any request whose connection does not come from Cloudflare (or `cloudflared`), so nobody can reach the origin by IP and skip Cloudflare's WAF. The setting is **Only allow Cloudflare**, in host settings:

| Value | A connection from anywhere else gets |
|---|---|
| Off (the default) | Handled as usual |
| Forbid | `403` with the built-in page |
| Drop | The connection is aborted with no answer |

- It works only in a Cloudflare mode. The review warns if it is set without one.
- It runs before anything else on the host, so a forged `CF-Connecting-IP` from an untrusted peer changes nothing.
- If **every** enabled host has it on, an untrusted peer is dropped before the TLS handshake on 443, with either value.
- Port 80 is not checked at the connection, so an ACME HTTP-01 validation from the CA still arrives when the record is DNS-only. Every other request to a locked-down host is refused.
- A locked-down admin host is reachable only through Cloudflare. Lockdown does not cover the admin port (9443), which is a separate listener.
- The lock-out guard refuses a change that adds lockdown to an admin host if the caller may have connected directly.

## Authenticated Origin Pulls

The strong form of lockdown. During the TLS handshake, Paduraksa asks Cloudflare for a client certificate and checks it against Cloudflare's origin-pull CA. The setting is **Authenticated Origin Pulls**, in host settings.

- **Turn on the zone setting in Cloudflare too**, or no request gets through.
- It works only in Cloudflare mode. In tunnel mode `cloudflared` presents no client certificate, so every request would be refused, and the review says so.
- A host with it on is never served over HTTP/3, because Cloudflare does not connect to origins over QUIC.
- A request on a connection with no verified certificate gets `421` if the connection's SNI does not name the host, and `403` otherwise (plain HTTP included).
- The CA that ships with the build expires on 2029-11-01. To replace it earlier, point `Paduraksa__Cloudflare__OriginPullCaFile` at a PEM file of the new CA (public certificates only, at most 1 MiB). It is read at start, and a file with no certificate stops the start.

## Certificates

- **Cloudflare Origin CA** certificates can be [uploaded](tls.md#uploaded-certificates) like any other. Only Cloudflare trusts them, so keep the zone's SSL mode on Full or Full (strict).
- **ACME with DNS-01** through the Cloudflare DNS provider works whether or not the proxy mode is on. It needs a scoped API token: see [DNS-01 providers](tls.md#dns-01-providers).
- **ACME with HTTP-01** works behind an orange-cloud record, because the validation arrives from Cloudflare's ranges. Behind a DNS-only record it arrives from the CA.

## Ask the Simulator

The **Simulator** takes a "via Cloudflare" choice and a country, and shows which address and country your policies would see.
