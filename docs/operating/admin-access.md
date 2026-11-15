# Exposing the admin port

The admin port (9443) holds the dashboard, the REST API and, if you turn it on, MCP. It is HTTPS only. Sign-in is throttled, locked out after repeated failures and protected by an optional second factor. Even so, it is built for a trusted network, not the internet.

The compose file publishes it as `127.0.0.1:9443:9443`. Docker puts published ports ahead of the machine's firewall rules, so `ufw` does not see them, and an unqualified `9443:9443` would answer on every interface the machine has.

From the safest to the most open:

1. **Loopback and an SSH tunnel** (the default). Run `ssh -L 9443:127.0.0.1:9443 you@server`, then open `https://localhost:9443/`.
2. **The admin as a host behind the proxy**, with its own name, certificate and an access list in front. See [The admin as a host](#the-admin-as-a-host). The port itself stays on loopback.
3. **The LAN address.** Publish `"192.168.1.10:9443:9443"` and set `Paduraksa__Listen__AdminAllowedSources` to your management subnet. Loopback is not implied, so add `127.0.0.1/32` for the container healthcheck (`paduraksa doctor --liveness`) and an SSH tunnel. Test it from a machine that should be refused.
4. **A public address**, only behind a firewall you control (not Docker's own rules), and with a second factor for every Owner and Admin. **Users** shows who has none. Nothing forces one.

A plain `http://<host>:9443/` GET is answered with a redirect to the same URL over `https://`, so an old bookmark still works. No page is ever served over plain HTTP.

For a headless setup reached through an SSH tunnel, `Paduraksa__Listen__AdminLoopbackHttp=<port>` opens a plain-HTTP listener. It binds loopback only. `0` (the default) turns it off.

## The admin as a host

A host of kind **Admin** serves the admin app (dashboard, API and `/mcp`) under a name of your choice. The proxy's certificate, access lists, rate limits and sign-in sit in front of it. It reaches the admin through a socket inside the process (`<data dir>/run/admin.sock`), so the admin port can stay on loopback. `Paduraksa__Listen__AdminInternal` (on by default) controls that listener.

**Never forward a public name to `127.0.0.1:9443` yourself.** Every client would look like loopback, share one throttle and pass an access list that allows loopback. The admin host is the supported way. Put an IP access list on it.

- There is at most one admin host. It has no routes, redirect or answer of its own. Its certificate cannot be *None*, and HTTPS is forced.
- The admin sees the real client address, scheme and host, so its sign-in throttle and audit log record the visitor, not the proxy.
- `Paduraksa__Listen__AdminAllowedSources` applies to the forwarded client address too.
- **First-run setup is refused through the proxy.** Create the Owner on the admin port or the loopback listener.
- The admin session cookie is bound to the host name, not the port. The proxy removes it from every request forwarded to another upstream, and an upstream cannot set it.
- Maintenance mode and replaced upstream error pages never apply to an admin host, so a global setting cannot lock you out of the dashboard.

**Lock-out guard.** A change made through the proxied admin is refused with `422` and code `host.admin.lockout` if it would cut that session off. Examples are deleting, disabling or renaming the admin host, an access list or certificate that no longer admits you, and a trusted-proxy change. Make the change from the admin port instead, or pass `?force=true` to the API. A change made on the normal admin port, the loopback listener or the CLI is never blocked.

### Trust the local CA root

The admin certificate comes from the local CA. The root is offered without sign-in, before setup, so you can trust it first. See [Getting started](../guide/getting-started.md#3-trust-the-local-ca-root-optional) and [The local CA](../guide/tls.md#the-local-ca).

To add names the admin certificate covers, such as an admin host name, set `Paduraksa__Admin__CertificateNames`. It always covers `localhost`, the loopback addresses, `AdminBind` and the machine's host name.
