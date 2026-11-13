# Exposing the admin port

The admin port (9443) holds the dashboard, the REST API and, if you turn it on, MCP. It is HTTPS only and its sign-in is throttled, locked
out after repeated failures and protected by an optional second factor, but it is built for a trusted network, not the internet. The compose
file therefore publishes it as `127.0.0.1:9443:9443`: Docker puts published ports ahead of the host's firewall rules (`ufw` does not see them),
so an unqualified `9443:9443` would answer on every interface the host has.

From the safest to the most open:

1. **Loopback and an SSH tunnel** (the default): `ssh -L 9443:127.0.0.1:9443 you@server`, then `https://localhost:9443/`.
2. **The admin as a host behind the proxy**, with its own name, certificate and an access list in front (`Hosts`, kind Admin; see [The admin as a host](#the-admin-as-a-host)).
   It reaches the admin over a socket inside the process, so the port itself stays on loopback. Put an IP access list on that host, and
   never forward a public name to `127.0.0.1:9443` yourself: every client would look like loopback, share one throttle and pass an allowlist
   that lists loopback.
3. **The LAN address**: publish `"192.168.1.10:9443:9443"` and set `Paduraksa__Listen__AdminAllowedSources` to your management subnet
   (loopback is not implied, add `127.0.0.1/32` for the container healthcheck; try it from a machine that should be refused).
4. A public address, only behind a firewall that you control (not Docker's own rules) and with a second factor for every Owner and Admin.
   Users shows who has none, and nothing forces one.

## The admin port is HTTPS only

The admin port is HTTPS only. A plain `http://<host>:9443/` GET is answered with a redirect to the same URL over
`https://` (no page is ever served over plain HTTP), so a mistyped or old bookmark still lands on the panel. If you set
`Paduraksa__Listen__AdminAllowedSources` (addresses/CIDRs), loopback is **not** implied - add `127.0.0.1/32` if you
also want the container healthcheck (`paduraksa doctor --liveness`) and an SSH tunnel to work.

An opt-in plain-HTTP listener for headless setups reached through an SSH tunnel is `Paduraksa__Listen__AdminLoopbackHttp=<port>`. It binds loopback only, with no setting to bind anything else, and `0` (the default) turns it off.

## The admin as a host

A host of kind **Admin** serves the admin app (dashboard, API and `/mcp`) under a name of your choice, with the proxy's certificate, access lists, rate limits and sign-in in front of it. It reaches the admin through a socket inside the process (`<data dir>/run/admin.sock`, mode 0600), so the admin port can stay on loopback. `Paduraksa__Listen__AdminInternal` (on by default) controls that listener.

- There is at most one admin host. It has no routes, redirect or answer of its own, and its certificate cannot be *None*. HTTPS is forced.
- The admin sees the real client address, scheme and host that the proxy resolved, so its sign-in throttle and audit log record the visitor and not the proxy. It trusts those headers only on the internal listener, and only with a secret the proxy adds.
- `Paduraksa__Listen__AdminAllowedSources` is applied there too, to the forwarded client address.
- **First-run setup is refused through the proxy.** Create the Owner on the admin port or the loopback listener.
- The admin session cookie is bound to the host name, not the port. The proxy removes it from every request forwarded to any other upstream, and an upstream cannot set it.
- Maintenance mode and replacing upstream errors never apply to an admin host, so you cannot lock the dashboard out with a global setting.
- **Never forward a public name to `127.0.0.1:9443` yourself.** Every client would look like loopback, share one throttle and pass an allowlist that lists loopback. The admin host is the supported way.

**Lock-out guard.** A change made through the proxied admin that would cut that session off (deleting or disabling the admin host, renaming it, an access list or certificate that no longer admits you, a trusted-proxy change) is refused with `422` and code `host.admin.lockout`. Change it from the admin port instead, or pass `?force=true` to the API to override. A change made on the normal admin port, the loopback listener or the CLI is never blocked.

### Trust the local CA root

Paduraksa issues the admin certificate from a local CA. The root is offered unauthenticated, before setup, precisely so you can trust it
first. See [Getting started](../guide/getting-started.md#3-trust-the-local-ca-root-optional) and [The local CA](../guide/tls.md#the-local-ca).
To add names that the admin certificate covers (an admin host name, say), set `Paduraksa__Admin__CertificateNames`; the process always covers
`localhost`, the loopback addresses, `AdminBind` and its host name.
