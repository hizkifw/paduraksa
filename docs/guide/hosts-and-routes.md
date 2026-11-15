# Hosts and routes

A **host** is a set of names and what Paduraksa does with requests for them. A proxy host also has **routes**, each sending a path to an upstream. Settings (HTTPS, HSTS, access lists, limits and the rest) are layered, so a rule set once is inherited by hosts.

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

- Paduraksa normalises names when they are saved: trimmed, lowercased, and internationalised names converted to punycode.
- A name has at most 253 characters, each label at most 63. A wildcard is only the leftmost label, and there is no trailing dot.
- A name is unique across enabled hosts, ignoring case. A disabled host keeps its names without serving them, so they can repeat an enabled host's.

If no enabled host serves a request's name, the global **unknown host** behaviour applies: `404` (the default), `421 Misdirected Request`, or a closed connection. It is set under **Global defaults**.

### Redirect hosts

- **Target:** an absolute `http` or `https` URL.
- **Status:** 301 (the default), 302, 303, 307 or 308.
- **Preserve path:** on by default. It appends the request's path and query to the target, so the target has no query or fragment of its own.

### Fixed-answer hosts

- **Status:** 200 to 599. 204, 205 and 304 have no body.
- **Body:** sent as UTF-8.
- **Content type:** `text/plain; charset=utf-8` unless you set one.

## Routes

A route has a **match**, a **path** and an **upstream**.

- **Match:** *Exact* matches that path only. *Prefix* matches the path and everything under it, by whole path segment (`/api` matches `/api/x`, not `/apix`).
- **Path:** starts with `/`, written decoded and canonical: no `\`, `//`, `;`, dot segments, percent-encoding, query or fragment. Matching ignores case, and two routes of a host with the same match kind cannot differ only in case.
- **Specificity:** the most specific route wins, regardless of list order. Exact routes come before prefixes, then a longer prefix before a shorter one, then the route's **Order** (lower first), then the listed order.

A request that matches no route gets a `404`.

A route can override proof of work, rate limit, access list, rule set, sign-in provider, body size and custom headers, and can rewrite the path it forwards (below). See [Access control and WAF](security.md) and [Connections, limits and client addresses](connections-and-limits.md).

### Rewriting the path

A route can forward a different path from the one the client asked for. Use it to put an app that expects to live at `/` under a path of a shared host, or to reach an API's versioned path. In the dashboard, the settings are in the route's menu, **Route settings**: **Path sent upstream** and **Prefix to add**.

- **Strip the prefix:** removes the route's own path. On `/grafana`, `/grafana/d/x` reaches Grafana as `/d/x`, and `/grafana` as `/`. It is not available on a route whose path is `/`.
- **Add a prefix:** puts a fixed path in front. With both on `/api` and `/v2`, `/api/users` reaches the API as `/v2/users`. The prefix starts with `/` and has no trailing `/`.
- Paduraksa forwards the path as the client sent it, without decoding or re-encoding (`%2F` stays `%2F`), and leaves the query untouched.
- A stripping route sends the removed prefix to the upstream in `X-Forwarded-Prefix` (`/grafana`), so an app that builds its own links can add it back.
- **Redirects from the upstream** are mapped back. A `Location` of `/login` reaches the client as `/grafana/login`. A `Location` that already starts with `/grafana` is left alone, for an app that reads `X-Forwarded-Prefix`. An absolute `Location` on the upstream's own name gets the client's scheme and host, so an internal address never reaches the browser.
- **Turn the mapping off** (**Redirects from the upstream**: *As the upstream sends them*, `rewriteLocation: false`) if the upstream redirects to other routes of the same host. Otherwise oauth2-proxy's `/oauth2/sign_in` becomes `/grafana/oauth2/sign_in` and gives a 404.
- **A path that can't be rewritten safely gets a `404`:**
    - a spelling of the prefix other than the route's own (`//grafana`, `/%67rafana`);
    - on any rewritten route, a dot segment in any spelling (`..`, `%2e%2e`, `.%2E`, `..;`);
    - on any rewritten route, an escape a loose decoder could misread (`%25`, `%c0`, `%u`, control characters).

```yaml
upstreams:
  - name: grafana
    destinations:
      - address: http://grafana:3000
  - name: api
    destinations:
      - address: http://api:8080
hosts:
  - names: [home.example.com]
    routes:
      - path: /grafana
        upstream: grafana
        rewrite: {stripPrefix: true}
      - path: /api
        upstream: api
        rewrite: {stripPrefix: true, addPrefix: /v2, rewriteLocation: false}
```

## Custom headers

Rules set, add or remove a header on the request the upstream receives or the response the client receives. They are a host setting, so the global defaults, a profile, a host and a route can each carry some. In the dashboard, they are **Request headers** and **Response headers**, under *Headers and logging*.

