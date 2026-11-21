# Access control and WAF

Paduraksa's protections are named objects. Define one once, then attach it to hosts, profiles, the global defaults or single routes.

| Block | Where | What it does |
|---|---|---|
| [Access lists](#access-lists) | Security, *Access lists* | Allow or deny addresses and countries, and ask for a password |
| [Rule sets](#rule-sets) | Security, *Rule sets* | Refuse, drop or allow by path, method and user agent |
| [Rate limits](#rate-limits) | Security, *Rate limits* | Token buckets that answer `429` |
| [Auth providers](#sign-in-with-your-identity-provider) | Security, *Auth providers* | Forward-auth to authentik, Authelia, oauth2-proxy or your own endpoint |
| [Browser check](#browser-check-proof-of-work) | A host setting | A proof of work a plain scraper does not pass |
| [robots.txt](#robotstxt) | A host setting | A managed file for crawlers |
| [Honeypot](#honeypot) | A host setting, and a rule action | A trap for crawlers that ignore `robots.txt` |

Connection floods, slow clients, request sizes and client addresses are on [Connections, limits and client addresses](connections-and-limits.md). Cloudflare's origin lockdown is on [Behind Cloudflare](cloudflare.md).

## What runs first

For a request to a proxy host, the gate runs in this order, and stops at the first refusal:

1. The connection stage, before TLS: the per-address cap and the deny list.
2. The host's address and country rules. A denied address is dropped or refused here and learns nothing about the host.
3. Request limits (`414`, `431`), then the HTTPS redirect, then `robots.txt`, then the honeypot, then the answers of Redirect and Respond hosts.
4. Rule sets, then the route's access list (addresses, then passwords), then the rate limit, then sign-in, then the browser check.

- A rule set runs before the access list that asks for passwords, so a refused request costs no password check or rate-limit token.
- The rate limit runs before the browser check, so a client over budget gets a `429`, not a challenge page.
- Rules, rate limits and the browser check never apply to `GET` and `HEAD /robots.txt`, because a `4xx` there would tell a crawler it may go anywhere.

## Attaching a block

A host, profile or the global defaults has one setting for each: **Access list**, **Rules**, **Sign-in** and **Rate limit**. Each picks *inherit*, *none* or a named object, and **New ...** creates one without leaving the form.

- A route can inherit its host's choice, name another, or turn it off for its own paths. A route that serves the sign-in provider's own pages must turn sign-in off.
- Paduraksa refuses to delete an object that a host, profile, default or route still names.
- Redirect and Respond hosts have no routes, so only the host's access list and rules apply to them. A rate limit and the browser check work on routes and never apply to these hosts.

## Access lists

An access list combines addresses, countries and basic-auth users.

![The access list drawer: allowed and denied addresses, countries, and users.](../assets/access-list.png)

### Addresses

- **Allowed** and **denied** addresses, one per line, as single addresses or CIDR ranges (IPv4 and IPv6).
- A denied address is always refused.
- A non-empty allow list admits only its addresses.
- **Drop** closes the connection without an answer, instead of sending a `403`, where the address decided.
- The address judged is the client's: see [Connections, limits and client addresses](connections-and-limits.md#client-addresses).

### Countries

Countries come from Cloudflare's `CF-IPCountry` header, trusted only from a Cloudflare peer. **Paduraksa v0.1 has no local country database.**

Outside Cloudflare's Proxy and Tunnel modes every country is unknown, so a country allow list refuses everyone. The review warns if a host uses country rules and the client address mode is not Cloudflare.

- Two letters are a country (`DE`). `T1` is Tor, kept separate so you can block it. `XX`, a missing header or a repeated header means *unknown*.
- Deny wins. If the list has a country allow list, an unknown country is refused unless `XX` is in it.
- If a list has both an address allow list and a country allow list, a request must match **both**.

### Users and passwords

The list can ask for HTTP basic auth.

- A username is case-sensitive, with 1 to 64 visible ASCII characters and no `:`. A list holds at most 1000 users.
- A password is set in the drawer. An empty box keeps the stored one. **Generate** fills in 20 random letters and digits and shows them once.
- The vault stores the password hashed. The config, its revisions, exports, the audit log and API responses carry only a credential ID, never a password or a hash. A config export has no passwords, so restore a full [backup](../operating/backups.md) to keep them.
- A user without a password cannot sign in, and the review warns about it. A rollback or an import restores the users a revision named, with the password the vault holds at that moment.
- **Realm** is the name in the browser's prompt, and defaults to the list's name.
- A wrong or missing password gets a `401` with `WWW-Authenticate: Basic`, and response timing does not reveal which usernames exist.
- Paduraksa remembers verified credentials per client address for up to five minutes. Under heavy load, a check that waits more than 10 seconds gets a `429` with `Retry-After: 1`.
- **Pass credentials upstream** is off by default, so after the list accepts the credentials, Paduraksa removes `Authorization` before the upstream sees it. A visitor the list let through without asking keeps their own `Authorization`.
- The review warns about a host that asks for a password without forcing HTTPS, because the password would cross the network in clear.
- A password change creates a revision of the list's users, never of the password.

### How the parts combine

With users and address rules both present, **How they combine** decides:

| Choice | An allowed address | A visitor outside the allow list |
|---|---|---|
| Address and password (the default) | Asked for a password | Refused with `403` |
| Address or password | Let in without a password | Asked for a password, which lets them in |

A denied address or country is refused either way. With no users, only the address rules apply, and with no address rules, only the password.

### Using a list for the connection deny list and for maintenance

- The global [connection deny list](connections-and-limits.md#connection-floods) uses only a list's **denied addresses**.
- A host's maintenance **bypass** uses only a list's **address side**: its allow rules, and no password. See [Hosts and routes](hosts-and-routes.md#maintenance-mode).

## Rule sets

A rule set is an ordered list of rules. Each rule has conditions and an action.

Conditions. A rule needs at least one, and a request must meet every condition the rule states:

- **Path:** *exact*, *prefix*, *glob* or *regex*, matched against the canonical path (case-insensitively).
- **Methods:** a set, such as `POST`.
- **User agent:** *contains* any of some case-insensitive substrings, or *empty* (which also matches a missing or blank one).

Actions:

- **Deny:** `403` with the built-in page.
- **Drop:** abort the connection with no answer.
- **Allow:** carves an exception out of the denies after it. An allow ends the set, but the rest of the chain still runs.
- **Honeypot:** answers with a page of the host's [honeypot](#honeypot). On a host without one, the rule is a deny.

The first rule a request matches decides. A request that matches no rule goes on.

- A set holds at most 200 rules. A glob has at most 126 characters and wildcards.
- Regexes can't backtrack catastrophically, so a slow pattern cannot stall the proxy.
- Write a path decoded, as a request would be after canonicalization. Paduraksa refuses a pattern that no canonical path could equal.

### Built-in presets

Three read-only sets that you can name directly. To add to one, use **Create from preset**, which makes a rule set of your own with the same rules.

| Preset | Refuses |
|---|---|
| Block AI crawlers | The user agents of known AI crawlers. The same list as the `robots.txt` preset, refreshed with each release |
| Block known scanners | `sqlmap`, `nikto`, `nmap`, `masscan`, `zgrab` and `nuclei`. The list is short on purpose, because it removes noise, not attackers |
| Block empty user agent | Scripts and some health checks |

## Rate limits

A rate limit policy is a token bucket with **requests** per second, minute or hour, a **burst** and a **key**.

| Key | One bucket per |
|---|---|
| Client IP | Client address. An IPv6 address counts by its /64 |
| Client IP and path | Address and the canonical path (`/API` and `//api;` share one) |
| Header | The value of a request header you name. A request without it falls back to the client address |

- A request over budget gets `429` with `Retry-After`.
- Buckets belong to a (policy, host) pair, so two hosts that name one policy do not share a client's budget. A route that uses the host's policy shares the host's buckets.
- Editing the rate or burst keeps the buckets, and the new numbers apply from the next request.
- A rate limit applies to the routes of proxy hosts and the admin host. It does not apply to unknown hosts, `404`s, ACME challenges, the browser check's verify endpoint, or Redirect and Respond hosts.

## Sign-in with your identity provider

An **auth provider** (Security, *Auth providers*) is chosen in the **Sign-in** setting of a host or a route, and sits in front of it. Before forwarding a request, Paduraksa asks the provider's endpoint whether to let it through (forward-auth). It works with authentik, Authelia, oauth2-proxy, or any endpoint that answers allow or refuse.

A provider has an endpoint URL and a preset:

| Preset | Endpoint it calls | Identity headers copied upstream | Decision cached |
|---|---|---|---|
| authentik (proxy outpost) | `/outpost.goauthentik.io/auth/traefik` | `X-authentik-*` | Per host |
| Authelia | `/api/authz/forward-auth` | `Remote-User`, `Remote-Groups`, `Remote-Name`, `Remote-Email` | Per request (path and method) |
| oauth2-proxy | `/oauth2/auth` | `X-Auth-Request-User`, `-Email`, `-Groups`, `-Preferred-Username` | Per host |

If you give a bare address, the preset adds its path. An empty field takes the preset's value.

Other fields:

- **Timeout:** 1 to 30 seconds, default 5.
- **Cache:** 0 to 300 seconds, default 10. 0 remembers nothing.
- Extra request headers to send, and response headers to copy upstream (a trailing `*` is a prefix).
- The **sign-in URL** and its return parameter.

How it behaves:

- The call is a `GET` with no body. `X-Forwarded-Method` carries the original method, and `X-Forwarded-Proto`, `-Host`, `-Uri` and `-For` carry the original request.
- Of the client's own headers, Paduraksa sends only `Cookie`, `Authorization`, `Proxy-Authorization`, `User-Agent`, `Accept`, `Accept-Language` and `X-Requested-With`.
- A `2xx` lets the request on, with the provider's identity headers added.
- Paduraksa removes the client's own copies of those headers on **every** route of the host, so a client cannot forge `Remote-User` through a route that skips the check.
- Paduraksa relays a `401`, `403`, `407` or a redirect (status, body up to 64 KiB, `Location`, `Set-Cookie`, `WWW-Authenticate`).
- A `401` to a browser navigation redirects to the sign-in URL with the original address in the return parameter. A request for the sign-in page itself is never redirected.
- It fails closed: none of these lets the request through:
    - an unreachable endpoint, or an answer Paduraksa cannot forward, gives a `502`;
    - a timeout gives a `504`;
    - more than 64 calls in flight gives a `503` with `Retry-After: 1`.
- `Authorization` and `Cookie` cannot be named as response headers, so the client's credentials reach the upstream as sent.
- Paduraksa verifies an `https://` endpoint against the system roots and has no "accept any certificate" option. Use `http://` on a private network, or a certificate the host trusts.
- Sign-in also applies to `robots.txt`, because it protects what the upstream serves.

Provider-specific setup:

- **authentik:** route `/outpost.goauthentik.io/` on the protected host to the outpost, with sign-in **off** on that route, for the callback.
- **oauth2-proxy:** route `/oauth2/` on the host to oauth2-proxy, with sign-in off. A bare `401` to a browser navigation becomes a redirect to `/oauth2/start?rd=<original URL>`.
- **Authelia:** its access-control rules decide by resource, method and network.

The review warns if a layer names a provider above a layer that switched sign-in off (the merged result is off, and naming a provider does not switch it back on), and if the provider's sign-in page is itself behind a provider.

## Browser check (proof of work)

The **Browser check** (a host setting) turns away clients that will not do work. A visitor without a valid cookie gets a small page that solves a proof of work in the browser, once. A signed cookie (`paduraksa_pow`) then lets the visitor through.

- **Difficulty:** the leading zero bits of SHA-256 a solution needs, 8 to 28 (default 16, under a second on a laptop). Each extra bit doubles the work, for visitor and bot alike.
- **Cookie lifetime:** 1 to 720 hours (default 168, seven days).
- A route can switch it on or off for its own paths, for example to exempt `/api` on a protected host.
- The cookie is bound to the host and the client address (a /32, or a /64 for IPv6), and is `HttpOnly`, `SameSite=Lax` and, over HTTPS, `Secure`.
- A browser with JavaScript solves the challenge. A client that asks for HTML gets the page, and one that does not and has no cookie gets `403`.
- The review warns about a host with the check on that serves plain HTTP without forcing HTTPS, because the cookie cannot be `Secure` there.
- If the signing key cannot be opened, every protected route answers `503` instead of letting requests through.
- Redirect and Respond hosts are never challenged.

## robots.txt

The **robots.txt** setting (global defaults, a profile or a host; not per route) is one of:

- **Pass through:** the upstream answers (the default).
- **Disallow all:** `Disallow: /` for everyone.
- **Block AI crawlers:** each known AI crawler gets `Disallow: /`, and every other crawler is allowed. The list is built in and refreshed with each Paduraksa release.
- **Custom:** your text, served as typed with a final newline.

It is sent with `Cache-Control: max-age=3600`, so crawlers pick up a change within the hour. It is answered after the host's access rules and the HTTPS redirect, and before route matching, so a Redirect or Respond host can carry one too.

[Analytics](observability.md#analytics) lists the bots that ignore it.

## Honeypot

The **Honeypot** setting (global defaults, a profile or a host; not per route) serves a trap on a host: a blog with no end, for crawlers that ignore `robots.txt` or probe for weak spots. It only observes. Nothing it sees refuses, bans or slows a client.

| Field | Built-in | Meaning |
|---|---|---|
| Enabled | off | Serve the trap on the host |
| Path | derived | The prefix of the trap, such as `/archive-k7f3/`. If unset, each host derives its own from its ID and the browser check's key, so the prefix is stable and names nothing about Paduraksa |
| Site name | derived | The blog's title, derived like the path |
| Share behind the browser check | 20 | Percent of trap pages that need a solved challenge, 0 to 100 |
| Difficulty | 16 | Leading zero bits of those challenges, 8 to 28 |
| No index | on | Sends `X-Robots-Tag: noindex, nofollow` on trap answers |

A path starts and ends with `/`, has letters, digits and `- _ . ~` only, is at most 64 characters and can't contain `/.well-known/`.

In the dashboard, the settings of a host, a profile and the global defaults have a Honeypot section. An empty path and name show the derived ones as placeholders. On a host, **Open trap** opens the path in a new tab.

There are two ways into the trap:

- **robots.txt.** If the host serves a managed `robots.txt`, a `Disallow:` line for the prefix is added to every group. A request under the prefix then came from a crawler that didn't read the file, or ignored it.
- **A rule.** The **Honeypot** action of a [rule set](#rule-sets) answers a matching request, on any path, with a page of the trap. Under the prefix too: a visitor the rule matches gets that page there and never passes the trap's challenge. The rule set's editor lists the hosts that use the set and have no trap.

How it answers:

- A request under the prefix is answered after `robots.txt` and before maintenance mode, a Redirect or Respond host's answer and the routes. The host's access list and rules, the request limits and the HTTPS redirect come first.
- The prefix is compared with the path's canonical spelling, and case counts. A path such as `/archive-k7f3x/` isn't under `/archive-k7f3/`.
- `GET` and `HEAD` get a page. Any other method gets `405`, except the `POST` of the trap's own browser check.
- The browser check of a trap page is the same page and solver as the [browser check](#browser-check-proof-of-work). Its cookie is separate and scoped to the prefix, so solving one passes nothing on the other.
- `Paduraksa__Honeypot__MaxConcurrent` (default 16) caps the pages generated at once. Over the cap, the trap answers `503` with `Retry-After`.
- If the signing key can't be opened, a trap with a path of its own answers `503`, and one with a derived path is off.

The review warns when:

- a host has the trap on but passes `robots.txt` through, because nothing then adds the `Disallow:` line;
- a route's path is under the prefix, because the trap answers it and the route never sees those requests.

An admin host can't have a trap. The request simulator shows a **Honeypot** step with the entry, the page, whether it is behind the browser check, and the status.

## The Security overview

**Security overview** (Security) shows what protects each host and whether it is working.

- Four live tiles for the last hour: what the gate refused overall, and what it refused for the browser check, for rate limits, and for access lists and rules together, each with a small chart. A tile opens the Security tab of Analytics over the same hour.
- A table of hosts with what protects each (browser check, rate limit, access list, `robots.txt`, rules and sign-in) and how many of its requests were refused in the last 24 hours. A route that differs from its host is grouped, such as "Off on 2 routes". Hosts with nothing on come last.

It reads the live config, not the stored one, so it shows what the gate is doing.

The counters are in the **Security** section at the bottom of [Analytics](observability.md#analytics), over the page's range (1 h, 24 h, 7 d, 30 d):

- the refusals per second over time, stacked by reason;
- a card each for proof of work, rate limits, access lists, the connection stage and sign-in;
- the requests the gate answered without a policy (unknown host, no route), once there are some.

They survive a restart and cover every host, regardless of the host picker. Older refusals stored without a reason count as "unknown reason". The same data is `GET /api/v1/security/overview?range=hour|day|week|month`.

## Defaults for new hosts

A new host is unprotected until you opt in. Put the protections you always want in the **global defaults** or in a **profile** ("LAN only", "Public app"), and hosts inherit them. To keep a service off the internet, set the host's **Access list** to a LAN-only list.
