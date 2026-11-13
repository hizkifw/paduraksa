# Hosts and routes

A **host** is a set of names and what Paduraksa does with requests for them. A proxy host also has **routes**, each sending a path to an upstream. Settings (HTTPS, HSTS, access lists, limits and the rest) are layered, so you state a rule once and hosts inherit it.

## Host kinds

| Kind | In the dashboard | What it does |
|---|---|---|
| Proxy | Proxy | Forwards requests to an upstream, by path. Needs at least one route. |
| Redirect | Redirect | Answers with a redirect to a fixed target. |
| Respond | Fixed answer | Answers with a fixed status and body: a 404, a 410, "coming soon". |
| Admin | Admin | Serves the admin app itself through the proxy. See [Exposing the admin port](../operating/admin-access.md). |

Static file serving is not in v0.1.

### Names

A host's names are exact names (`app.example.com`), wildcards (`*.example.com`) or IP addresses.

- Names are normalised when saved: trimmed, lowercased, internationalised names converted to punycode.
- A name has at most 253 characters, each label at most 63. A wildcard is only the leftmost label, and there is no trailing dot.
- A name is unique across enabled hosts, ignoring case. A disabled host keeps its names without serving them, so they may repeat an enabled host's.

A request for a name no enabled host serves gets the global **unknown host** behaviour: `404` (the default), `421 Misdirected Request`, or the connection closed. Set it under **Global defaults**.

### Redirect hosts

- **Target:** an absolute `http` or `https` URL.
- **Status:** 301 (the default), 302, 303, 307 or 308.
- **Preserve path:** on by default. The request's path and query are appended to the target, so the target has no query or fragment of its own.

### Fixed-answer hosts

- **Status:** 200 to 599. 204, 205 and 304 have no body.
- **Body:** sent as UTF-8.
- **Content type:** `text/plain; charset=utf-8` unless you set one.

## Routes

A route has a **match**, a **path** and an **upstream**.

- **Match:** *Exact* matches that path only. *Prefix* matches the path and everything under it, by whole path segment (`/api` matches `/api/x`, not `/apix`).
- **Path:** starts with `/`, written decoded and canonical: no `\`, `//`, `;`, dot segments, percent-encoding, query or fragment. Matching ignores case, and two routes of a host with the same match kind cannot differ only in case.
- **Specificity:** the most specific route wins, whatever the list order. Exact routes come before prefixes, then a longer prefix before a shorter one, then the route's **Order** (lower first), then the listed order.

A request that matches no route gets a `404`.

Each route can carry a few overrides of its own: proof of work, rate limit, access list, rule set, sign-in provider and body size. See [Access control and WAF](security.md) and [Connections, limits and client addresses](connections-and-limits.md).