- **Set** replaces every value the header had, **add** appends a value, and **remove** deletes the header, whoever set it.
- **Layers merge by header name:** a rule for a name replaces the lower layer's rule for that name, and the other rules are kept. A route's rules override its host's the same way. The form lists what a layer inherits and marks the rules it replaces.
- There are at most 32 rules per direction, per layer and after the layers merge. A name has up to 64 characters, and a value up to 4096 visible ASCII characters, with no line breaks and no leading or trailing space. Values are sent as written, with no placeholders.
- **Response rules** apply to the upstream's responses and to Paduraksa's own pages for the host (error pages, redirects, maintenance), after the built-in headers, so a rule for `Server` wins over the **Server header** setting. A page's own `Content-Security-Policy` stays, whatever a rule says.
- **Refused names:** Paduraksa sets these itself, or they frame the message.
    - Both directions: `Connection`, `Content-Encoding`, `Content-Length`, `Expect`, `Keep-Alive`, `Proxy-Authenticate`, `Proxy-Authorization`, `Proxy-Connection`, `TE`, `Trailer`, `Transfer-Encoding` and `Upgrade`.
    - On the request: `Host`, every `X-Forwarded-*`, `Forwarded`, `X-Real-IP`, `X-Request-Id`, every `cf-*`, `True-Client-IP` and `X-Paduraksa-Internal`.
    - On the response: `Strict-Transport-Security`, `Alt-Svc`, `X-Request-Id` and `Location`.
    - A request rule can't set a header the host's sign-in provider vouches for, and a response `Set-Cookie` rule can only add.
- **Values are config, not secrets:** a static `Authorization` to an upstream is stored in the config and its YAML export like any setting. The Simulator redacts such values (`Authorization`, `Cookie`, names ending in `-Key`, `-Token` or `-Secret`).

```yaml
profiles:
  - name: public
    settings:
      headers:
        response:
          - {name: X-Frame-Options, operation: set, value: DENY}
          - {name: X-Powered-By, operation: remove}
hosts:
  - names: [app.example.com]
    profile: public
    settings:
      headers:
        request:
          - {name: X-Tenant, operation: set, value: homelab}
    routes:
      - upstream: app
      - path: /embed
        upstream: app
        settings:
          headers:
            response:
              - {name: X-Frame-Options, operation: remove}   # replaces the profile's DENY for this route
```

## Compression

