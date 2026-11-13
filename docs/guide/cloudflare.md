# Behind Cloudflare

If Cloudflare (or a `cloudflared` tunnel) sits in front of Paduraksa, three things need attention: knowing the real visitor address, keeping people from skipping Cloudflare, and certificates.

## Tell Paduraksa about Cloudflare

Under **Global defaults**, **Client addresses**, pick the mode that matches:

- **Cloudflare proxy:** Cloudflare connects to Paduraksa directly. Only connections from Cloudflare's published edge ranges are trusted. The client address is `CF-Connecting-IP`.
- **Cloudflare tunnel:** a local `cloudflared` connects to Paduraksa. List the address it connects from (loopback, a Docker network CIDR or an IP). The edge ranges do not apply, because the peer is `cloudflared`.

Everything that uses the client address then sees the visitor: access lists, rate limits, the access log, analytics and the `X-Forwarded-For` your upstreams read. See [Connections, limits and client addresses](connections-and-limits.md#client-addresses).

Also in these modes:

- **Country rules** work: the country comes from `CF-IPCountry`. See [Access lists](security.md#countries).
- **The scheme** comes from `CF-Visitor`, so Cloudflare's "Flexible" SSL (plain HTTP to the origin) does not cause a redirect loop when you force HTTPS. Flexible is counted and logged once, and it is not secure between Cloudflare and Paduraksa: use Full (strict).
- **`CF-Ray`** is recorded in the access log and available as `{cfRay}` on error pages, next to Paduraksa's request ID.
- Every `cf-*` header and `True-Client-IP` from a peer that is not Cloudflare is ignored and removed before forwarding, so a LAN client cannot forge Cloudflare Access's identity headers.

### Edge ranges

In **Cloudflare proxy** mode, Paduraksa trusts Cloudflare's edge ranges. A list ships with each build, and a background job refreshes it daily from `https://api.cloudflare.com/client/v4/ips`, falling back to `https://www.cloudflare.com/ips-v4` and `ips-v6`. The server needs outbound HTTPS to Cloudflare for that.

- A refresh is accepted only if it looks right: strict CIDRs, at least three IPv4 and three IPv6 blocks, nothing wider than /8 (IPv4) or /12 (IPv6).
- A failed or refused refresh keeps the last good list. The review and the Overview warn when the list in use is more than 30 days old or three refreshes in a row failed.
- The list is stored in `<data dir>/cloudflare-ranges.json`. A changed list republishes the live config without making a revision.
- Turn the job off with `Paduraksa__Cloudflare__RangeRefresh__Enabled=false`. The URLs, start delay and interval are settable under `Paduraksa__Cloudflare__RangeRefresh__*` too.

## Origin lockdown

A host can refuse any request whose connection does not come from Cloudflare (or from `cloudflared`), so the origin cannot be reached by IP to skip Cloudflare's WAF. In host settings, **Only allow Cloudflare**:

| Value | A connection from anywhere else gets |
|---|---|
| Off (the default) | Handled as usual |
| Forbid | `403` with the built-in page |
| Drop | The connection aborted with no answer |

- It takes effect only in a Cloudflare mode. The review warns when it is set without one.
- It runs before anything else of the host. A forged `CF-Connecting-IP` from an untrusted peer changes nothing.
- When **every** enabled host has it on, an untrusted peer is dropped before the TLS handshake, on 443/tcp and 443/udp, whichever value is chosen.
- Plain HTTP (port 80) has no connection-stage check, on purpose: an ACME HTTP-01 validation arrives from the CA's own address when the record is DNS-only. The per-request check still refuses everything else of a locked-down host.
- A locked-down admin host is only reachable through Cloudflare. It does not cover the admin port (9443), which is a separate listener.
- The lock-out guard refuses a change that adds lockdown to an admin host for a caller who may have connected directly.

## Authenticated Origin Pulls

The strong form of lockdown: Paduraksa asks Cloudflare for a client certificate during the TLS handshake and checks it against Cloudflare's origin-pull CA. In host settings, **Authenticated Origin Pulls**.

- **Turn on the zone setting in Cloudflare too**, or no request gets through.
- It works only in Cloudflare mode. In tunnel mode `cloudflared` presents no client certificate, so it can only refuse, and the review says so.
- A host with it on is never served over HTTP/3, because Cloudflare does not connect to origins over QUIC. It also does a full TLS handshake every time (no session resumption).
- A request on a connection with no verified certificate gets `421` when the connection's SNI does not name the host, and `403` otherwise (plain HTTP included).
- The CA that ships with the build expires on 2029-11-01. To replace it before then, point `Paduraksa__Cloudflare__OriginPullCaFile` at a PEM file of the CA (public certificates only, at most 1 MiB, read once at start). A file with no certificate stops the start.

## Certificates

- **Cloudflare Origin CA** certificates can be [uploaded](tls.md#uploaded-certificates) like any other. Only Cloudflare trusts them, so keep the zone's SSL mode on Full or Full (strict).
- **ACME with DNS-01** through the Cloudflare DNS provider works whether or not the proxy mode is on. It needs a scoped API token: see [DNS-01 providers](tls.md#dns-01-providers).
- **ACME with HTTP-01** works behind an orange-cloud record, because the validation arrives from Cloudflare's ranges. With a DNS-only record it arrives from the CA.

## Ask the Simulator

The **Simulator** takes a "via Cloudflare" choice and a country, so you can see which address and country your policies would see.
