# The dashboard

The dashboard is a Blazor Server app on the admin port, built on MudBlazor. Every change goes through review, every list is live over the circuit, and nothing in a component ever holds a secret longer than the step that shows it. This page covers the stack and principles, authentication and circuits, the config pages and drawers, certificates and settings, the Insight pages (logs, live tail, security overview, analytics), drafts, the shared conventions and first-run setup.

Contents:
- [Stack and principles](#stack-and-principles)
- [The foundation (M5)](#the-foundation-m5)
- [Config pages (M5)](#config-pages-m5)
- [Certificates, ACME accounts and DNS providers](#certificates-acme-accounts-and-dns-providers-m5)
- [Settings, My account and the audit log](#settings-my-account-and-the-audit-log-m5)
- [Drawers and the design language](#drawers-and-the-design-language-ux-pass-1)
- [UX pass 2](#the-rest-of-the-dashboard-in-the-design-language-ux-pass-2)
- [Insight pages](#insight-pages)
- [Settings fields](#settings-fields)
- [Drafts](#drafts-m10)
- [Notification history](#notification-history)
- [Dashboard conventions](#dashboard-conventions-ui-consistency-pass)
- [First-run setup](#first-run-setup)

## Stack and principles

**Stack.** A .NET 10 Blazor Web App in interactive server render mode, served on the admin app only. The component library is **MudBlazor** (the most complete, with good data grids and dialogs); Fluent UI Blazor was the alternative. Charts are lightweight SVG sparklines rendered by our own components, with ApexCharts for the detail views.

**UX principles:**
- **Live by default.** Status dots, request rates and cert expiry update over the existing circuit. As built, the overview, the security overview, Analytics, upstream health, the live tail and notification history read on a timer while their page is open; nothing on the request path waits on them or runs per user.
- **Every change goes through review.** An edit form leads to a "Review changes" drawer (diff, validation and warnings like "this host will lose its certificate"), then Apply. Undo in a toast rolls back to the previous revision.
- **Inheritance is visible.** Every setting shows a badge for *Inherited*, *Profile* or *Overridden*.
- A command palette (Ctrl or Cmd + K): jump to a host, "issue cert", "rollback", "tail logs for ...". Deferred in M5 and not built.
- Keyboard-first forms, dark and light themes, a mobile-friendly host list.

**Pages:**
1. **Overview:** traffic, error rate, p50 and p95 latency, top hosts, blocked requests, cert expiries, unhealthy upstreams, recent audit events.
2. **Hosts:** a searchable, taggable list with live sparkline, health and cert status. As built, a host is edited in a drawer (see "Drawers" below), not on a tabbed detail page, and the list has no sparkline.
3. **Request simulator ("Why was this blocked?"):** enter a URL, IP, UA and headers and see the exact decision chain (host match, each policy, route, upstream), against the live config, an old revision or the change being reviewed. The same function is exposed to the API and MCP, and access-log entries link straight into it (see [hot-path.md](hot-path.md)).
4. **Certificates:** list, issue (wizard), upload, local CA, renewal timeline, ACME order logs.
5. **Upstreams and health:** destinations, probe results, history.
6. **Security:** global rule sets, rate-limit policies, geo policy, presets, blocked-request explorer. Built in M7: an overview, rate limits, access lists, rules and sign-in (auth providers) pages.
7. **Live logs:** a streaming tail with filters (see "Live tail" below).
8. **History:** revision timeline, diff viewer, rollback.
9. **Audit:** filterable, exportable, with hash-chain verification status.
10. **Settings:** global defaults, profiles, DNS providers, ACME accounts, notifications, users and roles, API tokens, sessions, backup and export.

## The foundation (M5)

**Stack and wiring.**
- A Blazor Web App in `Paduraksa.Dashboard`, a Razor class library that references Application and Contracts only. **MudBlazor 9**: the plan's choice held. It has what the later pages need, works within the CSP below and ships its assets in the package.
- `AdminApp` adds it after the API: `UsePaduraksaAdminApi`, then `UsePaduraksaAdminAuth`, then `UsePaduraksaAdminWeb` (authorization, antiforgery and the dashboard's pipeline hooks), then health, identity and the resource API, then the sign-in form endpoints (`MapPaduraksaAdminWeb`), `MapStaticAssets`, and `MapPaduraksaDashboard` at `/`.
- The dashboard owns its paths (`DashboardRoutes`). The server hands them to `Paduraksa.Admin.Web` as `AdminWebOptions`, so neither project references the other.
- Static files (Blazor's `blazor.web.js`, MudBlazor's CSS and JS, the dashboard's own `app.css`, `paduraksa.js`, `favicon.svg`) come from the app with fingerprinted URLs. No CDN, no web font.
- The server sets `RequiresAspNetWebAssets` (its own project has no `.razor` file, so the SDK would otherwise leave `blazor.web.js` out) and maps a build output's static web assets itself (the host does that only in Development).

**Authentication.** One session serves the API and the dashboard: the same `__Host-paduraksa-session` cookie, set only on ordinary HTTP responses.
- The sign-in, second-factor, sign-out and setup pages are plain HTML forms (static rendering, `[ExcludeFromInteractiveRouting]`). They post to form endpoints in `Paduraksa.Admin.Web`: `/auth/signin`, `/auth/second-factor`, `/auth/signout`, `/auth/setup`.
- These run the API's own steps (`SignInFlow`: throttle, bounded password derivation, lockout, audit, cookie; channel `dashboard`). They need the antiforgery token the page rendered (`__Host-paduraksa-antiforgery`, `SameSite=Strict`) and pass the CSRF middleware's same-origin check (`RequireSameOrigin`).
- They answer with a 303 carrying only an error code (the API's problem codes, plus `setup.passwordMismatch`) and a local `returnUrl` (anything else becomes `/`).
- Outside `/api` the authentication middleware reads the session cookie leniently:
  - valid: the caller, and `HttpContext.User` is a `SessionIdentity`;
  - partial: the partial session;
  - anything else: anonymous;
  - a bearer token never counts.
- Pages carry `[Authorize]`, and the `Paduraksa.Session` scheme's challenge redirects to `/signin?returnUrl=...`.
- A TOTP code is accepted with the space authenticators show.

**Circuits and revocation.**
- `DashboardAuthenticationStateProvider` takes the request's `SessionIdentity` (the circuit never holds the cookie) and adds the user's permissions. `DashboardPrincipal.Has(Permission)`, `IfPermitted` and the navigation hide what the user cannot use. The services still decide.
- It re-checks the session by ID (`SessionService.CheckAsync`):
  - in the background every `Paduraksa:Admin:SessionRecheckInterval` (30 s; it does not count as use, so an open page still idles out);
  - on circuit activity, at most every 10 s;
  - on every service call (`DashboardCaller`, which counts as use).
- A revoked or expired session, a disabled or deleted user, or a password change elsewhere makes the state anonymous. The router then reloads the page to `/signin?notice=sessionEnded`, and a circuit still talking 10 s later is torn down (`SessionCircuitHandler`).
- Changed roles change the permissions on the next check.

**Setup.** While setup is incomplete the gate redirects a browser opening any page (a `GET` accepting `text/html`) to `/setup`, and opens only `/setup`, `/auth/setup`, `/_framework` and `/_content`. Scripts still get the 503 problem, and the circuit hub stays shut.
- The page is one form in two numbered steps: claim the instance with the setup token, then the Owner.
- It calls `SetupService` through the same flow as `POST /api/v1/setup` and signs the Owner in.
- Like the API's setup endpoint it is refused through the proxy (`setup.viaProxy`).

**Hub, proxy, CSRF.** `/_blazor` needs a signed-in caller (else a bare 401) and passes the same-origin check on every method, the WebSocket handshake included. Through the proxy the circuit's WebSocket is an ordinary upgrade on the internal listener, with the hop secret checked there. This is tested end to end: negotiate, 101, and 401 without the cookie.

**CSP and headers.** HTML documents outside `/api` get (`DashboardSecurity`; the hook `AdminPipelineHooks.HtmlContentSecurityPolicy`):

`default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; manifest-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`

- No inline script, no `eval`.
- `'unsafe-inline'` for styles is MudBlazor's need (style attributes and the theme provider's `<style>` of CSS variables). It applies only to the dashboard's pages. The API, the static files and the Scalar docs keep their own policies.
- Blazor's own `frame-ancestors` header is turned off (the full policy covers it).
- Pages and the hub are `Cache-Control: no-store`. Fingerprinted files keep their long caching.
- HSTS, `nosniff`, `no-referrer` and `X-Frame-Options: DENY` are as for the rest of the admin.
- The browser test fails on any CSP report in the console.

**Look.** A custom MudBlazor theme (`PaduraksaTheme`):
- slate neutrals and one jade-teal accent (`#3FBFAE` dark, `#0E7D6F` light);
- the platform's UI font on a small regular scale;
- 10 px corners and outlined cards.

Dark is the default. With no choice of their own a person gets their system's setting (a script records it in a cookie so the next page renders right from the first byte). The top bar's toggle stores their choice (`paduraksa-theme`).

The shell is a top bar (mark, product name, theme toggle, user menu with name, username, roles, "My account" and sign-out), a responsive side navigation that folds away below the `md` breakpoint, and pages under a `PageHeader`.

Conventions:
- `EmptyState`: icon, one line of what is missing, one of what to do, one action.
- MudBlazor skeletons while loading.
- Snackbars bottom-right, newest on top, no duplicates.
- The sign-in pages mirror the palette in plain CSS.

**Pages.** Overview (`/`) shows the config source and degraded state, the latest revision, host and upstream counts, and the certificate count and those in use that end within 30 days. Each is shown only with the permission to read it. Its data is carried from the server's render into the circuit (`[PersistentState]`), so it does not flash. A degraded start shows a banner on every page.

**Data Protection.** The admin's key ring lives in the vault (see [vault-and-certificates.md](vault-and-certificates.md)), so antiforgery tokens and circuits survive a restart.

**Tests.** `Paduraksa.Tests.Dashboard`:
- bUnit: the sign-in pages' forms, messages and return addresses; the auth state provider (permissions, revocation, role changes, the background re-check, the circuit's grace period); permission-based visibility; the theme and its toggle.
- A Playwright smoke test against a real `PaduraksaHost`: setup through the page, TOTP enrolment, sign-out, sign-in with password and TOTP, the overview, the light theme and a phone-sized window, a revoked session ending the open page, sign-in with a recovery code.
- It skips when no Chromium is installed: `pwsh tests/Paduraksa.Tests.Dashboard/bin/Release/net10.0/playwright.ps1 install chromium` (or the `Microsoft.Playwright.CLI` dotnet tool), or `PADURAKSA_E2E_CHROMIUM=/path/to/chromium`. `PADURAKSA_E2E_SCREENSHOTS=<dir>` keeps screenshots.
- Integration tests cover the pipeline (gate, forms, antiforgery and same-origin, hub, CSP) and the dashboard through the proxy.

## Config pages (M5)

**Seam.** `IDashboardConfig` (`Paduraksa.Dashboard/Config`) is everything the config pages ask of the Application layer:
- the current config and its commands (dry runs too);
- the history timeline (`RevisionQueryService.ListWithChangesAsync`, the one Application addition: revision pages with each revision's change list, read from the stored summary without decompressing a document);
- a revision with its document;
- the certificate listing;
- the user list (to name who made a revision; empty without `users:read`);
- the simulator;
- `Watch` over `ConfigService.Changes`.

`DashboardConfig` is the real one, per circuit. The bUnit tests swap in an in-memory one. Reads are not authorized by the services (as on the API): each page checks `config:read` (`certificates:read`, `simulate`) first. Commands and simulations carry the user's `CommandOrigin`, and the services decide.

**Live.** Every page loads once its circuit is live (the prerender shows the skeleton) and re-reads on each commit through `Watch`. No timer polls. An open editor says when its object changed elsewhere (with Reload). The Apply would be a conflict anyway.

**Typed IDs at the edge.** `/hosts/{Key}` and `/upstreams/{Key}` take a string segment and parse it into a `HostId` or `UpstreamId`. Anything else, a wrong-type ID included, is "not found". `?revision=` parses into a `ConfigRevisionId`. Addresses are built from typed IDs (`DashboardRoutes.Host(id)`), and new objects get fresh typed IDs in the form models.

**Review before apply.** Every edit opens "Review changes" (`ReviewPanel`, a level of the drawer stack): the command runs as a dry run on the current head and shows:
- the diff (`ChangeView`): grouped by top-level object, routes and destinations nested, also those an added or removed object carries. Each field shows old and new, read from the two documents, with references by name;
- the warnings: the compiler's, placed by object name, plus "will lose its certificate", "will stop answering", names given up, an upstream left without destinations;
- the errors by place. An object command's errors also go back to the form's fields.

Apply runs the same command with the version the form opened with.
- A stale version or a deleted object is a conflict with Reload.
- A commit that could not go live stays on screen as "saved as #N, but it isn't live" with the publisher's errors.
- A commit leaves a toast with **Undo** (`ConfigUndo`): a rollback to the parent revision (a replace with the empty config for #1), based on the revision it undoes. It is refused when someone changed the config in between. A newer toast replaces the last, since only the latest change can be undone.
- The drawer sits above the toasts, closes with Escape and focuses its heading.
- "Simulate this change" runs a request against the draft in place, and the draft stays offered to the simulator page (`ReviewedDraft`) until it is applied.

**Forms.**
- **Hosts:**
  - names;
  - kind: proxy (routes with match, path and upstream), redirect (target, status and path), fixed (status, content type and body), or admin;
  - the certificate binding: auto, specific from the usable certificates, or none (which lifts an inherited HTTPS redirect);
  - the HTTPS redirect, showing what it inherits;
  - the profile;
  - under Advanced: the TLS minimum, TCP ALPN, HTTP/3, an IP allow list, tags and notes.

  The form keeps what it does not show (a route's own settings).
- **Upstreams:** destinations (IDs kept, so YARP health survives), load balancing, active and passive health, the connect and idle timeouts (in seconds, blank for the default, 0 for no idle limit) under Advanced, a connection limit and notes. The hosts and routes that use it sit under the form. Deleting one in use is refused up front with the routes named.
- **Inline validation** is the real `ConfigValidator` over the current config with the edited object put last (as the command pipeline validates it). Errors show once a field is touched, all of them (with a summary) after a review attempt. Enter submits to review.

**Lists.**
- **Hosts:** searchable, with a status dot (off, a live warning or a certificate problem, serving), where requests go, HTTPS behaviour, and the certificate state of the names (`HostOverview`: auto against the usable inventory, exact or parent wildcard; specific by ID; expiring within 14 days).
- **Upstreams:** destinations, policy, health and usage.

**History.** The timeline shows number, who ("You", a user's name with `users:read`, a token, the CLI, Paduraksa), channel, time, message, and what changed in counts. It has "load older". The selected revision's diff is against its parent or any loaded revision. Roll back goes through the drawer, based on the latest. Simulate is there too.

**Simulator.** URL, client IP, method and headers, with arrival, proxy address, TLS version, ALPN and SNI under Advanced. The target is live, a revision (`?revision=`), or the change under review. The outcome comes first in its tone, then the destinations with live health, the notes, and the trace as a vertical list of steps with verdict icons, summaries and facts. A refused request marks its fields.

**Permissions.** Add, edit, delete, roll back and Apply show only with `config:write` (a read-only form otherwise). The simulator needs `simulate`. The certificate picker and states need `certificates:read`. The services still refuse on their own.

**Errors and 404.**
- An `ErrorBoundary` around the page body turns an exception while prerendering or live into an error card in the shell (logged by the framework, cleared by the next navigation) instead of the API's JSON 500. A broken circuit shows Blazor's error bar, styled.
- An address nothing answers, asked for by a browser (GET or HEAD accepting HTML, outside `/api`, the hub and the static files: a route constraint, so the API keeps its 405 and 415), renders the app with a 404 and the router's not-found page for a signed-in user and challenges anyone else. Everything else keeps routing's answer.

**Secrets.** None reach a component. The pages hold Contracts DTOs only (config documents, certificate public facts, revision metadata).

**Tests.**
- bUnit: the host form (kind switching, inline errors, what it sends, read-only); the review drawer (diff and warnings, Apply with the opened version, errors by place, conflict at review and at apply, publish failed); the diff; the simulation trace; and the pages per role (adding, editing and rolling back hidden without `config:write`, the simulator refused without `simulate`, a non-host ID not found, an upstream in use refused, the list following a commit).
- Playwright (`ConfigPagesEndToEndTests`): create an upstream and a proxy host through review and apply, see it listed, simulate a request that forwards to it, edit and undo from the toast, the history with all four revisions, the styled 404 and a phone width, with no CSP report or console error. Screenshots of each page with `PADURAKSA_E2E_SCREENSHOTS`.

**Not yet:** route reordering, and host tags as list filters. (A route's own access list is picked in its settings, M7.)

## Certificates, ACME accounts and DNS providers (M5)

### Certificates (`/certificates`)

One list of the inventory plus the process's built-in certificates (the development placeholder, now shared with the admin container when enabled). It shows:
- name and names;
- source (ACME, uploaded, self-signed, local CA, built-in);
- status (an issuance in progress shows as "Issuing");
- a colour-coded countdown (green, amber inside 30 days, red inside 7 or after) with the date;
- the issuer and the hosts that use it;
- counters on top: active, expiring, failed or expired, pending.

It has skeletons, an empty state, and cards with labelled cells below 900 px.

Which hosts use a certificate is `CertificateUsage` (Application): the compiler's selection (Specific, the best match per name of every enabled Auto host, the default) applied to the metadata. It is shown only with `config:read`.

**Two cards.**
- The admin certificate is a read-only card: what it is, its names and countdown, "add names with `Paduraksa:Admin:CertificateNames`".
- The local CA root has a "Trust the local CA" card: name, validity, SHA-256 fingerprint, the `/api/v1/ca/root.crt` and `root.pem` downloads (`download` links, which Blazor's router leaves alone), and how to install it on Windows, macOS, Linux, Firefox and phones.

**Detail drawer.** A row opens it: status, validity bar, names, users, issuer, subject, key, serial, fingerprint, the chain's issuers and the public chain PEM. For ACME it adds the account, challenge, order state, next renewal, the ARI window, the last attempt, and a failure with the CA's message and the next try.

### Actions (with `certificates:write`; the services still decide)

- **Upload** a PEM (chain and PKCS#8 key), a PEM plus a separate key file (read as one stream through `PemConcatStream`, nothing buffered), or a PFX with an optional password.
  - **This goes through the circuit, not the API's multipart endpoint** (a change from the original design's note). `InputFile` streams the browser's file into `CertificateOperations.UploadAsync` during the call, capped at `CertificateImportLimits.MaxBytes`.
  - The password leaves the component's state before anything is awaited and is handed over as a `char[]` cleared in `finally`. The form forgets the file reference afterwards.
- **Self-signed and local-CA leaves:** names, label, validity, key. Issuing from the local CA creates the root first when there is none.
- "Create the local CA".
- **Re-issue** (self-signed and local-CA leaves, confirmed).
- **Delete** (confirmed; refused for the admin certificate and built-ins).
- **ACME request:** names, account, HTTP-01 or DNS-01 with a provider, label, key. A wildcard needs DNS-01 and an IP HTTP-01, checked in the form and again by the service.
- **Renew now.**

### Live ACME progress

`AcmeIssuanceService` takes an optional `IProgress<AcmeOrderProgress>` on request and renew, and keeps the latest step of every running issuance for `GetProgress`.
- The stages (Contracts: `AcmeProgressStage`) are Starting, OrderCreated, Authorizing, ChallengePublished, PropagationCheck, Validating, Validated, Finalizing, Issued and Failed, with the sanitised message and `RetryAt`. They are public facts only. A throwing reporter is logged and ignored.
- The dialog pushes each report to the page (no polling). It shows the steps order, authorizations, challenge published, DNS propagation (DNS-01 only), validated, finalized, issued; the skipped steps of a resumed order; the elapsed time; and a failure with the CA's message, the retry time and "Retry now".
- The run uses the application's stopping token, like the API. Closing the dialog ("Continue in the background") does not stop it.
- The detail drawer is the one place that polls. Every 2 s, while it is open, it reads `GetProgress` (in memory) for an ACME certificate and the stored order while a run is going. A run started by the scheduler or another tab shows too.

### Live lists

`ConfigService.OperationalChanges` (`OperationalChangeFeed`, Application) announces every audited change made through `ApplyOperationalChangeAsync` or `ApplyCertificateChangeAsync` after its commit (the audit action and target only). `LiveUpdates` (dashboard) listens to it and, where host usage matters, to the config change feed. It refreshes the page once per burst (300 ms). No list polls.

### Settings hub, ACME accounts and DNS providers

`/settings` is a hub of tiles. ACME accounts and DNS providers are its first two pages (`/settings/acme-accounts`, `/settings/dns-providers`; `providers:read` to see, `providers:write` to change).

**ACME accounts:**
- The list shows CA, contacts, key algorithm, staging and EAB markers, and how many certificates use each.
- Add with presets (Let's Encrypt, its staging, ZeroSSL and Google Trust Services, which need EAB) or any https directory URL. Fields: an optional contact email, the CA's terms (required, linking the CA's page), and optionally the EAB key ID and HMAC key.
- Deactivate is final and confirmed.

**DNS providers:**
- Cloudflare or deSEC, a label and the API token. The form names the one host the token is sent to (`DnsProviderService.ApiHostOf`).
- Test shows the provider's verdict inline (accepted, or its error without the token), and runs after adding or replacing a token.
- Replace the token. Delete (confirmed).

**Secrets are write-only.** They are password fields, emptied before the call, handed over as `char[]` and turned into bytes only in `ProviderActions` (which zeroes them when the service returns). Nothing returns a secret, the submission records print `***`, and the vault keeps the value.

**Shared pieces** (new files): `ConfirmActionDialog`, `LiveUpdates`, `QrCode` and `QrCodeImage`. Toasts follow the config pages: success bottom-right, a refusal in the service's words.

**Not yet:** changing a certificate's label or notes; replacing an uploaded certificate under its ID from the page (the API has it); moving certificates between ACME accounts or providers; a CNAME-delegation hint for DNS-01.

## Settings, My account and the audit log (M5)

**Settings hub.** Tiles, each shown only with the permission its page needs:
- **Global defaults** (`/settings/defaults`, `config:read`).
- **Profiles** (`/settings/profiles`, `/settings/profiles/new`, `/settings/profiles/{prof_...}`).
- ACME accounts, DNS providers.
- **Users and roles** (`/settings/users`, `users:read`).
- **API tokens** (`/settings/tokens`, `tokens:own`).
- **My account** (`/account`, also from the user menu).

Addresses are in `SettingsRoutes`. Every page has the "Settings" way back.

**Global defaults.** One form over `GlobalSettings`, applied through the review drawer as `UpdateSettingsCommand` with the version it opened with. It holds:
- the bottom layer of host settings (HTTPS redirect, minimum TLS, protocols over TCP, HTTP/3, HSTS, an IP allow list; each "Built-in default (...)" or a value, `HostSettingsFields`);
- what a request for an unknown name gets (404, 421, close);
- the default certificate (usable ones, with `certificates:read`);
- the client address: direct, behind a proxy (proxies, header, `X-Forwarded-Proto`), Cloudflare proxy or Cloudflare Tunnel (`True-Client-IP`, the `X-Forwarded-For` fallback), as cards. Switching modes drops the options the validator would refuse.

There is a line of help per setting. Inline validation is the real validator with the settings put in (`InlineValidation.ForSettings`). A change made elsewhere reloads an untouched form and puts a banner over an edited one. HSTS is in the config model since M6, so it has its own group.

**Profiles.** What each sets and the hosts that use it (a table since the drawers). The form (name, notes, the same fields, each "Inherit (the global value)") goes through review (`UpsertProfileCommand` and `DeleteProfileCommand`, IDs typed at the edge). Deleting one in use is refused up front with the hosts named.

**Users and roles.**
- The users with their roles, enabled, locked, two-step on or off, and, with `sessions:all`, when last seen (their newest session). A card says what each role may do in one line.
- With `users:write`: add (username, display name, initial password, roles; checked as the service checks), edit (display name, roles, enabled, sent as one `PATCH`-like update of only what changed) and delete (confirmed).
- The last-Owner guard's refusal is shown in the dialog that asked (`identity.lastOwner`'s words: make someone else an Owner first).

**API tokens.** One's own, and "Everyone's" with `tokens:all`. Revoked and expired ones are shown on request.
- Create: name, expiry (7, 30, 90 days, a year, never, or a date), and scopes by area with read-only and "all you can" presets.
- Only what the user holds is offered ("a token never exceeds its owner"), and never `tokens:*` or `sessions:all` (tokens cannot manage tokens or sessions).
- The secret shows once in the same dialog, with Copy and a "you won't see it again" notice. A click beside the dialog does not close it, and closing it drops the secret from the page's state.
- Revoke is confirmed.

**My account:**
- **Profile.** Username is read-only. The display name is editable with `users:write`, as the service allows, read-only otherwise.
- **Password.** Current, new with a strength meter, again. On success "your other sessions were signed out". The fields are emptied as the change is sent.
- **Two-step verification.** Set up with the wizard's `TwoStepEnrollment`. New recovery codes and turning it off, each behind the password. Recovery codes are shown once with Copy and "Download .txt", made in the page.
- **Sessions.** This one marked, browser and system from the user agent, address, last seen. End one, or "sign out everywhere else" (confirmed).

**Audit (`/audit`, `audit:read`).**
- **Filters:** time range (1 hour to all, or from and until in UTC), who (you, users with `users:read`, tokens one may list, Paduraksa, the CLI), action (exact or `prefix*`, with suggestions), target, and outcome (`AuditFilter.Outcome`, new in Application and on the API as `?outcome=`).
- **Rows:** when, the action in words with its dotted name as a chip coloured by area, who (a name where the page may read it, else the kind and a short ID), and the outcome chip.
- A row opens to the time, channel, client, target (linked when it has a page), revision, event ID and the details pretty-printed (never a secret).
- It pages through the cursor ("Load older events"). New events reload the first page, or offer "Show them" once older pages are loaded.
- The hash-chain card verifies on load and on "Verify now": intact with the count and the head hash's start, or broken with the first bad event and why.
- "Export JSON Lines" writes every event the filter matches (up to 100 000), field names as the API's, made in the page and saved through the browser.

**Seams.** `IDashboardIdentity` (users, password, TOTP, sessions, tokens; `DashboardIdentity` over the Application services) and `IDashboardAudit` (`AuditQueryService`).
- Password checks take the admin app's derivation slots (`IPasswordCheckSlots`, the server hands over `AdminThrottle`'s). Busy is "try again in a moment".
- Successful identity changes are announced on `IdentityChangeFeed`, so lists of users, tokens and sessions follow other circuits without a timer. Changes through the API or CLI show on the next load.
- The defaults and profiles follow the config change feed.

**Secrets.** Passwords, the TOTP URI and secret, recovery codes and a new token's secret live in component state only while their step shows, and are dropped on close, on the next step and on disposal. No DTO the pages hold carries one.

**Shared pieces.** `SecretValue` (shown once, Copy), `RecoveryCodesView`, `DashboardBrowser` (clipboard and saving a file from a .NET stream, in `paduraksa.js`), `settings.css`. Dialogs are MudBlazor dialogs (inline for the forms, so their state is the page's). Confirmations are `ConfirmActionDialog`, and the QR code is `QrCodeImage`. The certificate, ACME account and DNS provider forms moved from dialogs to drawers.

**Fixes from the config pages.**
- The review drawer's footer wraps its actions and its columns can shrink, so Apply never leaves the window. The cut-off Apply in the 1366 px screenshot was the sheet caught 32 px into its slide-in. Browser screenshots now take finite animations at their end.
- Toasts are at most 420 px wide with 20 px margins (full width less 8 px on a phone), and pages leave room below their last card to scroll clear of one. The drawer and dialogs sit above them.

**Tests.**
- bUnit (`SettingsPagesTests`, `AccountAndAuditTests`): a token's secret shown once and gone from markup and state after closing, only held scopes offered; the last-Owner refusal and what an edit sends; the add-user form's checks; the users, defaults and audit pages per role; the defaults through the drawer with mode options dropped; a profile in use refused; the TOTP enrolment's states and what each drops; recovery codes once; sessions and ending one; the password change; the audit filters, a row's details and the chain card intact and broken; the export line.
- Playwright (`SettingsEndToEndTests`): a Viewer added and, in a second browser, offered no change; a token made on the page answers `GET /api/v1/auth/me` as a token; a global default through review and apply, with Apply inside the window at 1366 and 390 px; the audit page with those events and an intact chain; My account (TOTP enrolment, two sessions, ending the other browser's, which lands on the sign-in page); no sideways scroll at 390 px on the new pages.

**Not yet:** changing one's own display name without `users:write` (the service does not allow it; an Owner can); identity changes made through the API or CLI reaching open pages live; a CSV export. Built since: notifications (M9), the YAML export and import page (see [admin-surfaces.md](admin-surfaces.md)) and the backup page (see [vault-and-certificates.md](vault-and-certificates.md)).

## Drawers and the design language (UX pass 1)

Creating and editing moved from separate pages and dialogs into drawers that stack. Making a host with a new upstream and a certificate now happens in one place, as in nginx-proxy-manager.

### The stack

`DrawerStack` (per circuit, `Paduraksa.Dashboard/Drawers`) holds the open levels. `DrawerHost`, once in the layout after `MudLayout`, renders them.
- Each level is a sheet from the right: 520, 640 or 780 px, the whole width of a phone.
- The top one is live. The ones under it are shifted 28 px aside per level, dimmed by the top level's backdrop and `inert`. The page behind is inert too while any drawer is open.
- A component opens a level with `Push<TComponent>(parameters, width)` (or `OpenAsync`, which waits).

The level's component gets the `DrawerLevel` as a cascading value:

| Member | Effect |
|---|---|
| `Close(value)` | Hands a result back to the level below and closes it. |
| `Cancel()` | Closes without a result. A level closing takes the levels above it along, cancelled. |
| `IsDirty` | It holds unsaved changes. |
| `Busy` | Keeps it open while it applies, or while an issuance shows its progress. |
| `ResultOnClose` | What it hands back however it is closed (a commit that could not go live). |

**Closing.**
- Escape, the backdrop and the close or back button act on the top level only. `paduraksa.js` hands Escape over only while focus is in the top drawer and no list, menu or dialog is open.
- When the level is dirty, they ask "Discard your changes?" in the drawer itself (Escape again keeps editing).
- Leaving the page with a dirty drawer asks the same (`NavigationLock`).
- Focus moves into a level as it opens (its first field when the form focuses one, else its title, which names the dialog) and back to what opened it as it closes.

**Layering.** Dialogs (a confirmation asked from a drawer) sit above the stack. The theme raises MudBlazor's dialog z-index to 1700 (tooltips 1800). The stack is at 1600 and up, above the toasts. MudBlazor places a select's list above its field, so pickers work in any level.

### Review is a level

`ReviewPanel` is "Review changes" as a level. `DrawerStack.ReviewAsync(ReviewRequest)` returns a `ReviewOutcome`: applied, or reload asked. Save in a form drawer stacks it on the form, and Apply closes both. Every change still goes through it.

`ReviewDrawer` keeps its old parameters for the pages whose form is not a drawer (global defaults, a rollback, the wizard's first host) and pushes the panel for them.

### Frame

`DrawerShell` is every drawer's frame:
- the way back to the level below ("< New host", named "Back to New host");
- an overline, the title with an icon, a line under it, actions beside the close button;
- banners that do not scroll away (changed elsewhere, read-only, in use);
- a body that scrolls;
- a footer for the actions: Delete on the left, Cancel, then the primary.

With `OnSubmit` the drawer is one form, so Enter submits. A form that brings its own buttons (the ACME account, DNS provider and certificate forms, shared with the wizard) keeps them in the same bar at the drawer's foot (CSS).

### Hosts, upstreams, profiles

`HostDrawer`, `UpstreamDrawer` and `ProfileDrawer` (`Paduraksa.Dashboard/Editors`) replace the editor pages.
- They share `DraftDrawer<TModel, TConfig>` for the life every draft drawer has: the caller and its permissions, the document, inline validation, the dirty guard, review-before-apply, "changed elsewhere" and reload. Each keeps its markup and its real differences (host staging, upstream probes, the profile's usage and settings count).
- Every reference in the host form is a picker of what exists with "New ..." last (`RefPicker`): a route's upstream (each with its address, and the chosen one's address under the field), the certificate (Automatic, the usable ones, None) and the profile.

**Staging.** "New upstream..." and "New profile..." stack the other form in stage mode. It is checked against the host's draft and handed back without being applied. The host form picks it, marks it "New" and lists what goes with the host.
- A host with staged objects is reviewed and applied as one `ReplaceConfigCommand` revision. `StagedChange` is the latest document with the host put in its place and the staged objects it still uses added. A staged upstream it no longer routes to is dropped.
- The same version check an object command makes applies: a host changed elsewhere since the form opened is not overwritten.
- The revision's message is "New host app.example.com with upstream app". Its audit action is `config.replace`.
- Cancelling the host drops what it staged, so nothing is left behind.
- A host without staged objects still goes as `UpsertHostCommand`.
- A conflict at apply rebuilds the replace on the latest config, keeping what was staged.
- "New certificate..." stacks the certificate flow (`CertificateCreateDrawer`) with the host's names. A certificate is a vault object, so it is issued or uploaded at once and comes back selected. A pending ACME certificate leaves the binding on Automatic, which picks it up once issued.
- A form is dirty when what it would send differs from what it opened with (the canonical JSON of the objects, `StagedChange.Fingerprint`). A staged object makes it dirty.
- The upstream form checks a destination on request (`UpstreamProbe`, as the wizard: a TCP connection, reachable or not) and lists the hosts and routes that use it.

### Certificates, ACME accounts, DNS providers

- A certificate opens in `CertificateDrawer`. It reads the inventory itself and follows its changes, so its address opens it from anywhere. Renew now stacks the progress on it. Re-issue and delete ask in a dialog over it.
- "Add certificate" opens `CertificateCreateDrawer` with a choice of where it comes from: an ACME CA, the local CA, self-signed, or upload. The "More" menu goes straight to one, and creates the local CA. While an issuance shows its progress, the drawer ends only with Done or Continue in the background.
- In the ACME form, the account and the DNS provider are pickers with "New ACME account..." and "New DNS provider..." (or a button when there is none). They stack `AcmeAccountDrawer` and `DnsProviderDrawer` (also what the settings pages open) and come back selected.

### Addresses

`RoutedDrawer` ties a list page's route key to its root drawer. These addresses are the list with that drawer open: `/hosts/{host_...}`, `/hosts/new`, `/upstreams/{ups_...}`, `/upstreams/new`, `/settings/profiles/{prof_...}`, `/settings/profiles/new` and `/certificates/{crt_...}`.
- A row opens its address. Closing the drawer goes back to the list's address. The browser's back closes it. Leaving the page closes it with every level stacked on it.
- The key is parsed at the edge as before. One that is not an ID of the right type opens a "not found" drawer (`NotFoundDrawer`), and an object deleted under an open drawer says so in it.

### Lists

- **Hosts** read like nginx-proxy-manager's:
  - the names (more as chips, and tags);
  - where requests go, line by line ("/api -> app  http://10.0.0.5:8080", a redirect's target, a fixed status);
  - the certificate state with the HTTPS behaviour;
  - the status (Online, Attention with the reason as a tooltip, Off) with an on/off switch for writers (not on the admin host) that goes through review like any change.
- **Upstreams:** name and policy, the destinations' addresses, and the hosts that use each (links).
- **Profiles:** what each sets, the hosts that use it, delete.
- The certificates list uses the same table.

### Design language

Shared pieces:
- `PageHeader`: the title, an optional lead, and the actions with the primary last. No overline, since the navigation and the data already say where a page is.
- `ListToolbar`: search, how many match, the list's own controls. Hidden on an empty list.
- The data table (`pdk-dt`): rows as grids with columns per list, the main cell's link as the keyboard's way in, and cards with labelled cells below 900 px.
- `EmptyState`.
- `StatusChip`: a state in a word, tone and dot or icon.
- `FormSection`: a heading, an optional line, an optional control, the fields. Sections are divided by a rule instead of cards.
- `RefPicker`, `DrawerShell`.

A line of copy stays only if it says what the title, the navigation or the data does not, and helps the person decide or act. `app.css` gained the Drawers and Lists sections and lost the editor pages' and card lists' rules. `certificates.css` lost its own table.

### Tests

- bUnit (`DrawerStackTests`):
  - push, pop and the result handed back, a level closing the ones above it;
  - Escape and the backdrop on the top level only, with the levels below inert;
  - the discard question (Escape, keep editing, discard), a busy level staying open;
  - an upstream made from the host drawer applied with the host as one `ReplaceConfigCommand` (one dry run, one commit, the message), and cancelling the host leaving nothing behind;
  - the host drawer at its address (read-only without `config:write`), a wrong or wrong-type ID, closing a drawer going back to the list and leaving the page closing it;
  - the upstream drawer refusing a delete in use, the upstreams list naming the hosts;
  - the review tests run through the drawer host.
- Playwright (`ConfigPagesEndToEndTests`): a host made in its drawer with a new upstream stacked on it (Escape closing only the upstream's), both applied as revision #1, listed with the upstream's address, the upstream's drawer with its usage, the simulator, an edit undone from the toast, the list's switch reviewed and cancelled, "New certificate..." from the host's drawer issuing from the local CA and coming back selected, the history, the not-found drawer. At 390 px: the lists as cards, a stacked drawer with its actions inside the window, the discard question, and no sideways scroll. Screenshots of each.
- The wizard's test opens a certificate's drawer by its row and uploads through the new drawer.

## The rest of the dashboard in the design language (UX pass 2)

**Where things live.**
- The side navigation lists areas, and an area's pages share a row of tabs under the page header (`SectionTabs`, through `PageHeader.Area`):
  - **Hosts, Upstreams, Profiles**;
  - **Certificates, ACME accounts, DNS providers**;
  - **All settings, Global defaults, Users and roles, API tokens, My account**.
- Profiles, ACME accounts and DNS providers are found where they are used. Their addresses under `/settings` are unchanged.
- The navigation marks a page's area by the longest address it starts with (`DashboardNavigation.ActiveFor`: a profile marks Hosts, an ACME account Certificates, My account Settings). It is drawn as plain links now rather than MudBlazor's nav links.
- The settings hub groups its links by what they are about (Hosts, Certificates, People and access), shown only with the permission each page needs. The "Settings" way back on each page gave way to the tabs.
- The user menu gained API tokens.

**Overview.** First what needs the person, most serious first, each with the drawer that fixes it (`HomeSnapshot.AttentionOf`):
- a certificate that failed;
- a certificate in use that ends within 30 days (red inside 7);
- a host the live config warns about or whose certificate is a problem;
- an upstream a host uses with no destination switched on;
- a master key beside the vault, for a user who holds `vault:manage` (threat model G3);
- a degraded config store;
- or "Nothing needs you".

A missing second factor is not on the overview (threat model G4, the owner's call). The setup checklist's "Two-step sign-in" asks the viewer, and Users shows each account's two-step status. A link that leaves the dashboard (the install guide) opens in a tab of its own.

Then come the counts (hosts online and off, upstreams, certificates, the live revision; two by two on a phone), the first six hosts with where their requests go and their status (those that need attention first), and the latest five revisions, each linked. "Add host" sits in the header. It reads through `IDashboardConfig` now (testable), and the whole snapshot still travels from the first render into the circuit.

**Drawers instead of dialogs.**
- Adding and changing a user (`UserDrawer`).
- Creating an API token (`TokenDrawer`): its secret shows once in the drawer, which then has no close button and ignores Escape and the backdrop until "I've copied it", and drops the secret as it goes.
- Two-step verification (`TwoFactorDrawer`): the enrolment, new recovery codes behind the password (shown once the same way), and turning it off.
- An audit event's details (`AuditEventDrawer`): outcome and action, who, when, channel, client, target linked, revision, event, the details.

Confirmations (delete a user, revoke a token, end a session, re-issue or delete a certificate) stay dialogs, which sit above the drawers. The users and tokens lists use the shared data table (cards with labelled cells on a phone).

**Drawer stack.**
- A drawer opened from a page without an address of its own (a user, a token, an audit event) closes when the person goes to another page, the browser's back included (`DrawerLevel.Routed` and `OwnerPath`). One with an address closes as its address goes, as before.
- Leaving with unsaved changes asks first, now verified for the browser's back within the app. A back to a page loaded outside the app, a full document load, is not something the page can hold.
- A stacked level's header shows the trail of every level under it ("app.home.test / New certificate / Add an ACME account"), with the way back to the one just under it, so the stack reads on a phone where the levels under it are hidden.
- `DrawerShell.Closable` drops the close button for a drawer that must be left through its own action.

**Gaps from pass 1, closed.**
- A staged upstream or profile opens again from the host drawer (its line under the form has Edit) as it was given (`UpstreamEditModel.FromDraft`, `ProfileEditModel.FromDraft`: still new, same ID) and comes back in its place.
- A review that refuses the one revision of a host and what was staged hands its errors back (`ReviewRequest.OnInvalidDocument`). The host's go to its fields. A staged object's are kept for it, marked "Needs a fix", and shown at its fields when it opens again (the review itself names the object, "app-backend > Address 1").
- A "New ..." picker keeps showing what it had while the stacked drawer is open.

**Tests.**
- bUnit: a staged upstream opened again and changed; a review's refusal of it marked and shown at its field; the trail of three levels; the overview's attention list (order, links, the live warning) and its "Nothing needs you"; the navigation's area for a profile, an ACME account, the users and My account; the tabs per role with the open one marked; the token, user, two-step and audit flows through their drawers (the token's and the codes' drawers not closing on Escape).
- Playwright: a staged upstream opened again; the host's certificate picker unchanged under the certificate drawer; the browser's back with unsaved changes asking, and "Keep editing" keeping them; the users, token and two-step flows and an audit event in drawers. Screenshots of each at 1366 and 390 px.

**Not yet:** History and the simulator keep their two-column pages (no create or edit flow of their own). Changing one's own display name without `users:write` (unchanged).

## Insight pages

### The logs page (M6)

**Logs** sits under Insight beside Audit (route `/logs`, permission `logs:read`: Owner, Admin and Auditor by default, and available to an API token as a scope).

The page reads `ILogFileStore` (`Paduraksa.Application.Logging`), the read-only side the dashboard and the API share:
- `List()`: the writer's names only, never a symlink or anything else in the directory. The live file comes first and rotated ones newest first by the timestamp in their name.
- `ReadTail(name, lines, filter)`:
  - at most 1000 lines, 200 unless asked otherwise;
  - a plain file is read from its last 1 MiB, and a compressed one is decompressed from its start for at most 8 Mi characters;
  - every line is cut at 4096 characters while it is being read, so no line, however long, corrupt or compressed, is ever decoded whole;
  - it returns the last matching lines after an optional case-insensitive filter, with `Truncated` when there was more.
- `OpenRead(name)`: a read-shared stream for the download.

A name is resolved inside the logs directory, and checked to be a log name and not a link before and after opening. `../`, an absolute path or a symlink is therefore a 404, not a read.

The page groups the files by log in a card per log (name, live, rotated or compressed, when it was last written, size). It opens a file in `LogFileDrawer`: a wide drawer with the lines in monospace, a filter field, a 200/500/1000-line choice, a note when only the end was read, and the download. On a fresh install it says "No log files yet" with the directory's path.

Downloading goes through `GET /api/v1/logs/{name}` (streamed, `Content-Disposition: attachment`, `text/plain` or `application/gzip`), so the page has no file access of its own. `GET /api/v1/logs` lists, and `GET /api/v1/logs/{name}/tail?lines=&filter=` gives the same tail as JSON. Both are in the OpenAPI document with `logs:read`.

Reading the logs is a read like the audit log's, so it is not audited.

Tests: bUnit covers the grouping, the empty state, the drawer and its filter, the truncation note and the refusal for a role without the permission. The API tests cover the list, the tail bounds, the download's headers and bytes, roles, path traversal and symlinks. The Playwright settings test opens the page, reads a line in the drawer and downloads the file, at 1366 and 390 px.

### The live tail (M9)

**Live tail** is the next entry under Insight after Logs (route `/logs/live`, permission `logs:read`: Owner, Admin and Auditor, and an API token with the scope).

**Decision: a page of its own, not a tab on Logs.** Logs is a browser of files (a card per log, a drawer per file). A stream is not a file. It has controls a file does not (follow, pause, filters that reshape what arrives). The navigation's longest-address rule keeps both entries marked right (`/logs` is Logs, `/logs/live` is Live tail).

The page reads `LogTailService` (Application): the hub described in [observability.md](observability.md), a subscription per page.

**Table.**
- Newest first. **Decision:** the view stays put at the top while following, with no scroll handling, and pausing is a plain freeze.
- At most 500 rows, each a grid row: time (UTC, to the millisecond); status (a badge coloured by class: 2xx green, 3xx blue, 4xx amber, 5xx red, none grey); method and path with its redacted query (the row's link is the keyboard's way in); host as received; client; time taken; upstream; request ID.
- Below 900 px a row is a card with labelled cells, the request first.
- Rows are components that never re-render (`ShouldRender` false after the first), keyed by sequence. A batch costs the new rows and not 500 diffs.

**Controls.**
- *Pause/Follow* freezes the view while the channel keeps filling to its bound. What overflows is counted: "N lines skipped". "N waiting" says what a Follow will show.
- *Clear* empties the table.
- The *filters*: host (a picker of the configured hosts' first names, from the config when the role may read it, "Any host" and "No host matched"), status (one box taking `404` or a class `5xx`), client IP (an address or its leading part), method, and path contains.
- A text filter applies after 400 ms. Any change opens a new subscription (the backlog of 200 newest matches, then the stream), so the filter runs in the hub, not in the page.
- "Clear filters" shows only while one is set.
- **One filter contract:** `LogTailFilter` (Contracts) holds the status grammar (`TryParseStatus`, shared by the API's two parameters and the page's box) and the 256-character cap on every text. `LogTailService.Open` and `Recent` refuse a filter that fails its `Problem()`, so the dashboard's circuit is held to the API's rules. The page stops its fields at the cap and opens no tail for a longer text a client sends anyway.

**Pushing.**
- The page reads its channel every 300 ms (`PeriodicTimer` on `TimeProvider`; a `BatchInterval` parameter for tests) and renders once if anything changed. A batch of any size is one render, and an idle page renders nothing. A batch larger than the table keeps its newest 500 and counts the rest as skipped.
- The tab's visibility is the existing `paduraksa.pageVisibility` helper (as the home page and the security page). Hidden, the page stops reading, so nothing is rendered or sent to a tab nobody sees. A show reads at once. Disposal unsubscribes.
- **Load**, proven in `ThousandsOfRequestsASecondStayBoundedAndRenderInBatches`: 20 000 entries a second through the hub for two seconds against a page at a 40 ms batch. Renders stay within one per batch, the table holds 500 rows, newest on top and in order, and the skipped count is positive. `ABurstIsRenderedOnceAndTheTableKeepsOnlyTheNewestRows` is the small version. The renderer's own `RenderCount` counts every child (about 35 MudBlazor components a render), so the tests count the page's own.

**A row** opens `LogTailEntryDrawer`:
- status and matched host as chips;
- the request ID in the copy control `SecretValue` gives (clipboard through `DashboardBrowser`);
- the request line, host, client, time taken, upstream with its time, response size, `CF-Ray`, referer and user agent;
- a **Simulate this request** button (for a role that may simulate) linking to `/simulator?url=https://<host><path><query>&clientIp=<client>&method=<method>`.

The simulator learned two query parameters for this (`clientIp`, and `method` when it is one the form offers, next to the `url` it already took). The link's scheme is always `https`, since the entry does not say. The query is the redacted one, so a simulation of a redacted token runs with the literal `REDACTED`.

**Copy.** Two lines say something the data does not: the empty state ("Requests show up as they end. A host with its access log off never does.") and the skipped badge's tooltip. No lead, no eyebrow.

**The stream** (`GET /api/v1/logs/tail`, `logs:read`): `text/event-stream`, `Cache-Control: no-store`, `X-Accel-Buffering: no`.
- The first line is the comment `: open`, so a client knows the stream is live before the first request ends.
- Each entry is a default (`message`) event: `id:` its sequence and `data:` one `LogTailEntry` JSON on one line. The fields are `sequence`, `time`, `clientAddress`, `host`, `matchedHost`, `method`, `path`, `query`, `protocol`, `status`, `responseBytes`, `totalMs`, `upstreamMs`, `upstream`, `requestIdentifier`, `cloudflareRay`, `referer`, `userAgent`. Nulls are omitted. The request ID is `requestIdentifier`, because the architecture rule keeps any `*Id` property of a contract a typed ID.
- A reader that falls behind gets `event: skipped` with `{"count":N}`.
- A comment `: heartbeat` goes out every 15 seconds of silence (`LogTailService.Heartbeat`, which a test shortens), so an idle proxy or client does not time the stream out and a vanished client fails the write.
- Query parameters: `host` (a configured host's name), `unmatched=true`, `statusClass` (`5` or `5xx`), `status`, `ip` (prefix), `method`, `path`, and `backlog` (0 to 1000, default 0, the newest matching entries first).
- Every bad parameter, an over-long text and `host` with `unmatched` are 400 problems. The 33rd stream is a 503 with `Retry-After: 5`.
- There is no maximum duration. The stream ends when the client disconnects (`RequestAborted`), which disposes the subscription (a test checks the hub's subscriber count returns to zero).

**Tests.**
- bUnit (`LiveTailPageTests`, `LiveTailTextTests`, `SimulatorQueryTests`, 46): order and status colours, the empty state, a burst rendered in a handful of renders, pause and what waits, a hidden tab, every filter and the host picker, a bad status, the drawer with the request ID and the simulator link, a role without `logs:read` getting no subscription, disposal, the busy alert, the load test above, the status parser, the navigation entry.
- Playwright (`LiveTailEndToEndTests`): entries arrive over a real circuit, a filter and pause hold, a row opens its entry and leads to the simulator prefilled, the page fits 390 px.

**Visual pass** (a throwaway tour, not committed; dark, light and 390 px, with a 6 000-entry burst, a paused and filtered view, the empty state and the drawer) found and fixed:
- IPv6 clients overflowing their column into the next (ellipsis and a wider column);
- a hidden "Clear filters" button reserving a row of the filter card (it moved to the status line, shown only when a filter is set);
- the drawer's facts stretching to fill the drawer on a phone (the page's own list instead of the chip-style `.pdk-facts`);
- the simulate button crowding the drawer's title (it moved to the footer).

### The security overview (M7)

The first entry of the Security group (`/security`, `config:read`, "Security overview") answers "what is protecting my hosts, and is it working".

**`SecurityOverviewService`** (`Paduraksa.Application.Security`) is registered as a shared singleton at startup, so its `Since` is the process start.
- It reads `ProofOfWorkStats`, `RateLimitStats` and `AccessListStats` (a card of its own under the proof-of-work rejections: refused by address, wrong or missing password, passwords checked, queued, refused busy).
- Per enabled host it reads the *live compiled snapshot*, not the stored document. So it is what the gate is doing, and a revision the compiler rejected never shows.
- It needed three small additions to the compiled model: `CompiledHost.Names`, `CompiledHost.RateLimit` (the host's own policy object, which an inheriting route's chain shares, so a route whose `RateLimitPolicy` has another ID, or none, is an override), and `RateLimitPolicy.PolicyId` and `PolicyName`. Nothing on the request path changed.

**What a host reports:**
- **Proof of work:** its switch and difficulty, plus how many routes differ, either way. Redirect, Respond and Admin hosts report it off, since the gate only runs on proxy routes.
- **Rate limit:** the policy, with the routes that apply another policy or none, grouped.
- **Robots.txt:** the mode.
- **Access list:** read from `CompiledHost.AccessList` and `CompiledRoute.AccessList` (the compiled list each carries), not from the policies in the chain. The chains no longer reveal it once a host with routes on different lists holds only the address-only union. The column names the list, linked to its page, with what it asks of a visitor ("2 users" or "addresses only", from `CompiledAccessList.UserCount`). Routes on another list, or none, are grouped like the rate-limit column's, so "Off on 2 routes" is a route that lifted it.
- `AccessListRef.AddressRanges` (merged ranges, `CidrSet.RangeCount`: `10.0.0.0/8` plus `10.1.0.0/16` is one range) rides along in the DTO for the API. The page does not print it.

The overview is one immutable DTO (`Paduraksa.Contracts.Security.SecurityOverview`), read lock-free and stateless. `GET /api/v1/security/overview` returns it (`config:read`, in OpenAPI) for debugging with a token.

**Polling.**
- The page polls every 2 s with a `PeriodicTimer` (a `PollInterval` parameter for tests) and shows per-second rates computed from the difference between two samples over their `SampledAt` times (blank until the second sample).
- A small JS helper (`paduraksa.pageVisibility`) pauses polling while the tab is hidden. On return it starts over from a fresh sample, so a rate never spans the time away.
- Four compact tiles: challenges served; solved, with solve rate; rejected (the sum of bad solutions, non-browsers and the four cookie reasons); rate-limited, with its share of the requests a limit checked.
- The hosts table: protected hosts by name, then those with nothing on, each cell a muted "None" when empty. A stacked card per host on a phone.
- The rejection reasons.
- No persistence and no background service: rollups are M9.

**Gaps:**
- The permission is checked when the page opens, not on each tick (the session re-check still ends a revoked session's circuit).
- The rate-limit share counts only requests on routes with a policy.
- There is no per-host or per-route counter (the stats are process-wide).

### The Analytics page

**Analytics** is the first entry under Insight (route `/analytics`, permission `metrics:read`, which every role holds, so the page is for everyone who can read the overview). It reads `AnalyticsService` (Application) directly, as the home overview reads `MetricsService`. The numbers behind it are in [observability.md](observability.md).

The header holds a **host picker** (all hosts, each configured host by its first name when the role may read the config, and "No host matched") and the 1 h / 24 h / 7 d / 30 d switch of the overview (default 24 h).

There is no page-wide accuracy note (the owner found it too much). The visitors tile carries its error as its subtitle. A table count a sketch may have overcounted is marked with the approximation sign, and its own tooltip gives the range.

**Top.** Four tiles:
- requests (with the share from bots);
- bandwidth: what was sent and, when anything was uploaded, how much was received (see the body-bytes decision in [observability.md](observability.md));
- unique visitors (with "+/-1.6%", the sketch's standard error, as its subtitle);
- the error rate (5xx).

Nothing is drawn for a range with no requests: one empty state says that a host with its access log off is never counted.

**Visitors and content.**
- Three charts (requests with a 5xx line, bytes sent, unique visitors): the overview's inline-SVG `TimeSeriesChart`, no dependency. **A step nobody observed is a shaded gap and its line breaks. An observed quiet step is a real zero** (the M9 rule, from the service's `coveredSeconds`).
- Bar lists with shares (`ShareList`: a label, a bar as wide as the share, the count, the percent): status classes (green, blue, amber and red by class) and the top exact codes, methods and HTTP versions.
- Tables: top paths (requests, bandwidth, p95, errors), top referers, slowest paths, largest responses.
- A count a sketch may have overshot is written with the approximation sign and a tooltip "Between X and Y".
- A path is a link to the **Live tail filtered to it** (and to the host when the page is for one: `/logs/live?path=...&host=...`). A path the analytics cut at their cap is plain text. The Live tail learned `?path=` and `?host=` for this: a host the picker does not offer is ignored, and a path is cut at the filter's cap.
- **A user agent is not a link.** The tail has no user-agent filter, and adding one would reach into the tail's contract for little.

**Clients and bots.** Browsers (families, with versions in the API), operating systems and devices as bar lists with shares; bots by kind; the top bots with their kind; the bots ignoring robots.txt; and the raw user agents.
- The ignoring-robots empty state says only hosts that serve a robots.txt are checked: the one consequence a person needs to act on.
- The overview's Traffic card links to the page.

**Updates.** The page reads every 10 s while the tab is visible (`paduraksa.pageVisibility`, as the overview) and the longer ranges six times less often. The service keeps a result for 5 s (30 s beyond the hour). A range that cannot be read is an alert with "Try again".

**Tests.**
- bUnit (`AnalyticsPageTests`, 14, over a real database): the empty state, the tiles, the three charts and a gap, the breakdowns with their tones and shares, the tables with links and approximation marks, the clients and bots, the ignoring-robots empty state, the range and host switches (the blog's own name in the tail link), a cut path, a database that is down and recovers, the navigation entry. `LiveTailPageTests` covers the query parameters.
- Playwright (`AnalyticsEndToEndTests`): the page over a real circuit with seeded buckets and a gap, a path leading to the filtered Live tail, the 7 d switch, and 390 px without sideways scroll.
- **Visual pass** (a throwaway tour in dark, light and 390 px) found and fixed: the top-paths table cut off in a three-column grid (the table cards are wider), bar labels truncated ("Search en..."), and the header's actions overflowing at 390 px (they wrap).

## Settings fields

### HSTS fields (M6)

`HostSettingsFields` gained an **HSTS** group, shared by the global defaults, the profiles (and the profile drawer) and the host form. An admin sets it once, and every layer says where its value would come from.

- A switch ("Inherit (off)", On, Off) brings the rest with it.
- **Remembered for:** a select of 5 minutes (for testing), a day, six months, a year and two years, plus custom seconds (`HstsMaxAgePresets`, kept as the number of seconds the config holds).
- **Every subdomain** and **Preload list**, each "Inherit (...)" with the inherited value in words.
- The copy says what a browser remembers and for how long, that a subdomain without a certificate stops working over plain HTTP, and that preload is hard to undo.
- The validator's errors show at the field they are about (the preload pair, the custom seconds).
- The group shows the rows that apply. Inheriting "on" shows them. An explicit Off hides the preload pair. **Remembered for** also shows when the layer sets an age with the switch off, so a number the validator refuses has a field to appear at.
- Cards and profile summaries say "HSTS: 1 year, subdomains, preload", "HSTS when on: ..." when the switch is left to the layer below, or "HSTS off".

### Server header fields (M6)

The same form has a **Server header** group:
- A select with "Inherit (...)" naming the inherited mode in words (a custom value is named), then `Paduraksa (no version)`, `Custom value`, `The upstream's header` and `Remove`.
- A **Custom value** text field appears for the custom mode, and also when the layer already sets a value or the validator refuses one, so an error always has a field to appear at.
- The one helper line says that hiding the upstream's header stops it advertising its software and version. Nothing repeats the label.
- Cards and profile summaries say "Server header: Paduraksa", "Server header: edge/1.0", "Server header: the upstream's header" or "Server header: none". A value without a mode says "Server header value: ...".

### The settings fields bind to the contract (M6)

`HostSettingsFields` works on the contract layer itself: a `HostSettings` where null means inherit, plus what that layer would inherit. It hands back a changed record with `SettingsChanged`. The form page owns it (the global defaults, a profile, a host), and there is no flat form model in between.

- The inherited values are `HostSettingsLayers.MergeAll` of the layers below over `BuiltIn`, so the form cannot promise a default or a merge the proxy would not apply.
- Each field changes its own record with `with`. `HostSettingsEdit.WithTls`, `WithHsts` and `WithServerHeader` collapse a nested record that sets nothing back to null by `ISettingsLayer.SetsNothing`, so clearing a field leaves no key behind.
- `SettingRow` is the row's title, help and control.
- `InheritSelect<T>` is a nullable value as a select whose first option says "Inherit (...)" (or "Built-in default (...)" on the global defaults) in the words of the value the layer below has. It carries the label, `data-field`, error and read-only state every field shares.
- HSTS's "Remembered for" keeps state of its own in a small component (`HstsMaxAgeField`): the preset-or-custom choice, derived from the stored seconds, with a custom number remembered while a preset is picked. (The IP allow list's own field went with the inline list in M7, which the access-list picker replaced.)
- `SettingsSummary` holds one phrase per setting in the contract's order, so the cards and profile summaries need no per-field code elsewhere.
- The per-field `From`, `ApplyTo` and `Overrides` round trips are gone.
- `HostSettingsFieldsTests` is one table over every field: setting it through the control, the record that leaves, the inherited words, clearing it, and an error at its path. A reflection completeness check over the contract is the dashboard's counterpart to `SettingsInheritanceTests`.
- The "Adding a field to the settings UI" checklist in [config.md](config.md) is the whole cost of a new setting here.

## Drafts (M10)

Every edit used to be its own review and its own revision. A host drawer could stage a new upstream or profile, but only inside that one drawer. A **draft** collects edits made anywhere (hosts, upstreams, profiles, rate limits, access lists, rule sets, sign-in providers, the global defaults) and applies them as **one revision** when the admin is ready. A change that spans several pages therefore goes live at once or not at all.

Code: `Paduraksa.Application/Drafts` (`DraftService`, `DraftReplay`), `Contracts/Drafts`, the `ConfigDrafts` tables, `/api/v1/drafts`, and the dashboard's `DraftState`, bar and list.

### What a draft is

A per-user list of **entries**, in the order they were added. Each entry holds:
- an object command (`UpsertHostCommand`, `DeleteUpstreamCommand`, ...);
- the version of the object it was made against (an upsert's `Version`, a delete's);
- the object it is about (`DraftEntry.Target`: `hosts/host_...`, `settings`);
- what it does (create, update, delete);
- plus the revision the draft was started on.

It is not a frozen document.

**One entry per object.**
- A later edit of the same object replaces the earlier one and keeps its place and number (numbers are never reused).
- An edit that leaves the object as the head has it takes the entry out.
- Deleting an object the draft itself creates removes its entry.
- An entry can be removed on its own.
- A whole-document command (replace, rollback) is not an entry.
- A host form's staged objects (an upstream made for it) go in as entries of their own, ahead of the host.

### Stored server-side

In the config database (migration `ConfigDrafts`: `ConfigDrafts`, one row per user with a unique owner and a `Version` that moves on every change; and `ConfigDraftNotices`). It survives a reload, a second tab and a restart. Nothing in it is live, committed or audited until it is applied.

A token acts on its owner's draft (an agent proposes, the person applies).

**Not drafted: what is not config.** Certificates, ACME accounts, DNS providers, users, tokens, notification channels and backup act at once as before (a draft can reference a certificate created meanwhile). So does a YAML import, which replaces the whole document.

### Lifetime

Untouched for 30 days (`DraftService.Lifetime`; every change restarts it), a draft is dropped, with the vault values bound to it, by `DraftExpiryJob`. The job runs once at start, then hourly. A call that meets an expired draft drops it too, so the clock is the only thing the tests drive.

Its owner gets a `DraftNotice` at the next dashboard load: a toast that stays until it is closed ("Your draft with 3 changes was dropped after 30 days untouched."), shown once.

### The head with the draft applied

One read model, `IDashboardConfig.GetWorkingAsync` over `DraftService.GetConfigAsync`: the latest revision with each entry replayed in order (`DraftReplay`). Versions are left as the head's. Nothing is stamped: a form that reads an object of the draft sends back the version the entry will be checked against.

- Every config page and form reads it: hosts, upstreams, profiles, rate limits, access lists, rules, sign-in, global defaults and all their pickers. The overview, history, simulator, analytics and live tail read the live config.
- A picker marks an object the draft creates or changes "Draft" (`DraftChip`, beside "New" for what a host form staged).
- The host list's live warnings index the live document, so they are left out while a draft is open.
- `Watch` also fires, with the latest revision and no changes, when the caller's draft changes in any tab (`DraftService.Changed`).

### Adding an entry checks it

The command is dry-run through the real pipeline (`ConfigPipeline.AttemptAsync` with the draft's base document, a simulation: the same validator, compiler and certificate guard) on the head with the draft's *other* entries applied.
- The errors are the ones a live command gives, relative to the object, and a form shows them at its fields.
- A refused command adds nothing (`FailedCommand` says which of several). Nothing is committed or audited.
- A version that does not match the object in that view is the usual conflict ("changed elsewhere").

### Secrets

A password typed into a drafted access list never reaches the row.
- The entry's user carries a **new** `credentialId` (never the one the list has now: the live password stays until the draft is applied). The password goes to the vault as a credential no config names yet.
- The draft row keeps the list of those IDs (`PendingCredentials`) and nothing else: no secret in the row, the API output, the audit log or any log.
- They are deleted with their entry (removed, replaced by a newer password, taken out because the object went back to the head) and with the draft (discarded, expired). They are never a credential the live config names.
- Applied, they are simply the credentials the revision names, and the credentials it stopped naming go as after any commit.
- The startup sweep of the vault's orphans leaves what an open draft holds.
- A DNS token is not draftable (DNS providers act at once), so access-list passwords are the only draftable secret.

### Review and apply: what the person confirmed is exactly what is applied, or nothing

**Review** (`DraftService.ReviewAsync`) replays the entries on the **current** head under the writer lock and dry-runs the set as one whole-document command. It gives the existing review panel the whole set: the diff grouped by object, the compiler's warnings, the lock-out guard (`?force=true`) and Simulate against the set. It also returns a **review token**.

**Apply** (`ApplyAsync`) must send that token (`If-Match` on the API). **Under the writer lock it replays on the head as it is now, plans again, and commits only if the token of that plan is the one it was given.**
- Otherwise nothing is committed, and the **fresh review** (the plan as it is now, with its token) comes back. That is `DraftStatus.ChangedSinceReview`, and the API's 412 `draft.changedSinceReview` carries it as the problem's `review`. A client can show it at once, and the person confirms again on purpose.
- The commit is **one revision**. The note defaults to "Draft (3 changes): add upstream app, change host x, ..." (editable, 1000 characters at most).
- The audit event is one `config.draft.apply` for the draft, with every entry listed in its details (number, the action it would have been alone, the target, the title).
- **Undo on that revision undoes the whole draft.**
- The draft's row leaves in the commit's own transaction, so a crash cannot leave an applied draft to be applied again.
- A set that does not validate as a whole is refused as any command is (422, errors by place), with no token to confirm, and the draft stays as it was.

### The review token (`DraftReviewToken`)

It is `{draft version}.{22 characters of base64url}`. The hash is SHA-256 over:
1. **The draft's ID and version.** Any change to the draft (an entry added, replaced or removed) makes an older review stale.
2. **For every entry in order:** its number, target and action, and **the object it leaves** (its canonical JSON in the candidate document, or "gone"). That covers the diff field by field without the document positions an unrelated commit would shift.
3. **Every object those entries refer to and the draft does not write** (the upstream of a drafted host, its profile, the lists it names), as it is now. The diff does not show it, and the person still confirmed it.
4. **The compiler's warnings the draft changes**: the candidate's against the live config's, by code and message, counted (their paths are positions). A warning about a host nobody is editing, which the live config has too, is not part of it.

**Choice:** the hash is over what the draft does, not over the head.
- A commit that touches none of that (another host edited or added, a revision number moved, positions shifted) leaves the token as it was, so a busy instance does not make applying impossible.
- A change to an object the draft writes blocks its entry (below). A change to one it refers to, to the draft, or to anything that makes the draft warn differently, does not match.
- What the token does not cover: a change to something neither written nor referred to that would change the diff (which cannot happen, since the diff is of the objects written), or a warning that appears at the same moment in the live config and the candidate (it is not the draft's).

### Someone changed the config meanwhile: blocked entries

**A draft applies whole or not at all.** Replaying on the new head is the merge, with no three-way merge.

- An entry whose object changed since the entry was added (the version check an object command makes), or went away, is **Changed elsewhere**.
- So is every entry that **refers to what a blocked entry would have written**, transitively and whatever the order. Example: the draft creates upstream U under an ID it chose and host H using U. Someone else creates an object with that ID. Both are blocked, H's reason names "add upstream U", and H is not applied on top of their upstream.
- A blocked *delete* blocks nothing that refers to it. What still points at an object that stays is the validator's to refuse (a review of the rest is a 422).
- **If any entry is blocked, apply refuses the whole draft** (`DraftStatus.Blocked`, 409 `draft.blocked` with one error per blocked entry) and commits nothing. A review still shows the rest, without a token.
- The person resolves them. **Reload this entry** is taking it out and opening the object's form on the config as it is now. An edit of the new version is a new entry. Then they review again and apply.
- The draft never silently overwrites another admin's change, and never applies around one.

### API (`/api/v1/drafts`, every operation behind `config:write`)

| Operation | Notes |
|---|---|
| `GET` | Lists the caller's (at most one) draft. |
| `POST` | Opens one (409 `draft.exists`). |
| `GET {id}` | The draft's version as `ETag`. |
| `GET {id}/config` | The head with the draft applied (one replay). Another user's ID is a 404. |
| `POST {id}/entries` | Adds object commands (a list). Each is checked as above, with 422, 412 and 404 like the live command. An access-list password goes to the vault. |
| `DELETE {id}/entries/{number}` | Removes an entry: 204, with the draft's new version as `ETag`. |
| `POST {id}/review` | A dry run. The answer's `reviewToken`, also its `ETag`, is what apply must send. None when an entry is blocked, and 409 `draft.blocked` when all are. |
| `POST {id}/apply` | `If-Match` is **the review token**. 428 without it, with the current review as the problem's `review`. 412 `draft.changedSinceReview` with the fresh review and its token, also as the `ETag`. 409 `draft.blocked`. 409 `draft.nothingToApply` for an empty draft. `?message=`, `?force=`. `?dryRun=true` is the review. The body is a `DraftOutcome`. |
| `DELETE {id}` | Discards. |

Someone else's draft is a 404.

**MCP** classifies by the existing rules (`EndpointRules`, checked against the generated description by `McpRequestPolicyApiTests`):
- The reads and the review (a read-only POST) are allowed.
- Open, add, remove, apply and discard are writes that need `confirm`. An apply without it is the review.
- **An apply with `confirm` must pass the review token it confirmed (`ifMatch`).** Without it, or with another, it does nothing and answers with the fresh review.

### Dashboard

`DraftState` (per circuit) follows the draft.

**The bar.** A slim **bar** under the app bar on every page (`DraftBar`) while a draft is open, for a user who may change config. It shows "Draft", the change count and, when there are, how many are changed elsewhere, with **Review** and **Discard** (which asks).

**Making a change.** Every draftable change takes one path (see "Making a change" under the dashboard conventions):
- A form (host, upstream, profile, rate limit, access list, rule set, sign-in provider, global defaults) has one primary button, **Review changes**. It has no draft button of its own, whether or not a draft is open.
- The **review sheet** it opens (`ReviewPanel`: diff, warnings, lock-out guard, Simulate) is where the person chooses **Apply now** or **Add to draft**. The buttons keep one order and one look, Cancel, Add to draft (outlined), Apply now (filled), whether a draft is open or not. Decided by the owner: a button that changed style with the draft's state read as inconsistent.
- Add to draft opens a draft when there is none. The toast says which ("Added to the draft: ..." or "Started a draft: ...").
- A delete, a host's switch or wrench in the list, a row's delete: the same sheet. Putting a host in maintenance from the list asks first for the mode's options (`MaintenanceDrawer`: status, Retry-After, who is let through, the page, prefilled from what the host inherits), then opens the sheet.
- Applying now a change the draft also holds drops the draft's stale entry. The sheet says so in one line when the draft holds one ("Applying now drops this object's entry from the draft.").
- A change the live config refuses while a draft is open (a host naming an upstream only the draft adds) shows its refusal as a warning, keeps Add to draft, and turns Apply now off. Add to draft checks it on the draft's view and shows a refusal at the form's fields.
- A whole-document change (rollback, YAML import) has no draft choice. While a draft is open its sheet carries one notice (`DraftOpenNotice`): the draft was made on the config as it is, applying this moves it, and entries for the objects it changes become changed elsewhere. **Review the draft first** closes the sheet and opens the draft. The Undo toast asks the same in a confirm.

**The draft drawer is the review.** The bar's Review opens it (`DraftReviewDrawer`); there is no separate step.
- **Entries:** in the order they were added, each a section with its summary line, a toggle, a remove button, and its diff rendered by the same `ChangeList` the review sheet uses (field by field, grouped by object, with Added, Changed and Removed chips). Each entry is applied on the document the entries before it left, so its "before" is the object as the head has it and its names resolve (`DraftEntryDiffs`).
- **Collapse:** a draft of up to 3 entries opens them all (`DraftReviewDrawer.OpenByDefaultUpTo`). A longer draft starts collapsed to its summary lines, so the drawer stays one scroll long.
- **The whole set:** below the entries, the review's warnings, the lock-out guard's errors and Simulate for the set.
- **Footer:** **Apply** (primary), **Discard** (danger, asks) and Close, with the note for the history. Apply sends the review token of exactly what is on screen.
- **Cost:** the review runs once when the drawer opens, and again only when the draft's version or the head moved (the draft state says "changed" on every commit, so the drawer compares the key). It reads the draft service's cached replay; a render reads what the drawer holds.

**When Apply is refused** because the config changed since the review, the drawer stays open and shows the new review **in place**, with one line: "The config changed since this review. This is the review as it is now: check it, then apply." Apply needs a new click on that review, never an automatic one. A change that lands while the drawer is open shows the same way before any click.

**When entries are blocked** (before or after), the drawer says how many ("N changes are changed elsewhere, so nothing can be applied"). Each blocked entry shows its reason and its own **Reload**, the diff of the rest is still shown, and Apply is disabled until they are resolved.

Lists mark rows the draft creates or changes with "Draft".

### Choices where the plan left them open

- One draft per user (a token's is its owner's).
- An entry replaced in place keeps its number and position.
- **A draft is applied whole or not at all.** The first version applied the unblocked entries and kept the blocked ones. It was replaced by the owner's rule that what was confirmed is what is applied.
- The review of a set is a whole-document dry run, so its errors are by place in the document and the lock-out guard applies to the set.
- The token covers what the draft does, not the head.
- A delete of an object something still uses is the validator's to refuse (at the add or at the review).
- **The choice is in the review sheet, not in the form** (owner's rule). A form, a toggle and a delete are all "make this change"; Apply now and Add to draft are what the person then decides, so every control offers the same two, in the same order.
- Quick toggles never apply or draft silently: they open the same sheet. The host list's switch and wrench are drafted like a form's change.
- A toggle's host comes from the head with the draft applied, so a host the draft already changes carries those changes into the sheet (the diff shows them against the live config).
- Maintenance from the list takes its options in a small drawer first, because the mode needs a status, a page and who is let through, not a bare flag.
- Rollback, Undo and YAML import stay immediate whole-document changes with their own review. They are not draftable (an entry is one object), and while a draft is open they say why that matters and offer the draft first.
- Each entry's diff is the entry applied on the entries before it, against the head. An object has one entry, so this is the object before and after.
- A draft of up to 3 entries opens all its entries; more start collapsed.
- Apply on the draft drawer follows the same refusal rule as the old sheet: nothing committed, the fresh review in place, a new click.
- Other open tabs follow a change through the draft's `Changed` event, not a poll.
- A draft made through a token shows for its owner's session at once.

### Cost

- A read replays the draft once per (latest revision, draft, draft version). `DraftService` keeps one replayed view per user (at most 256, then the table starts over). Every render of every page of a circuit therefore costs a head read and a key comparison while nothing changed.
- A row stores each entry's number, action, target, command and time and **not its state or reason**, which are worked out on every read. Rows written before that, which carry a `state`, still read.
- The hourly expiry reads each draft's ID and time first and loads only the expired rows. An expired draft its owner refreshed before the drop was decided under the writer's lock is not dropped (`DropExpiredIdsAsync` says which it dropped).

### Tests

- **Unit** (`DraftReplayTests`, `DraftReviewTokenTests`): replay and ordering; the version check per object kind; changed-elsewhere and deleted-elsewhere; blocking that follows references (transitively, in any order; a blocked delete blocks nothing); versions staying the head's; titles; what the token covers and what it ignores.
- **Data** (`DraftServiceTests`, `DraftSecretsTests` over a real database):
  - one draft per user, entries checked as added and replaced in place, the head with the draft applied;
  - a review that commits nothing, an apply as one revision with one audit event and an intact chain, undo;
  - a review that no longer matches (nothing committed, the fresh review back, a deliberate second apply), no token or an invented one;
  - an unrelated commit between review and apply (still applies) and one to an object the draft refers to (refused);
  - a blocked entry refusing the whole draft, a blocked dependency, the safe twin (a blocked delete leaves a reference: 422);
  - the cache, the expiry race, rows with a stored state, restart, expiry and its notice with a `TimeProvider`, a token acting on its owner's draft;
  - the secrets: never in the row, the output, a revision or the audit log; deleted with the entry, the draft and the expiry; kept by the startup sweep; a refused password stored nowhere; a password replaced within one add never set; a value that stays pending while its entry is blocked.
- **Integration:**
  - `DraftApiTests`: the whole API flow, the review token as `If-Match` with 428 and 412 carrying the fresh review, a concurrent commit to an object the draft refers to against an unrelated one, a blocked entry refusing the whole draft, two users and another's `/config` as a 404, no secret in any response or in the database, permissions, the OpenAPI conventions.
  - `DraftLifetimeEndToEndTests`: restart survival, then an apply as one revision; 30 days untouched on a real database.
  - `McpDraftTests`: a confirmed apply without the token does nothing and answers with the review.
  - `McpRequestPolicyApiTests`.
- **bUnit** (`DraftTests`, `ChangeActionsTests`):
  - the bar; the draft drawer with remove and changed-elsewhere; each entry's diff, open for a small draft and collapsed for a long one; one review per draft or head change, not per render;
  - apply through the draft with its token; the drawer refreshing in place after a refusal and needing a new click; a blocked entry disabling Apply with its Reload;
  - a form with one Review changes button; the sheet's two choices in one order with the primary swapping; the drop-entry line; a refused add shown in the sheet and at the field; a delete; the global defaults;
  - the host list's switch with and without a draft; maintenance from the list (options, then the sheet); a destination's switch; a row's delete; a change the live config refuses while a draft is open; the rollback notice and its Review the draft first;
  - marks on a list and in a picker; a page following a change made in another tab.
- **Playwright** (`DraftsEndToEndTests`): an upstream in one page, a host using it in another (the picker offers it marked Draft), each added from the review sheet; the bar across pages, a reload and a second tab; the draft drawer's entries with their diffs and one Apply, one revision; a host's switch in the list through the same sheet into a new draft; an upstream changed while the draft is open (the drawer shows the blocked entry in place and nothing is applied); and the bar and the drawer at 390 px.
- **Architecture** (`DashboardChangePathTests`): reads the IL of the dashboard assembly and fails on any call to the config's execute, add-to-draft or apply-draft methods outside the review sheet, the undo toast, the draft state and the draft drawer.
- **Visual pass** (throwaway tours in dark, light and 390 px; not committed) found and fixed a changed-elsewhere chip and Reload cut off on a phone, and (the change-actions tour) a draft notice whose button wrapped into its text and a repeated Maintenance heading above the maintenance options.

### Not yet

- The simulator page does not offer a draft as a target of its own (the review panel's Simulate does).
- No merge of an entry's fields with someone else's change (an entry is reloaded, not rebased).
- The YAML import does not go through a draft.

## Notification history

**Settings, Notification history** (`/settings/notifications/history`, `providers:read`) and the delivery figures on the channel list and in the channel drawer are described with the outbox in [observability.md](observability.md). It sits under Settings, not Insight, because it is read and retried with the channels' permissions and linked from the channel list.

## Dashboard conventions (UI consistency pass)

Shared pieces first. A page that needs its own look for one of these is a sign the shared piece is missing something.

**`SegmentedControl<T>`** is the one single-choice-among-a-few control: time ranges, Mine and Everyone's, a host's kind, a token's expiry, the certificate filter.
- Options are `SegmentOption<T>` (value, label, optional icon, tooltip, `data-value` key, count, tone dot).
- It is a `radiogroup` of `radio` buttons. The chosen segment is the one tab stop. The arrow keys, Home and End move the choice (it follows focus, as native radios do, and wraps), and `paduraksa.js` keeps the page from scrolling on those keys.
- It is always one 40 px row beside a dense outlined field, with the chosen segment raised. When the segments do not fit (a phone) it fills the width and wraps onto further rows rather than hiding options behind a scroll.
- Tests find it by `[data-field=...] [data-value=...]` and read `aria-checked`.
- A choice whose options need a sentence each is not a segment: it is `ChoiceCards` (below).
- A segment with a zero count is muted, or left out when it is a filter.

**Page header.** The title (h1) and the actions share one centre line in a row at least 40 px tall, so a title sits at the same height on every page. A lead, only where it says something the page does not, sits under the row.

**Headings.**
- A card's title is h6 (`Typo.h6`).
- A card under a section heading, on a page that has several (Analytics), is `subtitle1`. The section heading itself is h6 with `.pdk-section-heading`.
- A small label inside a form is `subtitle2`.

**Filter cards** (Audit, Live tail) are `.pdk-filters`: an optional bar (a segmented control, "Clear filters" at the end), then the fields in one wrapping grid. Every field is dense outlined and floats its label (give it a placeholder).

**Heights and sizes.**
- Dense outlined fields and the segmented control are 40 px.
- Icon buttons are 36 px (`Size.Small`, never `Medium`) and carry a tooltip or `aria-label`.
- A drawer's header is at least 80 px whether or not it has a subtitle.

**Buttons.** One filled primary action per view. Outlined for a secondary action that stands alone, text for the rest, and destructive actions in the error colour.

**A list page shows its primary action once.** While the list is empty, the empty state carries it (the same size as the header's, `Size.Small` only inside a compact card) and the page header hides its own (`PageHeader.ListEmpty`). Once the list has items the header has it. Both placements keep the one `data-action` hook (and any class a test uses), so a test clicks the same selector either way. An empty state that offers a different, secondary action (History's "Add an upstream") is outlined.

**Labels.** Every outlined text, numeric, select and autocomplete field floats its label on the border, empty or not, so forms never mix floated and resting labels and a row lines up.
- MudBlazor floats it only for a field with a value or a placeholder, and has no global default for `ShrinkLabel` (nor a theme or cascading option).
- So `app.css` applies MudBlazor's own shrink rules (the label's transform, the border's notch) to every outlined field, with no call site setting anything.
- A field with a placeholder shows it (give a field one where an example helps). The label never covers it.
- The dense toolbar and filter fields are unchanged in height.
- `FormLabelsEndToEndTests` holds an empty and a filled field's label to one position.

**Choices with explanations** (`ChoiceCards<T>`, options are `ChoiceOption<T>`: title, one line of help, optional icon). They are used for Import's merge or replace, Global defaults' unknown-host answer and client addresses, the ACME challenge, the DNS provider and the notification service.
- One look: cards in a wrapping grid (at least 190 px each), the title and its help under it, the icon level with the title, and the chosen card outlined and tinted in the primary colour.
- A radiogroup with the segmented control's keyboard behaviour. There is no MudRadio list in the dashboard.
- The help sits on the option, so the setting's own help line does not say it again.
- Tests find a card by `[data-field=...] [data-value=...]`.

**Text a person copies** is `CopyableCode`: the text in one box with the copy control inside its top-right corner, so all such values share one left and right edge.
- `SecretValue` (shown once) wraps it with a dashed, tinted box.
- The MCP snippets, the local CA fingerprint, the TOTP key and the restore command use it too.
- Recovery codes keep their Copy and Download pair.
- A command to run on the server is never a bare `<code>`: it is `CliHint`, which shows the Docker form (`docker compose exec paduraksa paduraksa ...`, or `run --rm` when the server must be stopped) and the bare form for a systemd install, each in a `CopyableCode`.
- The sign-in and setup pages are plain HTML, so their `CopyableCode` is `Static`: a native button that `paduraksa.js` answers. The text is in the markup, so it is never a secret.
- Inline `<code>` stays for a name inside a sentence (a setting, a flag), never for something to run.

**Pills.**
- `.pdk-chip` and `.pdk-pill` are status (one look, tone by meaning: success, warning, error, info, default).
- `.pdk-badge` is the smaller label or count. An icon inside it is sized by the badge, never by a `Style=`; there is no inline style in the dashboard.
- `.pdk-tag` is a neutral tag.
- A row that summarises and filters is a segmented control with counts, never a row of pills. A row that only summarises is stat tiles.

**Share-bar lists** (`ShareList`: status codes, methods, browsers, bots by kind and the rest) are one grid with a subgrid per row.
- The label, bar, count and percentage columns are as wide as the widest row needs, so every bar starts and ends at the same x.
- A tiny share is at least 3 px of fill from the track's start.
- Counts and percentages are tabular and right-aligned.

**Units.** A compact control writes units short ("1 h", "24 h", "7 d", "30 d", "1 y"), with the long form as its tooltip. Prose writes them out ("7 days").

**Making a change.** Every control that changes the config goes through one path, so the choices, labels, order and styling are the same everywhere.
- **Draftable changes** (everything that is a config command on one object or the global settings): a form, a toggle, a row's delete or a menu item builds a `ReviewRequest` and opens the **review sheet**. Nothing applies or drafts silently.
- **The form:** one primary, **Review changes**, then Cancel and Delete (danger, at the start) where they apply. No "Add to draft" in a form.
- **The sheet:** Cancel, **Add to draft**, **Apply now**, in that order. Apply now is always the filled button and Add to draft the outlined one; Apply now is red when the change removes something.
- **Quick toggles** (a host's switch or wrench, a destination's switch in its form) open the same sheet. Maintenance from the list asks for its options in a small drawer first.
- **Acts at once by design** (certificates, ACME accounts, DNS providers, users, tokens, notification channels, backup): their own confirm where destructive, no review sheet, no draft choice. Their drawers use the same footer (`DrawerActions`), so Cancel and the primary button sit where they do in a config form.
- **Whole-document changes** (rollback, undo, YAML import): their own review, no draft choice, and the draft-open notice while a draft is open.
- **The footer is `DrawerActions`:** Delete or another leading action at the start, Cancel, extra choices, the primary last. A drawer does not lay out its own footer buttons.
- **A guard** (`DashboardChangePathTests`) fails when a component calls the config's execute or add-to-draft methods outside the allowed types.

**The connection snippets** of an AI assistant (`McpConnect`) take the token just issued and fill it into the commands. The token drawer shows them open, with the token only in that component's parameter and the markup built from it (it goes with the drawer, as the token's own box does), and says how to turn MCP on when it is off.

## First-run setup

**Security.** Until setup completes, the admin app serves **only** the setup page. That requires a one-time **setup token** printed to the logs and the console (`paduraksa setup-token` reprints it), which stops anyone else on the LAN from claiming the instance.

**The original plan** was a wizard of eight steps:
1. Setup token.
2. **Owner account** (plus optional passkey or TOTP).
3. **Admin access** (port, bind, allowed CIDRs, admin hostname).
4. **ACME account** (email, CA, ToS).
5. **DNS provider** (pick, enter creds, live *Test*).
6. **Wildcard certificate** (`example.com` and `*.example.com`, with live progress through order, TXT, propagation, issued).
7. **Global defaults** (security preset: *Off*, *Balanced* or *Strict*, HSTS, default-host behaviour, robots preset).
8. **First host** (optional, with an upstream reachability test), then Done (summary, "export config" and a backup reminder).

Each step was to be skippable where it makes sense (for example HTTP-01 only, no DNS provider), and the wizard resumable.

### As built (M4)

- Setup is incomplete while no Owner exists.
- **The token.** At startup (the core host, after the config store) the server writes a 256-bit setup token to `<data dir>/setup-token` (mode 0600; a copy readable by others is discarded and replaced).
  - It logs it at Warning on **one line** (the console logger is single-line, so a multi-line message would be flattened).
  - It prints **one boxed banner** to standard output, which `docker logs` captures. The box is sized to its longest line, so the borders always line up. It uses `+`, `-` and `|` when the console's output encoding is not UTF-8.
  - It deletes the file once an Owner exists.
  - `paduraksa setup-token` prints the same banner, or says setup is complete.
- `SetupService.CompleteSetupAsync(token, username, password, displayName)` compares the token in constant time, then checks that no Owner exists and creates the Owner in one write transaction (two simultaneous attempts make one). It audits it and deletes the token. A wrong token is audited.
- `SetupService.IsCompleteAsync` is what the admin app asks.
- CLI:
  - `admin reset-password <user>` reads the password from standard input or a no-echo prompt (never argv), ends the user's sessions and clears a lockout.
  - `admin disable-2fa <user>` turns the second factor off.
  - Both are audited as the `cli` actor.

### The trimmed wizard (M5), and why it is gone

M5 built a trimmed wizard: setup token, Owner, two-step sign-in, ACME account, DNS provider, certificate, first host, done. `SetupWizard` was the step list and `WizardStepper` the rail. The token and Owner stayed the static form (`/setup`, posting to `/auth/setup`), and the rest was `/welcome/{step}`. It used the normal services as the signed-in user, so permissions and audit applied as anywhere else. What was built in it lives on elsewhere:
- **Two-step enrolment** is `TwoStepEnrollment`, shared with My account. It goes through `IDashboardIdentity`, so its password check takes one of the API's derivation slots. The password is asked again, then the QR code of the `otpauth://` URI is drawn as inline SVG by `QrCode` (a byte-mode level-M encoder of our own, checked with zbarimg for every version), with the key shown for typing. A code turns it on, and the ten recovery codes show once.
- **First host's reachability check** is `UpstreamProbe` (Application): a TCP connection within 3 s to the URL's host and port, closed at once, nothing sent or read, reachable or not. It needs `config:write`, and a refusal is audited.
- **Resumable progress** is read from the data, not remembered (`SetupProgress`: the user's second factor, an active ACME account, a DNS provider, a certificate other than the admin's and the root, a host).

**Owner decision (post-M5): no wizard.** The step-by-step wizard after the Owner was more ceremony than help, so it is gone: `/welcome`, `SetupWizard`, `WizardStepper` and the certificate, first-host and summary steps.

Decided:
- The setup page is still the one static form (token, then Owner), and its rules are unchanged: refused through the proxy, the setup token required, the gate open only to it while no Owner exists. It has a password strength hint (`setup.js`, a classic script from this origin; the form works without it).
- On success it sends the Owner to the overview (`SetupCompletedPage` is the home page).
- Everything else is done on its own page, when the person wants. The overview's "Finish setting up" checklist (`SetupChecklist`, from `SetupProgress`, read from the data) lists the open tasks (`SetupTasks`): two-step sign-in, an ACME account, a DNS provider once there is an account, a certificate, a first host. Each links to the page that does it (My account, ACME accounts, DNS providers, Certificates, New host). The checklist stays until they are done or it is dismissed (remembered in the browser's local storage by `setup-checklist.js`, a module from this origin). What the user cannot read is not listed.

**Not done:** the admin-access step and the global-defaults step (the defaults have their settings page), passkeys, "export config" in the setup flow, and the backup reminder (a backup can be made from Settings > Backup, but the checklist does not ask for one).

**Tests.**
- bUnit for the checklist's tasks and links, and the upload, ACME account and DNS provider forms (validation, secrets cleared, write-only), the test verdicts and the progress list per state.
- Playwright (`FirstRunEndToEndTests`): token, Owner, the overview with its checklist, two-step from My account through the QR step, a local-CA certificate and an uploaded PEM from the certificates page, and the checklist with only what is still open.
- The earlier wizard's Playwright test (`SetupWizardEndToEndTests`) covered a DNS provider added and tested: no fake is reachable through the vault's handler, so the verdict is the real API's or a network failure, and the test checks that a verdict shows and the token does not. A PEM made by openssl was uploaded (skipped without it).


