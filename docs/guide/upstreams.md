# Upstreams

An **upstream** is a named set of destinations that routes send requests to. Several routes, on one host or on different hosts, can use the same upstream. Manage them under **Upstreams** (Traffic), or create one from a route's **Upstream** picker with **New upstream...**.

## Destinations

A destination is an address like `http://jellyfin:8096` or `https://nas.lan:8443`.

- An `http://` or `https://` URL with a port if it is not the scheme's default, and optionally a base path.
- No query, fragment or credentials.
- At least one destination must be enabled. A disabled destination stays in the config and gets no traffic.
- Each destination can be checked from the form (**Check that it answers**): Paduraksa opens a TCP connection to its host and port within 3 seconds, closes it and sends nothing.
- Paduraksa re-resolves names by recycling pooled connections every 60 seconds, so a restarted container with a new address is picked up quickly. A destination that fails to connect is also marked unhealthy, which forces new connections and a fresh DNS lookup on the retry.

## Load balancing

With more than one destination you choose how requests are spread (**Load balancing**):

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

Both are off until you turn them on. A destination that is out of rotation is not sent requests while another one is healthy.

- **Active:** Paduraksa sends a `GET` to the path you give (it must start with `/`, no query or fragment) on each destination every **15 seconds**, and waits **5 seconds** for an answer. Both are settable.
- **Passive:** a destination with too many transport failures is taken out for a while. **Back in rotation after** sets how long; empty is YARP's default.

The Upstreams page, the upstream form, the Hosts list and the Overview show each destination's state (*Healthy*, *Unhealthy*, *Checking* or *Not health-checked*) and when it last changed. A host whose upstream is down gets a chip beside its name. To be told, add a [notification channel](observability.md#notifications): an upstream going down or up is an event, per upstream and per destination.

## Timeouts, WebSockets and event streams

WebSockets and event streams work without any setting. An idle one is not cut for an hour. Each upstream has two timeouts, under **Advanced**:

- **Connect timeout:** how long a connect (TCP and TLS) to a destination may take. The default is 10 seconds, and the range is 100 ms to 5 minutes.
- **Idle timeout:** how long a request, an event stream or a WebSocket may go with no data in either direction before the proxy closes it. The default is 1 hour, the range is 1 second to 24 hours, and `0` means never.

A ping or heartbeat from either side counts as data, and so do the response headers arriving.

- Set the idle timeout shorter to shed connections whose peer has silently gone away. Each upgraded connection holds one of the [`MaxUpgradedConnections`](../operating/settings.md#connection-and-timeout-limits) slots.
- Set it longer, or `0`, for a session that is meant to sit silent for more than an hour.
- nginx's own `proxy_read_timeout` is 60 seconds, which cuts idle WebSockets, so a setup moved from nginx may carry a ping it no longer needs.
- A change applies to connections made after it.
- A request cut by either timeout gets a 504 with the gateway-timeout page.

## HTTPS upstreams

An `https://` destination is verified like a browser would: a chain to the system's trusted roots, issued for the name the client asked for.

Homelab gear with a self-signed certificate (a NAS, a router, Proxmox) fails that. The error log shows `The remote certificate is invalid ... RemoteCertificateNameMismatch, RemoteCertificateChainErrors`, and the client gets a 502.

Set the upstream's **HTTPS certificate trust** (Advanced):

- **System** (the default): verify against the system roots and the name sent.
- **Pinned fingerprint:** accept exactly the listed SHA-256 fingerprints, whatever their name, chain or dates.
    - **Fetch from destinations** shows the certificate each `https://` destination presents (fingerprint, name, issuer, expiry), and **Pin** adds it. Check it is the device you meant before pinning.
    - Or paste a SHA-256 fingerprint yourself, colons allowed, for example from `openssl s_client -connect nas.lan:8443 </dev/null | openssl x509 -noout -fingerprint -sha256`.
    - When the device renews its certificate, fetch and pin the new one.
- **Any certificate:** no verification. Traffic is still encrypted, but anyone on the path can read and change it. The review warns about it.

An upstream that also speaks plain HTTP needs none of this: use an `http://` destination.

## Host header modes

The `Host` header the upstream receives is a setting of the upstream, **Host header sent to the upstream**:

| Mode | Sends | Use it for |
|---|---|---|
| The client's (default) | The `Host` the client sent | Almost everything: name-based virtual hosts, absolute URLs, trusted-domain checks |
| The destination's address | The destination's own host and port | An upstream that only answers to its own address. An upstream with several destinations sends each its own |
| A fixed name | A name you set, such as `app.internal` or `app.internal:8080` | A service that answers only to its own name |

The fixed name is a host name or IP address, with an optional port from 1 to 65535 (an IPv6 literal goes in brackets).

On an HTTPS destination the TLS server name (SNI) follows the Host sent, and a system-trusted certificate is checked against it. A pinned fingerprint is not tied to a name. With a fixed name, active health probes send that name too, so an upstream that only answers to it does not fail them all.

Two routes that need different Host headers need two upstreams: the setting belongs to the upstream, not the route.

## What the upstream receives

Paduraksa builds these request headers itself and never forwards what the client put in them:

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

- The admin session cookie (`__Host-paduraksa-session`) is removed from proxied requests, and an upstream cannot set it. A browser that signed in to the admin on the same name never leaks its session to a proxied app.
- A sign-in provider's identity headers are removed from the client's request and set from the provider's answer. See [Access control and WAF](security.md#sign-in-with-your-identity-provider).
- Upstream response headers pass through. The exceptions are `Server` and HSTS, which follow the host's settings, so the upstream's own HSTS header passes only while the host's HSTS is off.