A host can compress the upstream's responses with Brotli or gzip, whichever the client's `Accept-Encoding` prefers. It is off by default (see [BREACH](https://www.breachattack.com/)). **Compress responses**, under *Compression*, turns it on globally, per profile or per host.

- It compresses text (HTML, CSS, JavaScript, JSON, XML, SVG, plain text, WebAssembly, fonts) of at least **Smallest body** (1 KiB by default, 0 to 1 MiB), including a body of unknown length.
- It leaves alone a response the upstream already compressed, a range, an event stream, a `Cache-Control: no-transform` response, a `HEAD`, a WebSocket and Paduraksa's own pages.
- A compressed response gets `Vary: Accept-Encoding` and a weak `ETag`, and loses `Content-Length` and `Accept-Ranges`.
- The access log and the metrics count the compressed bytes.

```yaml
hosts:
  - names: [docs.example.com]
    settings:
      compression: {enabled: true, minimumSize: 512}
    routes:
      - upstream: docs
```

## Settings layers

A host setting comes from the first layer that sets it, starting from the top. A layer that does not set a value inherits it from the one below.

1. **Built-in defaults.**
2. **Global defaults** (Settings, *Global defaults*): what every host inherits.
3. **Profile** (Traffic, *Profiles*): a named preset such as "LAN only" or "Media server". A host names at most one.
4. **Host:** the host's own overrides.
5. **Route:** only a few settings (the ones listed above, and custom headers), on top of the host's.

How layers combine:

- Each setting is either set or inherited. Grouped settings, such as HSTS, merge field by field: a layer can change `max-age` and keep the `includeSubDomains` below it.
- A higher layer can lift a rate limit, access list, rule set or sign-in provider that a lower layer applies by turning it **off**. The choice of policy is kept while it is off.
- Error pages merge entry by entry, and each entry is replaced whole. Custom headers merge by header name.
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
| Custom headers | none |
| Compression | off. Once on: from 1 KiB |

### HTTPS and HSTS

- **HTTPS redirect** redirects plain-HTTP requests to HTTPS before anything else, to the public HTTPS port (443, or `Paduraksa__Listen__PublicHttpsPort` behind a port mapping).
- A host with no certificate (**Certificate: None**) cannot force HTTPS.
- **HSTS** is sent only on HTTPS responses, never for an IP address or a loopback name, and never on the answer to a name no host serves.
- `max-age` is 0 to 63 072 000 seconds (two years). 0 tells a browser to forget the host.
- `preload` needs `includeSubDomains` and a `max-age` of at least a year, and it is hard to undo: browsers that ship the list keep refusing plain HTTP after the header stops.
- A browser keeps the policy for `max-age`, so turn HSTS on only if every name under it works over HTTPS.

### The `Server` header

| Mode | Effect |
|---|---|
| `Paduraksa` (default) | Sends the name without a version, replacing the upstream's header |
| `Custom` | Sends your value (1 to 128 visible ASCII characters), replacing the upstream's |
| `Upstream` | Passes the upstream's own header through |
| `Remove` | Sends no header, and removes the upstream's |

### TLS and HTTP/3 per host

- **Lowest TLS version** over TCP is 1.2 by default. QUIC is always 1.3.
- **HTTP/1.1 only** stops offering HTTP/2 over TCP.
- A connection weaker than the host allows gets `421 Misdirected Request`, so browsers retry on a new connection.

### Request ID

Every request gets an `X-Request-Id`. Paduraksa generates one, or keeps the one a **trusted proxy** sent. Other clients cannot choose their own. Per host, profile or globally, you choose where it travels: upstream and client (the default), upstream only, client only, or nowhere.

## Maintenance mode

Maintenance mode answers every request to a host with a page instead of forwarding it.

- **Status:** 503 (the default), 200, 500, 502, 504, 403, 404 or 410.
- **Retry-After:** 0 sends none, up to a week.
- **Bypass:** a named access list. A request skips the page if the list's allow rules (addresses, and countries behind Cloudflare) admit it and its deny rules do not refuse it. It never asks for a password.
- **Page:** the host's `maintenance` error page, or the built-in "Down for maintenance".

The wrench in the Hosts list switches it on. It asks for the options first, then opens the review sheet like any other change.

Maintenance mode covers every host kind except Admin, so a layer that turns it on for every host cannot lock the dashboard out. Access lists and rules run first, so a refused client is still refused, and plain HTTP is still redirected to HTTPS first.

## Error pages

Paduraksa answers its own refusals with built-in pages: 403, 404, 413, 414, 421, 429, 431, 502, 503 and 504. A browser (an `Accept` that includes `text/html`) gets the page, and any other client gets plain text. Each page carries the request ID.

You can replace any of them, plus the maintenance page, globally, per profile or per host:

- a **message**: a heading of up to 120 characters and a sentence of up to 500, shown in the built-in layout; or
- a **document**: your own HTML, up to 64 KiB, with no script and only inline styles and `data:` images and fonts.

Variables are written `{status}`, `{statusText}`, `{requestId}`, `{host}` and `{cfRay}`. Any other brace is literal, so CSS needs no escaping, and `{{` writes a literal `{`.

**Replace the upstream's error pages** is a separate choice: *off* (the default), *HTML* (replace an upstream's `text/html` error bodies) or *any* (also an API's JSON errors). It never changes a WebSocket upgrade, an event stream or gRPC.

The Simulator shows which page a browser would get and which layer it came from.

## Review, apply and undo

Every config change goes through the review sheet, including the switch in the Hosts list. Nothing applies silently.

![The review sheet: Cancel, Add to draft, Apply now.](../assets/review.png)

The sheet shows:

- the diff, grouped by object, field by field;
- the compiler's warnings (a name that no certificate covers, a sign-in provider that is named but off);
- the lock-out guard, which refuses a change that would cut you off from the admin through the proxy unless you force it;
- a **Simulate** check against the change.

Then you choose:

- **Apply now** creates a revision and publishes it. It goes live within milliseconds without dropping requests, and the toast offers **Undo**.
- **Add to draft** keeps it for later.

The review sheet's note (up to 1000 characters) becomes the revision's note in **History**, where each revision can be rolled back. A rollback is a revision too.

### Drafts

A **draft** collects changes made on any page (hosts, upstreams, profiles, rate limits, access lists, rules, sign-in providers, global defaults) and applies them as **one revision**. A change that spans several pages goes live all at once or not at all.

![The draft bar under the app bar, with Review and Discard.](../assets/draft-bar.png)

- Each user has one draft, kept on the server, so it survives a reload, a second tab and a restart. After 30 days untouched it is dropped, with a notice.
- A bar under the app bar shows the change count, with **Review** and **Discard**. Lists mark objects the draft creates or changes with a *Draft* chip, and the pickers offer them.
- An object has one entry. Editing it again replaces its entry.
- A form checks an entry against the config as the draft would leave it, and shows errors at the fields.
- **Review** opens the draft drawer: every entry with its diff, the whole set's warnings and a Simulate. **Apply** commits it.

![The draft drawer: each entry with its diff, and Apply.](../assets/draft-review.png)

- A draft applies whole or not at all.
    - If someone changed an object an entry touches, that entry shows *Changed elsewhere*, and Apply is refused until you reload it.
    - If the config changed in another way, you get the fresh review to confirm. The draft never applies on its own.
- Undoing the revision undoes the whole draft.
- Certificates, ACME accounts, DNS providers, users, API tokens and notification channels are not config, so they act at once. So do a YAML import, a rollback and an Undo.
- An API token acts on its owner's draft: an assistant can propose, and you apply. See [MCP](../automation/mcp.md).

## Ask the Simulator

**Simulator** (Insight) shows why a made-up request is routed, redirected or refused, against the live config, an older revision, the change you are reviewing or your open draft ("My draft"). It walks the same steps as the gate and reports what each decided: client address, access list, rules, rate limit, sign-in, route, upstream and the Host sent. Simulating never spends a rate-limit token or sends a request.
