# Upstreams

An **upstream** is a named set of destinations that routes send requests to. Several routes, on one host or on different hosts, can share an upstream. Manage them under **Upstreams** (Traffic), or create one from a route's **Upstream** picker with **New upstream...**.

## Destinations

A destination is an address like `http://jellyfin:8096` or `https://nas.lan:8443`.

- It is an `http://` or `https://` URL, with a port if it is not the scheme's default, and optionally a base path.
- It has no query, fragment or credentials.
- At least one destination must be enabled. A disabled destination stays in the config and gets no traffic.
- **Check that it answers**, in the form, opens a TCP connection to the destination's host and port, waits up to 3 seconds, and closes it. It sends nothing.
- Paduraksa looks names up again every 60 seconds, so a restarted container with a new address is picked up quickly. It marks a destination that fails to connect as unhealthy and looks it up again on the retry.

## Load balancing

With more than one destination, **Load balancing** sets how requests are spread:

| Policy | Sends the request to |
|---|---|
| Power of two choices (the default) | The less busy of two destinations picked at random |
| Round robin | The next destination in turn |
| Random | A random destination |
| Least requests | The destination with the fewest requests in flight |
| First alphabetical | The first healthy destination by address, so the others are standbys |

**Sticky sessions (cookie)** keeps a visitor on the destination that served them first.

**Connections per destination** limits the connections the proxy opens to one destination. It is empty (no limit) by default.

## Health checks

Both are off until you turn them on. While another destination is healthy, a destination that is out of rotation gets no requests.

- **Active:** every **15 seconds**, Paduraksa sends a `GET` to the path you give (it must start with `/`, with no query or fragment) on each destination, and waits **5 seconds** for an answer. Both times are settable.
- **Passive:** Paduraksa takes a destination with too many connection failures out of rotation for a while. **Back in rotation after** sets how long, and an empty field uses the default.

The Upstreams page, the upstream form, the Hosts list and the Overview show each destination's state (*Healthy*, *Unhealthy*, *Checking* or *Not health-checked*) and when it last changed. If a host's upstream is down, a chip appears beside the host's name. For a notification, add a [notification channel](observability.md#notifications): an upstream going down or up is an event, per upstream and per destination.

## Timeouts, WebSockets and event streams

WebSockets and event streams need no setting. Idle connections stay open for an hour. Each upstream has two timeouts, under **Advanced**:

- **Connect timeout:** how long a connect (TCP and TLS) to a destination may take. The default is 10 seconds, and the range is 100 ms to 5 minutes.
- **Idle timeout:** how long a request, an event stream or a WebSocket may go with no data in either direction before the proxy closes it. The default is 1 hour, the range is 1 second to 24 hours, and `0` means never.

A ping or heartbeat from either side counts as data, and so do the arriving response headers.

- A shorter idle timeout sheds connections whose peer has silently gone away. Each upgraded connection holds one of the [`MaxUpgradedConnections`](../operating/settings.md#connection-and-timeout-limits) slots.
- A longer one, or `0`, suits a session that sits silent for more than an hour.
- nginx's `proxy_read_timeout` is 60 seconds and cuts idle WebSockets, so a setup moved from nginx may carry a ping it no longer needs.
- A change applies to connections made after it.
- Either timeout cuts a request with a 504 and the gateway-timeout page.

## HTTPS upstreams

Paduraksa verifies an `https://` destination the way a browser would: a chain to the system's trusted roots, issued for the name the client asked for.

Homelab gear with a self-signed certificate (a NAS, a router, Proxmox) fails that check. The error log shows `The remote certificate is invalid ... RemoteCertificateNameMismatch, RemoteCertificateChainErrors`, and the client gets a 502.

Set the upstream's **HTTPS certificate trust** (Advanced):

- **System** (the default): verifies against the system roots and the name sent.
- **Pinned fingerprint:** accepts exactly the listed SHA-256 fingerprints, whatever their name, chain or dates.
    - **Fetch from destinations** shows the certificate each `https://` destination presents (fingerprint, name, issuer, expiry), and **Pin** adds it. Check it is the device you meant before pinning.
    - Or paste a SHA-256 fingerprint yourself, colons allowed, for example from `openssl s_client -connect nas.lan:8443 </dev/null | openssl x509 -noout -fingerprint -sha256`.
    - When the device renews its certificate, fetch and pin the new one.
- **Any certificate:** no verification. Traffic is encrypted, but anyone on the path can read and change it. The review warns about it.

An upstream that also speaks plain HTTP needs none of this: use an `http://` destination.

## Host header modes

The upstream's **Host header sent to the upstream** setting controls the `Host` header it receives:

| Mode | Sends | Use it for |
|---|---|---|
| The client's (default) | The `Host` the client sent | Almost everything: name-based virtual hosts, absolute URLs, trusted-domain checks |
| The destination's address | The destination's own host and port | An upstream that answers only to its own address. An upstream with several destinations sends each one its own |
| A fixed name | A name you set, such as `app.internal` or `app.internal:8080` | A service that answers only to its own name |

The fixed name is a host name or IP address, with an optional port from 1 to 65535 (put an IPv6 literal in brackets).

On an HTTPS destination, the TLS server name (SNI) follows the Host sent, and a system-trusted certificate is checked against it. A pinned fingerprint is not tied to a name.

With a fixed name, active health checks send that name too, so an upstream that answers only to it still passes them.

Two routes that need different Host headers need two upstreams, because the setting belongs to the upstream, not the route.

## What the upstream receives

Paduraksa builds these request headers itself and never forwards the client's values:

| Header | Value |
|---|---|
| `X-Forwarded-For`, `X-Real-IP` | Exactly one address: the resolved client. No chain is kept or appended |
| `X-Forwarded-Proto` | The trusted request scheme, the one the HTTPS redirect uses |
| `X-Forwarded-Host` | The `Host` or `:authority` as received, port included |
| `Host` | As the upstream's Host header mode says |
| `Forwarded`, `X-Forwarded-Prefix`, `X-Forwarded-Port` | Removed. A route that strips its prefix sends its own `X-Forwarded-Prefix`: the path it took off ([Rewriting the path](hosts-and-routes.md#rewriting-the-path)) |
| `cf-*` and `True-Client-IP` | Removed, unless the request came from a trusted Cloudflare peer |
| `X-Request-Id` | Sent when the host's request ID setting says so |

Also:

- Paduraksa removes the admin session cookie (`__Host-paduraksa-session`) from proxied requests, and an upstream cannot set it. A browser signed in to the admin on the same name never leaks its session to a proxied app.
- Paduraksa removes a sign-in provider's identity headers from the client's request and sets them from the provider's answer. See [Access control and WAF](security.md#sign-in-with-your-identity-provider).
- Upstream response headers pass through, except `Server` and HSTS, which follow the host's settings. The upstream's own HSTS header passes only while the host's HSTS is off.