!!! note "Not in v0.1"
    Custom request and response headers, path rewriting (stripping or adding a prefix) and response compression are not config options in v0.1. An upstream that needs a different path or compresses its own responses has to do that itself. What Paduraksa sets on the request it sends is listed under [What the upstream receives](upstreams.md#what-the-upstream-receives).

## Settings layers

Every host setting can be set at four layers. A layer that does not set a value inherits it from the one below.

1. **Built-in defaults.**
2. **Global defaults** (Settings, *Global defaults*): what every host inherits.
3. **Profile** (Traffic, *Profiles*): a named preset such as "LAN only" or "Media server". A host names at most one.
4. **Host:** the host's own overrides.
5. **Route:** only a few settings (the ones listed above), on top of the host's.

How layers combine:

- Each setting is either set or inherited. Grouped settings, such as HSTS, merge field by field: a layer can change `max-age` and keep the `includeSubDomains` below it.
- A rate limit, access list, rule set or sign-in provider that a lower layer applies can be lifted by turning it **off** at a higher layer. The choice of policy is kept while it is off.
- Error pages merge entry by entry, and each entry is replaced whole.
- The dashboard shows where each value comes from ("inherited from Global", "from profile *LAN only*") and offers "override here" and "reset to inherited".

The settings a host can carry:

| Setting | Built-in default |
|---|---|
| HTTPS redirect (force HTTPS) | off |
| TLS: lowest version, TCP protocols, HTTP/3 | TLS 1.2, HTTP/2 and HTTP/1.1, HTTP/3 on |
| HSTS | off. Once on: one year, no subdomains, no preload |
| Access log | on |
| Request ID | sent upstream and back to the client |
| `Server` header | `Paduraksa`, with no version |
| Proof of work | off |
| Rate limit, access list, rules, sign-in provider | none |
| `robots.txt` | passed through to the upstream |
| Cloudflare: origin lockdown, Authenticated Origin Pulls | off |
| Request limits | 100 MiB body, 100 header lines, 32 KiB of headers, 8 KiB request target |
| Error pages | the built-in pages |
| Maintenance mode | off |

### HTTPS and HSTS

- **HTTPS redirect** redirects plain-HTTP requests to HTTPS before anything else. The redirect goes to the public HTTPS port (443, or `Paduraksa__Listen__PublicHttpsPort` behind a port mapping).
- A host with no certificate (**Certificate: None**) cannot force HTTPS.
- **HSTS** is sent only on HTTPS responses, and never for an IP address or a loopback name. A request for a name no host serves never carries it.
- `max-age` is 0 to 63 072 000 seconds (two years). 0 tells a browser to forget the host.
- `preload` needs `includeSubDomains` and a `max-age` of at least a year, and it is hard to undo: browsers that ship the list keep refusing plain HTTP after the header stops.
- A browser keeps the policy for `max-age`, so turn HSTS on only when every name under it works over HTTPS.

### The `Server` header

| Mode | Effect |
|---|---|
| `Paduraksa` (default) | Sends the name, never a version, replacing the upstream's header |
| `Custom` | Sends your value (1 to 128 visible ASCII characters), replacing the upstream's |
| `Upstream` | The upstream's own header passes through |
| `Remove` | Sends none, the upstream's included |

### TLS and HTTP/3 per host

- **Lowest TLS version** over TCP is 1.2 by default. QUIC is always 1.3.
- **HTTP/1.1 only** stops offering HTTP/2 over TCP.
- A connection weaker than the host allows gets `421 Misdirected Request`, so browsers retry on a connection of their own.

### Request ID

Every request gets an `X-Request-Id`. Paduraksa generates one, or keeps the one a **trusted proxy** sent. A client that is not a trusted proxy cannot choose its own. Per host, profile or globally you choose where it travels: upstream and client (the default), upstream only, client only, or nowhere.

## Maintenance mode

Maintenance mode answers every request to a host with a page instead of forwarding it.

- **Status:** 503 (the default), 200, 500, 502, 504, 403, 404 or 410.
- **Retry-After:** 0 sends none, up to a week.
- **Bypass:** a named access list. A request skips the page when the list's allow rules (addresses, and countries behind Cloudflare) admit it and its deny rules do not refuse it. A password is never asked for here.
- **Page:** the host's `maintenance` error page, or the built-in "Down for maintenance".

Use the wrench in the Hosts list to switch it on. It asks for the options first, then opens the review sheet like any other change.

Maintenance mode covers every host kind except Admin: a layer that turns it on for every host cannot lock the dashboard out. A refused client is still refused (access lists and rules run first), and plain HTTP is still redirected to HTTPS first.

## Error pages

Paduraksa answers its own refusals with built-in pages: 403, 404, 413, 414, 421, 429, 431, 502, 503 and 504. A browser (an `Accept` that includes `text/html`) gets the page and anything else gets plain text. Each page carries the request ID.

You can replace any of them, plus the maintenance page, globally, per profile or per host:

- a **message**: a heading of up to 120 characters and a sentence of up to 500, shown in the built-in layout; or
- a **document**: your own HTML, up to 64 KiB. It has no script, and only inline styles and `data:` images and fonts.

Variables are written `{status}`, `{statusText}`, `{requestId}`, `{host}` and `{cfRay}`. Any other brace is literal, so CSS needs no escaping, and `{{` writes a literal `{`.

**Replace the upstream's error pages** is a separate choice: *off* (the default), *HTML* (replace an upstream's `text/html` error bodies) or *any* (also an API's JSON errors). It never touches a WebSocket upgrade, an event stream or gRPC.

The Simulator tells you which page a browser would get and which layer it came from.

## Review, apply and undo

Every config change goes through the review sheet. Nothing applies silently, including the switch in the Hosts list.

![The review sheet: Cancel, Add to draft, Apply now.](../assets/review.png)

The sheet shows:

- the diff, grouped by object, field by field;
- the compiler's warnings (a name no certificate covers, a sign-in provider that is named but off);
- the lock-out guard: a change that would cut you off from the admin through the proxy is refused unless you force it;
- a **Simulate** check against the change.

Then you choose:

- **Apply now** makes a revision and publishes it. It goes live in milliseconds without dropping requests, and the toast offers **Undo**.
- **Add to draft** keeps it for later.

The review sheet's note (up to 1000 characters) becomes the revision's note in **History**, where each revision can be rolled back. A rollback is a revision too.

### Drafts

A **draft** collects changes made on any page (hosts, upstreams, profiles, rate limits, access lists, rules, sign-in providers, global defaults) and applies them as **one revision**. A change that spans several pages then goes live all at once or not at all.

![The draft bar under the app bar, with Review and Discard.](../assets/draft-bar.png)

- There is one draft per user. It is kept on the server, so a reload, a second tab and a restart keep it. After 30 days untouched it is dropped, and you get a notice.
- A bar under the app bar shows the change count, with **Review** and **Discard**. Lists mark objects the draft creates or changes with a *Draft* chip, and the pickers offer them.
- An object has one entry. Editing it again replaces its entry.
- Adding an entry checks it against the config as the draft would leave it, so a form shows its errors at its fields.
- **Review** opens the draft drawer: every entry with its diff, the whole set's warnings and a Simulate. **Apply** commits it.

![The draft drawer: each entry with its diff, and Apply.](../assets/draft-review.png)

- A draft applies whole or not at all. If someone changed an object it touches since you added the entry, that entry shows *Changed elsewhere* and Apply is refused until you reload it. If the config moved under you in another way, you get the fresh review to confirm again, never an automatic apply.
- Undoing the revision undoes the whole draft.
- Certificates, ACME accounts, DNS providers, users, API tokens and notification channels are not config: they act at once, as do a YAML import, a rollback and an Undo.
- An API token acts on its owner's draft: an assistant can propose, and you apply. See [MCP](../automation/mcp.md).

## Ask the Simulator

**Simulator** (Insight) answers "why was this request routed, redirected or refused?" for a made-up request, against the live config or a draft. It walks the same steps the gate does and says what each decided: client address, access list, rules, rate limit, sign-in, route, upstream and the Host sent. Simulating never spends a rate-limit token or sends a request.
