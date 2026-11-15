# The dashboard

For contributors: how the Blazor dashboard is built, the conventions every page follows, and why.

## In short

- A Blazor Web App (interactive server render mode) on the admin port, in `Paduraksa.Dashboard`, a Razor class library that references Application and Contracts only. Component library: MudBlazor 9. Nothing in a component holds a secret longer than the step that shows it.
- **One session** serves the API and the dashboard (`__Host-paduraksa-session`). Pages carry `[Authorize]`, a circuit re-checks its session by ID, and the services still decide every permission.
- **Every config change goes through the review drawer**, which offers **Apply now** or **Add to draft**. Nothing applies or drafts silently. A **draft** collects edits from any page and applies them as one revision.
- **Drawers, not pages, for create and edit.** They stack, have addresses, and guard unsaved changes.
- **Live by default** over the circuit: lists follow commits through change feeds, and statistics pages poll while the browser tab is visible. Nothing on the request path waits on the dashboard.
- **Shared pieces carry the look** (`SegmentedControl`, `CopyableCode`, `ShareList`, `DisplayFormat`, `TimeSeriesChart`). If a page needs its own look for one of them, the shared piece is missing something.
- **Copy is a tool's voice:** plain statements in a fixed vocabulary (see [Voice and vocabulary](#voice-and-vocabulary)). A line of copy stays only if it tells the person something the title, navigation or data does not.
- Code: `Paduraksa.Dashboard` (pages, components, drawers), `Paduraksa.Admin.Web` (form endpoints, pipeline hooks), tests in `Paduraksa.Tests.Dashboard` (bUnit and Playwright).

## Contents

- [Stack and principles](#stack-and-principles)
- [The foundation](#the-foundation)
- [Config pages](#config-pages)
- [Drawers and the design language](#drawers-and-the-design-language)
- [Navigation and the overview](#navigation-and-the-overview)
- [Certificates, ACME accounts and DNS providers](#certificates-acme-accounts-and-dns-providers)
- [Settings, My account and the audit log](#settings-my-account-and-the-audit-log)
- [Insight pages](#insight-pages)
- [Settings fields](#settings-fields)
- [Drafts](#drafts)
- [Notification history](#notification-history)
- [Dashboard conventions](#dashboard-conventions)
- [Voice and vocabulary](#voice-and-vocabulary)
- [About Paduraksa](#about-paduraksa)
- [First-run setup](#first-run-setup)

## Stack and principles

**Stack.** A .NET 10 Blazor Web App in interactive server render mode, served on the admin app only. The component library is **MudBlazor** (the most complete, with good data grids and dialogs); Fluent UI Blazor was the alternative. Charts are our own inline SVG (`TimeSeriesChart`).

**UX principles:**
- **Live by default.** Status dots, request rates and certificate expiry update over the existing circuit. The overview, security overview, Analytics, upstream health, the live tail and notification history read on a timer while their page is open.
- **Every change goes through review.** An edit form leads to a "Review changes" drawer (diff, validation and warnings like "this host will lose its certificate"), then Apply. Undo in a toast rolls back to the previous revision.
- **Inheritance is visible.** Every setting shows a badge for *Inherited*, *Profile* or *Overridden*.
- Keyboard-first forms, dark and light themes, a mobile-friendly layout. A command palette (Ctrl or Cmd + K) is not built.

**Pages:**
1. **Overview:** what needs the person, a live strip of the last hour, the counts, the hosts and the recent changes.
2. **Hosts:** a searchable list with status and certificate state. A host is edited in a drawer.
3. **Request simulator ("Why was this blocked?"):** enter a URL, IP, UA and headers and see the exact decision chain, against the live config, an old revision, the draft or the change being reviewed. The same function is on the API and MCP, and access-log entries link into it (see [hot-path.md](hot-path.md)).
4. **Certificates:** list, issue, upload, local CA, ACME progress.
5. **Upstreams and health:** destinations, probe results.
6. **Security:** overview, rate limits, access lists, rule sets, auth providers.
7. **Insight:** Analytics, Logs, Live tail, Audit.
8. **History:** revision timeline, diff viewer, rollback.
9. **Settings:** global defaults, profiles, DNS providers, ACME accounts, notifications, users and roles, API tokens, sessions, backup, server settings, YAML export and import.

## The foundation

**Stack and wiring.**
- `Paduraksa.Dashboard` is a Razor class library that references Application and Contracts only. MudBlazor 9 works within the CSP below and ships its assets in the package.
- `AdminApp` adds it after the API: `UsePaduraksaAdminApi`, then `UsePaduraksaAdminAuth`, then `UsePaduraksaAdminWeb` (authorization, antiforgery and the dashboard's pipeline hooks), then health, identity and the resource API, then the sign-in form endpoints (`MapPaduraksaAdminWeb`), `MapStaticAssets`, and `MapPaduraksaDashboard` at `/`.
- The dashboard owns its paths (`DashboardRoutes`). The server hands them to `Paduraksa.Admin.Web` as `AdminWebOptions`, so neither project references the other.
- Static files (`blazor.web.js`, MudBlazor's CSS and JS, the dashboard's own `app.css`, `paduraksa.js`, `favicon.svg`) come from the app with fingerprinted URLs. No CDN, no web font.
- The server sets `RequiresAspNetWebAssets` (its own project has no `.razor` file, so the SDK would otherwise leave `blazor.web.js` out) and maps a build output's static web assets itself (the host does that only in Development).

**Authentication.** One session serves the API and the dashboard: the same `__Host-paduraksa-session` cookie, set only on ordinary HTTP responses.
- The sign-in, second-factor, sign-out and setup pages are plain HTML forms (static rendering, `[ExcludeFromInteractiveRouting]`). They post to form endpoints in `Paduraksa.Admin.Web`: `/auth/signin`, `/auth/second-factor`, `/auth/signout`, `/auth/setup`.
- These run the API's own steps (`SignInFlow`: throttle, bounded password derivation, lockout, audit, cookie; channel `dashboard`). They need the antiforgery token the page rendered (`__Host-paduraksa-antiforgery`, `SameSite=Strict`) and pass the CSRF middleware's same-origin check (`RequireSameOrigin`).
- They answer with a 303 carrying only an error code (the API's problem codes, plus `setup.passwordMismatch`) and a local `returnUrl` (anything else becomes `/`).
- Outside `/api` the authentication middleware reads the session cookie leniently: valid is the caller (`HttpContext.User` is a `SessionIdentity`), partial is the partial session, anything else is anonymous. A bearer token never counts.
- Pages carry `[Authorize]`, and the `Paduraksa.Session` scheme's challenge redirects to `/signin?returnUrl=...`. A TOTP code is accepted with the space authenticators show.

**Circuits and revocation.**
- `DashboardAuthenticationStateProvider` takes the request's `SessionIdentity` (the circuit never holds the cookie) and adds the user's permissions. `DashboardPrincipal.Has(Permission)`, `IfPermitted` and the navigation hide what the user cannot use. The services still decide.
- It re-checks the session by ID (`SessionService.CheckAsync`): in the background every `Paduraksa:Admin:SessionRecheckInterval` (30 s; it does not count as use, so an open page still idles out), on circuit activity at most every 10 s, and on every service call (`DashboardCaller`, which counts as use).
- A revoked or expired session, a disabled or deleted user, or a password change elsewhere makes the state anonymous. The router then reloads the page to `/signin?notice=sessionEnded`, and a circuit still talking 10 s later is torn down (`SessionCircuitHandler`). Changed roles change the permissions on the next check.

**Setup gate.** While setup is incomplete the gate redirects a browser opening any page (a `GET` accepting `text/html`) to `/setup`, and opens only `/setup`, `/auth/setup`, `/_framework` and `/_content`. Scripts still get the 503 problem, and the circuit hub stays shut.

**Hub, proxy, CSRF.** `/_blazor` needs a signed-in caller (else a bare 401) and passes the same-origin check on every method, the WebSocket handshake included. Through the proxy the circuit's WebSocket is an ordinary upgrade on the internal listener, with the hop secret checked there.

**CSP and headers.** HTML documents outside `/api` get (`DashboardSecurity`; the hook `AdminPipelineHooks.HtmlContentSecurityPolicy`):

`default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; manifest-src 'self'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`

- No inline script, no `eval`.
- `'unsafe-inline'` for styles is MudBlazor's need (style attributes and the theme provider's `<style>` of CSS variables). It applies only to the dashboard's pages. The API, the static files and the Scalar docs keep their own policies.
- Blazor's own `frame-ancestors` header is turned off (the full policy covers it).
- Pages and the hub are `Cache-Control: no-store`. Fingerprinted files keep their long caching. HSTS, `nosniff`, `no-referrer` and `X-Frame-Options: DENY` are as for the rest of the admin.
- The browser test fails on any CSP report in the console.

**Look.** A custom MudBlazor theme (`PaduraksaTheme`): slate neutrals and one jade-teal accent (`#3FBFAE` dark, `#0E7D6F` light), the platform's UI font on a small regular scale, 10 px corners and outlined cards.
- Dark is the default. With no choice of their own a person gets their system's setting (a script records it in a cookie so the next page renders right from the first byte). The top bar's toggle stores their choice (`paduraksa-theme`).
- The shell is a top bar (mark, product name, theme toggle, user menu), a responsive side navigation that folds away below the `md` breakpoint, and pages under a `PageHeader`.
- `EmptyState`: icon, one line of what is missing, one of what to do, one action. MudBlazor skeletons while loading. Snackbars bottom-right, newest on top, no duplicates. The sign-in pages mirror the palette in plain CSS.

**Data Protection.** The admin's key ring lives in the vault (see [vault-and-certificates.md](vault-and-certificates.md#the-data-protection-key-ring)), so antiforgery tokens and circuits survive a restart.

**Tests.** bUnit covers the sign-in forms, the auth state provider (permissions, revocation, role changes, the re-check), permission-based visibility and the theme. A Playwright smoke test runs against a real `PaduraksaHost` (setup, TOTP enrolment, sign-in, a revoked session ending the open page, a phone-sized window). It skips when no Chromium is installed: `PADURAKSA_E2E_CHROMIUM=/path/to/chromium` (or `playwright.ps1 install chromium`); `PADURAKSA_E2E_SCREENSHOTS=<dir>` keeps screenshots. Integration tests cover the pipeline (gate, forms, antiforgery, hub, CSP) and the dashboard through the proxy.

## Config pages

**Seam.** `IDashboardConfig` (`Paduraksa.Dashboard/Config`) is everything the config pages ask of the Application layer: the current config and its commands (dry runs too), the history timeline (`RevisionQueryService.ListWithChangesAsync`: revision pages with each revision's change list, read from the stored summary without decompressing a document), a revision with its document, the certificate listing, the user list (to name who made a revision; empty without `users:read`), the simulator, and `Watch` over `ConfigService.Changes`.

`DashboardConfig` is the real one, per circuit; bUnit tests swap in an in-memory one. Reads are not authorized by the services (as on the API): each page checks `config:read` (`certificates:read`, `simulate`) first. Commands and simulations carry the user's `CommandOrigin`, and the services decide. The config pages read the head with the draft applied (see [Drafts](#drafts)).

**Live.** Every page loads once its circuit is live (the prerender shows the skeleton) and re-reads on each commit through `Watch`.

**A page loads nothing while it is prerendered.** Its lifecycle method returns while `!RendererInfo.IsInteractive`, so the prerender is the skeleton and the header has no buttons. A page that loaded during the prerender showed an Edit or Add button before the circuit had a handler for it, and a click made then was lost. A page with only links (`Home`) may load. Two tests hold this: `DashboardMarkupConventionTests.NoPageLoadsItsDataWhilePrerendering`, and `PrerenderEndToEndTests` (Chromium with a watcher on every DOM change; fails if the prerendered document had a button, a `data-action` control or a clickable row).

An open editor says when its object changed elsewhere (with Reload); the Apply would be a conflict anyway. No timer polls.

**Typed IDs at the edge.** `/hosts/{Key}` and `/upstreams/{Key}` take a string segment and parse it into a `HostId` or `UpstreamId`. Anything else, a wrong-type ID included, is "not found". `?revision=` parses into a `ConfigRevisionId`. Addresses are built from typed IDs (`DashboardRoutes.Host(id)`), and new objects get fresh typed IDs in the form models.

**Review before apply.** Every edit opens "Review changes" (`ReviewPanel`, a level of the drawer stack): the command runs as a dry run on the current head and shows:
- the diff (`ChangeView`): grouped by top-level object, routes and destinations nested, also those an added or removed object carries. Each field shows old and new, read from the two documents, with references by name;
- the warnings: the compiler's, placed by object name, plus "will lose its certificate", "will stop answering", names given up, an upstream left without destinations;
- the errors by place. An object command's errors also go back to the form's fields.

Apply runs the same command with the version the form opened with.
- A stale version or a deleted object is a conflict with Reload.
- A commit that could not go live stays on screen as "saved as #N, but it isn't live" with the publisher's errors.
- A commit leaves a toast with **Undo** (`ConfigUndo`), "Applied: host app.example.com added. Live as #4." (the review request's `SuccessMessage` is the part after "Applied: ", without a verb of its own; a draft says "Applied: 3 changes"). Undo is a rollback to the parent revision (a replace with the empty config for #1), based on the revision it undoes. It is refused when someone changed the config in between. A newer toast replaces the last, since only the latest change can be undone.
- The drawer sits above the toasts, closes with Escape and focuses its heading.
- "Simulate this change" runs a request against the change in place, and the change stays offered to the simulator page (`ReviewedDraft`) until it is applied. In the draft drawer, "Simulate the draft" targets the stored draft (`{kind: draft}`), and the simulator page offers the draft as "My draft".

**Forms.**
- **Hosts:** names; kind (proxy: routes with match, path and upstream; redirect: target, status and path; fixed: status, content type and body; or admin); the certificate binding (auto, specific from the usable certificates, or none, which lifts an inherited HTTPS redirect); the HTTPS redirect, showing what it inherits; the profile; under Advanced: the TLS minimum, TCP ALPN, HTTP/3, an IP allow list, tags and notes. The form keeps what it does not show (a route's own settings).
- **Upstreams:** destinations (IDs kept, so YARP health survives), load balancing, active and passive health, the connect and idle timeouts (in seconds, blank for the default, 0 for no idle limit) under Advanced, a connection limit and notes. The hosts and routes that use it sit under the form. Deleting one in use is refused up front with the routes named.
- **Inline validation** is the real `ConfigValidator` over the current config with the edited object put last (as the command pipeline validates it). Errors show once a field is touched, all of them (with a summary) after a review attempt. Enter submits to review.

**Lists.**
- **Hosts:** searchable, with a status dot (off, a live warning or a certificate problem, serving), where requests go, HTTPS behaviour, and the certificate state of the names (`HostOverview`: auto against the usable inventory, exact or parent wildcard; specific by ID; expiring within 14 days).
- **Upstreams:** destinations, policy, health and usage.

**History.** The timeline shows number, who ("You", a user's name with `users:read`, a token, the CLI, Paduraksa), channel, time, message, and what changed in counts, with "load older". The selected revision's diff is against its parent or any loaded revision. Roll back goes through the drawer, based on the latest. Simulate is there too.

**Simulator.** URL, client IP, method and headers, with arrival, proxy address, TLS version, ALPN and SNI under Advanced. The target is live, a revision (`?revision=`), "My draft" while the user has a draft with entries (`?target=draft`; it falls back to live when the draft goes), or the change under review. The outcome comes first in its tone, then the destinations with live health, the notes, and the trace as a vertical list of steps with verdict icons, summaries and facts. A refused request marks its fields. It takes the query parameters `url`, `clientIp` and (when the form offers it) `method`, which the live tail's "Simulate this request" link fills.

**Permissions.** Add, edit, delete, roll back and Apply show only with `config:write` (a read-only form otherwise). The simulator needs `simulate`. The certificate picker and states need `certificates:read`. The services still refuse on their own.

**Errors and 404.**
- An `ErrorBoundary` around the page body turns an exception while prerendering or live into an error card in the shell (logged by the framework, cleared by the next navigation) instead of the API's JSON 500. A broken circuit shows Blazor's error bar, styled.
- An address nothing answers, asked for by a browser (GET or HEAD accepting HTML, outside `/api`, the hub and the static files: a route constraint, so the API keeps its 405 and 415), renders the app with a 404 and the router's not-found page for a signed-in user and challenges anyone else.

**Secrets.** None reach a component. The pages hold Contracts DTOs only (config documents, certificate public facts, revision metadata).

**Not built:** route reordering, host tags as list filters.

## Drawers and the design language

Creating and editing happens in drawers that stack, so making a host with a new upstream and a certificate happens in one place.

### The stack

`DrawerStack` (per circuit, `Paduraksa.Dashboard/Drawers`) holds the open levels. `DrawerHost`, once in the layout after `MudLayout`, renders them.
- Each level is a sheet from the right: 520, 640 or 780 px, the whole width of a phone.
- The top one is live. The ones under it are shifted 28 px aside per level, dimmed by the top level's backdrop and `inert`. The page behind is inert too while any drawer is open.
- A component opens a level with `Push<TComponent>(parameters, width)` (or `OpenAsync`, which waits). A level's header shows the trail of every level under it ("app.home.test / New certificate / Add an ACME account") with the way back to the one just under it, so the stack reads on a phone.

The level's component gets the `DrawerLevel` as a cascading value:

| Member | Effect |
|---|---|
| `Close(value)` | Hands a result back to the level below and closes it. |
| `Cancel()` | Closes without a result. A level closing takes the levels above it along, cancelled. |
| `IsDirty` | It holds unsaved changes. |
| `Busy` | Keeps it open while it applies, or while an issuance shows its progress. |
| `ResultOnClose` | What it hands back however it is closed (a commit that could not go live). |
| `Routed`, `OwnerPath` | A drawer opened from a page without an address closes when the person goes to another page, the browser's back included. |

**Closing.**
- Escape, the backdrop and the close or back button act on the top level only. `paduraksa.js` hands Escape over while a drawer is open and no list, menu or dialog is, wherever the key lands (as a drawer opens, focus is still on the control that opened it, on the inert page behind, until the browser's next frame).
- When the level is dirty, they ask "Discard your changes?" in the drawer itself (Escape again keeps editing). Leaving the page with a dirty drawer asks the same (`NavigationLock`), also for the browser's back within the app. The page can't hold a back to a page loaded outside the app.
- Focus moves into a level as it opens (its first field, else its title) and back to what opened it as it closes. A drawer with an address opens by navigating, and the router then focuses the page's heading, so `paduraksa.js` remembers the control the person just activated and returns focus to it (`DrawerFocusEndToEndTests`).
- `DrawerShell.Closable` drops the close button for a drawer that must be left through its own action (a new token's secret: it ignores Escape and the backdrop until "I've copied it").

**Layering.** Dialogs (a confirmation asked from a drawer) sit above the stack. The theme raises MudBlazor's dialog z-index to 1700 (tooltips 1800). The stack is at 1600 and up, above the toasts.

**A drawer that throws.** Each level has its own `ErrorBoundary` (`DrawerHost`). The failed level shows `DrawerError` ("This drawer couldn't be shown", a compact message and Close). The page and the levels below keep running. The details are in the server log.

### Review is a level

`ReviewPanel` is "Review changes" as a level. `DrawerStack.ReviewAsync(ReviewRequest)` returns a `ReviewOutcome`: applied, or reload asked. Save in a form drawer stacks it on the form, and Apply closes both. `ReviewDrawer` keeps its old parameters for the pages whose form is not a drawer (global defaults, a rollback) and pushes the panel for them.

### Frame

`DrawerShell` is every drawer's frame: the way back to the level below ("< New host"), an overline, the title with an icon, actions beside the close button, banners that do not scroll away (changed elsewhere, read-only, in use), a scrolling body, and a footer for the actions (`DrawerActions`: Delete or another leading action at the start, Cancel, extra choices, the primary last). With `OnSubmit` the drawer is one form, so Enter submits. A drawer does not lay out its own footer buttons.

### Hosts, upstreams, profiles

`HostDrawer`, `UpstreamDrawer` and `ProfileDrawer` (`Paduraksa.Dashboard/Editors`) replace editor pages.
- They share `DraftDrawer<TModel, TConfig>` for the life every draft drawer has: the caller and its permissions, the document, inline validation, the dirty guard, review-before-apply, "changed elsewhere" and reload.
- Every reference in the host form is a picker of what exists with "New ..." last (`RefPicker`): a route's upstream, the certificate (Automatic, the usable ones, None) and the profile.

**Staging.** "New upstream..." and "New profile..." stack the other form in stage mode. It is checked against the host's draft and handed back without being applied. The host form picks it, marks it "New" and lists what goes with the host.
- A host with staged objects is reviewed and applied as one `ReplaceConfigCommand` revision. `StagedChange` is the latest document with the host put in its place and the staged objects it still uses added (one it no longer routes to is dropped). The revision's message is "New host app.example.com with upstream app". Its audit action is `config.replace`.
- The same version check an object command makes applies. A conflict at apply rebuilds the replace on the latest config, keeping what was staged. Cancelling the host drops what it staged. A host without staged objects goes as `UpsertHostCommand`.
- A staged upstream or profile opens again from the host drawer (its line under the form has Edit) as it was given (`UpstreamEditModel.FromDraft`, `ProfileEditModel.FromDraft`: still new, same ID). A review that refuses the revision hands its errors back (`ReviewRequest.OnInvalidDocument`): the host's go to its fields, a staged object's are kept for it, marked "Needs a fix".
- "New certificate..." stacks the certificate flow (`CertificateCreateDrawer`) with the host's names. A certificate is a vault object, so it is issued or uploaded at once and comes back selected. A pending ACME certificate leaves the binding on Automatic, which picks it up once issued.
- A form is dirty when what it would send differs from what it opened with (the canonical JSON of the objects, `StagedChange.Fingerprint`). A staged object makes it dirty.
- The upstream form checks a destination on request (`UpstreamProbe`: a TCP connection, reachable or not).

### Certificates, ACME accounts, DNS providers in drawers

- A certificate opens in `CertificateDrawer`. It reads the inventory itself and follows its changes, so its address opens it from anywhere. Renew now stacks the progress on it. Re-issue and delete ask in a dialog over it.
- "Add certificate" opens `CertificateCreateDrawer` with a choice of where it comes from: an ACME CA, the local CA, self-signed, or upload. While an issuance shows its progress, the drawer ends only with Done or Continue in the background. A certificate made from the drawer closes it and opens that certificate's own drawer before the list is read again (the read waits on the vault).
- In the ACME form, the account and the DNS provider are pickers with "New ACME account..." and "New DNS provider..." (or a button when there is none). They stack `AcmeAccountDrawer` and `DnsProviderDrawer` and come back selected.

### Addresses

`RoutedDrawer` ties a list page's route key to its root drawer. These addresses are the list with that drawer open: `/hosts/{host_...}`, `/hosts/new`, `/upstreams/{ups_...}`, `/upstreams/new`, `/settings/profiles/{prof_...}`, `/settings/profiles/new`, `/certificates/{crt_...}`, `/certificates/acme/{acct_...}`, `/certificates/dns/{dnsp_...}`, `/settings/tokens/{tok_...}`, `/settings/notifications/{chan_...}` and `/settings/users/{user_...}` (the list alone, with no drawer and no link, for someone who may only read).
- A row opens its address. Closing the drawer goes back to the list's address. The browser's back closes it. Leaving the page closes it with every level stacked on it.
- **From one drawer to another.** A link in a drawer is an ordinary link to the target's address. The dirty guard asks first. On another page the new page opens its drawer and the old page's levels close; Back returns to the first address, whose drawer opens again. The new page pushes its level before the old page is disposed, so closing a page's drawer takes only its own levels (`DrawerStack.RemoveOwned`, stopping at the next level that has an address). A drawer with no address (the audit event's, a log entry's) is closed by `DrawerHost` when the person leaves its page.
- The key is parsed at the edge. One that is not an ID of the right type opens a "not found" drawer (`NotFoundDrawer`), and an object deleted under an open drawer says so in it.

### Lists and the design language

- **Hosts** read like nginx-proxy-manager's: the names (more as chips, and tags), where requests go line by line ("/api -> app  http://10.0.0.5:8080"), the certificate state with the HTTPS behaviour, and the status (Online, Attention with the reason as a tooltip, Off) with an on/off switch for writers (not on the admin host) that goes through review.
- **Upstreams:** name and policy, the destinations' addresses, and the hosts that use each (links). **Profiles:** what each sets, the hosts that use it, delete.

Shared pieces:
- `PageHeader`: the title, an optional lead, and the actions with the primary last. No overline, since the navigation and the data already say where a page is.
- `ListToolbar`: search, how many match, the list's own controls. Hidden on an empty list.
- The data table (`pdk-dt`): rows as grids with columns per list, the main cell's link as the keyboard's way in, and cards with labelled cells below 900 px.
- `EmptyState`, `StatusChip` (a state in a word, tone and dot or icon), `FormSection` (a heading, an optional line, an optional control, the fields; sections are divided by a rule instead of cards), `RefPicker`, `DrawerShell`.

A line of copy stays only if it says what the title, the navigation or the data does not, and helps the person decide or act.

## Navigation and the overview

**Where things live.**
- The side navigation lists areas, and an area's pages share a row of tabs under the page header (`SectionTabs`, through `PageHeader.Area`): **Hosts, Upstreams, Profiles**; **Certificates, ACME accounts, DNS providers**; **All settings, Global defaults, Users and roles, API tokens, My account**.
- Profiles, ACME accounts and DNS providers are found where they are used. Their addresses under `/settings` are unchanged.
- The navigation marks a page's area by the longest address it starts with (`DashboardNavigation.ActiveFor`: a profile marks Hosts, an ACME account Certificates, My account Settings, `/logs/live` Live tail). It is drawn as plain links, not MudBlazor's nav links.
- The settings hub groups its links by what they are about (Hosts, Certificates, People and access), shown only with the permission each page needs. The user menu links My account, API tokens and About.

**Overview.** First what needs the person, most serious first, each with the drawer that fixes it (`HomeSnapshot.AttentionOf`):
- a certificate that failed;
- a certificate in use that ends within 30 days (red inside 7);
- a host the live config warns about or whose certificate is a problem;
- an upstream a host uses with no destination switched on;
- a master key beside the vault, for a user who holds `vault:manage` (threat model G3);
- a degraded config store;
- or nothing, and the card is not shown.

A missing second factor is not on the overview (threat model G4, the owner's call). The setup checklist's "Two-step sign-in" asks the viewer, and Users shows each account's two-step status. A link that leaves the dashboard opens in a tab of its own.

Then come the traffic strip, the counts (hosts online and off, upstreams, certificates, the live revision; two by two on a phone), the first six hosts with where their requests go and their status (those that need attention first), and the latest five revisions, each linked. "Add host" sits in the header. It reads through `IDashboardConfig`, and the whole snapshot travels from the first render into the circuit (`[PersistentState]`) so it does not flash. A degraded start shows a banner on every page.

**The traffic strip** (`Components/TrafficStrip.razor`, `metrics:read`) is the overview's only traffic: four tiles, always the live hour, with no range picker.
- **Tiles:** requests per second, errors (the 5xx share), refused per second and latency p95. Each figure is the pace of the last 30 s (`MetricsService`'s "now"), with a sparkline of the last hour from the sampler's ring (a compact `TimeSeriesChart`: the line alone, no axes, grid or time labels; the tooltip still gives each step's time and values, and gaps are still shaded).
- **Link:** the whole tile is one link (`SecurityTile.Href`, stretched by CSS, with the sparkline above it so its tooltip works). The refused tile goes to the Security tab, `/analytics?tab=security&range=1h`; the others go to `/analytics?tab=traffic&range=1h`. Analytics has no host filter in its address, so none of these links names a host.
- **Polling:** every 5 s while the browser tab is visible (`paduraksa.pageVisibility`), and at once on return. The hour is the sampler's memory and reads nothing that can fail, so the strip has no error state. A sparkline appears once the hour has two steps.
- **Where it sits:** first after what needs the person, above the counts (the owner wanted the charts at the top); about 150 px tall (two rows of tiles on a phone).

**Drawers instead of dialogs** for adding and changing a user (`UserDrawer`), creating an API token (`TokenDrawer`), two-step verification (`TwoFactorDrawer`) and an audit event's details (`AuditEventDrawer`). Confirmations (delete a user, revoke a token, end a session, re-issue or delete a certificate) stay dialogs, which sit above the drawers.

**Not built:** History and the simulator keep their two-column pages (no create or edit flow of their own).

## Certificates, ACME accounts and DNS providers

### Certificates (`/certificates`)

One list of the inventory plus the process's built-in certificates (the development placeholder). It shows name and names, source (ACME, uploaded, self-signed, local CA, built-in), status (an issuance in progress shows as "Issuing"), a colour-coded countdown (green, amber inside 30 days, red inside 7 or after) with the date, the issuer and the hosts that use it, and counters on top (active, expiring, failed or expired, pending). It has skeletons, an empty state, and cards with labelled cells below 900 px.

Which hosts use a certificate is `CertificateUsage` (Application): the compiler's selection (Specific, the best match per name of every enabled Auto host, the default) applied to the metadata. It is shown only with `config:read`.

**Two cards.**
- The admin certificate is a read-only card: its names and countdown, "add names with `Paduraksa:Admin:CertificateNames`".
- The local CA root has a "Trust the local CA" card: name, validity, SHA-256 fingerprint, the `/api/v1/ca/root.crt` and `root.pem` downloads (`download` links, which Blazor's router leaves alone), and how to install it on Windows, macOS, Linux, Firefox and phones. The Local CA page shows a skeleton in place of its two cards until it has loaded.

**Detail drawer.** A row opens it: status, validity bar, names, users, issuer, subject, key, serial, fingerprint, the chain's issuers and the public chain PEM. For ACME it adds the account, challenge, order state, next renewal, the ARI window, the last attempt, and a failure with the CA's message and the next try.

### Actions (with `certificates:write`; the services still decide)

- **Upload** a PEM (chain and PKCS#8 key), a PEM plus a separate key file (read as one stream through `PemConcatStream`, nothing buffered), or a PFX with an optional password.
  - **This goes through the circuit, not the API's multipart endpoint.** `InputFile` streams the browser's file into `CertificateOperations.UploadAsync` during the call, capped at `CertificateImportLimits.MaxBytes`.
  - The password leaves the component's state before anything is awaited and is handed over as a `char[]` cleared in `finally`. The form forgets the file reference afterwards.
- **Self-signed and local-CA leaves:** names, label, validity, key. Issuing from the local CA creates the root first when there is none. "Create the local CA" is its own action.
- **Re-issue** (self-signed and local-CA leaves, confirmed). **Delete** (confirmed; refused for the admin certificate and built-ins). **Renew now.**
- **ACME request:** names, account, HTTP-01 or DNS-01 with a provider, label, key. A wildcard needs DNS-01 and an IP HTTP-01, checked in the form and again by the service.

### Live ACME progress

`AcmeIssuanceService` takes an optional `IProgress<AcmeOrderProgress>` on request and renew, and keeps the latest step of every running issuance for `GetProgress`.
- The stages (Contracts: `AcmeProgressStage`) are Starting, OrderCreated, Authorizing, ChallengePublished, PropagationCheck, Validating, Validated, Finalizing, Issued and Failed, with the sanitised message and `RetryAt`. They are public facts only. A throwing reporter is logged and ignored.
- The dialog pushes each report to the page (no polling). It shows the steps (order, authorizations, challenge published, DNS propagation for DNS-01 only, validated, finalized, issued), the skipped steps of a resumed order, the elapsed time, and a failure with the CA's message, the retry time and "Retry now".
- The run uses the application's stopping token, like the API. Closing the dialog ("Continue in the background") does not stop it.
- The detail drawer is the one place that polls: every 2 s while open it reads `GetProgress` (in memory) for an ACME certificate and the stored order while a run is going, so a run started by the scheduler or another tab shows too.

### Live lists

`ConfigService.OperationalChanges` (`OperationalChangeFeed`, Application) announces every audited change made through `ApplyOperationalChangeAsync` or `ApplyCertificateChangeAsync` after its commit (the audit action and target only). `LiveUpdates` listens to it and, where host usage matters, to the config change feed, and refreshes the page once per burst (300 ms). No list polls.

### ACME accounts and DNS providers

`/settings` is a hub of tiles. ACME accounts and DNS providers (`/settings/acme-accounts`, `/settings/dns-providers`; `providers:read` to see, `providers:write` to change) are `pdk-dt` tables in a `pdk-table-card`, with rows that open a drawer at an address, and the same skeleton, empty state and cards-below-900-px as the certificates list.

**ACME accounts.**
- The list has an Active and a Deactivated tab (`SegmentedControl` with counts, Active first; the tab is page state, not part of the address). An empty tab is one line of text; "No ACME accounts yet" (with the add button) is for no accounts at all. Columns: the account (label, else the first contact), the CA (with the Staging and EAB pills), the key algorithm, when it was added, how many certificates are ordered with it.
- Add with presets (Let's Encrypt, its staging, ZeroSSL and Google Trust Services, which need EAB) or any https directory URL. Fields: an optional contact email, the CA's terms (required, linking the CA's page), and optionally the EAB key ID and HMAC key.
- `AcmeAccountDetailsDrawer`: status, the certificates ordered with the account (`CertificateLinks`, with `certificates:read`), directory URL, contacts, key algorithm, EAB key ID (never the key), the account URL at the CA, when added. Deactivate is final and confirmed, a footer action for `providers:write` and an active account.

**DNS providers.**
- Cloudflare or deSEC, a label and the API token. The form names the one host the token is sent to (`DnsProviderService.ApiHostOf`). The table has no filter and no status column (a provider has no states, and a test's verdict isn't kept). Columns: the name, the type, when the token was last changed, when added, how many certificates are ordered through it.
- `DnsProviderDetailsDrawer`: the name (renamed in place for `providers:write`; Rename acts at once, and an edited name makes the drawer ask before it closes), the certificates ordered through it, the type, the API host, and "stored in the vault" (never the token). Footer: Delete, Replace token (stacks the token form and tests the new token), and Test credentials, whose verdict shows in the drawer. A provider just added opens its drawer and is tested.
- **Renaming** is `DnsProviderService.RenameAsync` (same label rules as adding, `providers:write`, audited as `acme.dns_provider.rename` with the old and new name) and `PATCH /api/v1/dns-providers/{id}` with `{ label }`. It leaves `UpdatedAt` alone, which the table shows as when the token was last changed.
- **The seam** is `IDashboardProviders`, so the pages and drawers are tested with a fake and no CA.

**Secrets are write-only.** They are password fields, emptied before the call, handed over as `char[]` and turned into bytes only in `ProviderActions` (which zeroes them when the service returns). Nothing returns a secret, the submission records print `***`, and the vault keeps the value.

**Shared pieces:** `ConfirmActionDialog`, `LiveUpdates`, `QrCode` and `QrCodeImage`.

**Not built:** changing a certificate's label or notes; replacing an uploaded certificate under its ID from the page (the API has it); moving certificates between ACME accounts or providers; a CNAME-delegation hint for DNS-01.

## Settings, My account and the audit log

**Settings hub.** Tiles, each shown only with the permission its page needs: Global defaults (`/settings/defaults`, `config:read`), Profiles, ACME accounts, DNS providers, Users and roles (`/settings/users`, `users:read`), API tokens (`/settings/tokens`, `tokens:own`) and My account (`/account`, also from the user menu). Addresses are in `SettingsRoutes`.

**Server** (`/settings/server`, `config:read`; editable with `config:write` and `server:write`, the Owner and Admin). One card per group (data retention, sign-in, notifications, integrations, connections), one row per declared setting. The page builds itself from `ServerSettingCatalog`, so a new setting needs no markup.
- **One field per kind** (`ServerSettingField`): a duration is a number and a unit (`DurationField`), a size is `SizeField`, a switch is a `SegmentedControl` of Default, On and Off, and text and numbers are plain fields. Empty is the default, which the placeholder names ("Default (90 days)"). A number past what a span holds says "Too long to store." at the field and is not emitted (null would read as the default).
- **Set by the environment:** the field is read-only and shows the value in force, with one caption naming the variable (`Paduraksa__Metrics__Retention`).
- **A restart-only setting** says "Applies after a restart", with a *Restart pending* pill while the stored value isn't the one running.
- **The review drawer** (`ServerSettingCatalog.Notices`, added in `ReviewNotices`) says what a lowered retention deletes (and that it can't be recovered), that shortened sessions end at their next check, that a longer minimum password applies to new passwords only, what turning MCP on allows, and which changes wait for a restart.
- **Changed elsewhere:** a commit by someone else reloads the page, or shows the banner while there are edits, as Global defaults does.

**Global defaults.** One form over `GlobalSettings`, applied through the review drawer as `UpdateSettingsCommand` with the version it opened with. It holds:
- the bottom layer of host settings (HTTPS redirect, minimum TLS, protocols over TCP, HTTP/3, HSTS, an IP allow list; each "Built-in default (...)" or a value, `HostSettingsFields`);
- what a request for an unknown name gets (404, 421, close);
- the default certificate (usable ones, with `certificates:read`);
- the client address: direct, behind a proxy (proxies, header, `X-Forwarded-Proto`), Cloudflare proxy or Cloudflare Tunnel (`True-Client-IP`, the `X-Forwarded-For` fallback), as cards. Switching modes drops the options the validator would refuse.

Inline validation is the real validator with the settings put in (`InlineValidation.ForSettings`). A change made elsewhere reloads an untouched form and puts a banner over an edited one.

**Profiles.** What each sets and the hosts that use it. The form (name, notes, the same fields, each "Inherit (the global value)") goes through review (`UpsertProfileCommand` and `DeleteProfileCommand`, IDs typed at the edge). Deleting one in use is refused up front with the hosts named.

**Users and roles.**
- The users with their roles, enabled, locked, two-step on or off, and, with `sessions:all`, when last seen. A card says what each role may do in one line.
- With `users:write`: add (username, display name, initial password, roles; checked as the service checks), edit (sent as one `PATCH`-like update of only what changed) and delete (confirmed). The last-Owner guard's refusal is shown in the dialog that asked (`identity.lastOwner`: make someone else an Owner first).

**API tokens.** One's own, and "Everyone's" with `tokens:all`. Revoked and expired ones are shown on request.
- Create: name, expiry (7, 30, 90 days, a year, never, or a date), and scopes by area with read-only and "all you can" presets. Only what the user holds is offered ("a token never exceeds its owner"), and never `tokens:*` or `sessions:all`.
- The secret shows once in the same drawer, with Copy and a "This token won't be shown again" notice, and is dropped from the page's state when closed.
- **A row opens the token's drawer** (`TokenDetailsDrawer`, `/settings/tokens/{tok_...}`; someone else's token opens from the list of everyone's with `tokens:all`, anything else is the "not found" drawer). It shows the status, owner (a link: My account for one's own, the user's drawer when the caller may read users, else text), created, last used, expires and the token ID in a copy box; never the secret.
- Whoever may revoke it can change it (`tokens:own` or `tokens:all`): the name, and the scopes with the issuing picker (`ScopePicker`), which offers what the token's *owner* holds, not the editor. Save goes straight to the service: a token is not a config revision, so there is no review sheet or draft. Only what changed is sent.
- **Disable and Enable** act at once and keep the drawer open. **Revoke** keeps its confirmation. A disabled token gets an amber "Disabled" pill and a dimmed row and is listed by default (it can come back); revoked and expired ones need "Show revoked and expired".
- If a token is revoked or the caller may not change it, the drawer shows the name as a `ReadOnlyField` (it stands where an input would be), the scopes as chips and no actions. The details rows stay in the `pdk-facts` grid, the drawer pattern for facts.
- Scopes the token has but can't use (its owner lost them) are shown as struck-through chips with one line saying that saving changed scopes removes them. A rename alone never sends scopes.

**My account:** Profile (username read-only; display name editable with `users:write`), Password (current, new with a strength meter, again; on success "your other sessions were signed out"; fields emptied as the change is sent), Two-step verification (`TwoStepEnrollment`; new recovery codes and turning it off, each behind the password; recovery codes are shown once with Copy and "Download .txt"), Sessions (this one marked; end one, or "sign out everywhere else", confirmed).

**Audit (`/audit`, `audit:read`).**
- **Filters:** time range (1 hour to all, or from and until in the browser's time zone), who (you, users with `users:read`, tokens one may list, Paduraksa, the CLI), action (exact or `prefix*`, with suggestions), target, and outcome (`AuditFilter.Outcome`, on the API as `?outcome=`).
- **Rows:** when, the action in words with its dotted name as a chip coloured by area, who, and the outcome chip. A row opens to the time, channel, client, target (linked when it has a page), revision, event ID and the details pretty-printed (never a secret).
- It pages through the cursor ("Load older events"); new events reload the first page, or offer "Show them" once older pages are loaded.
- The hash-chain card verifies on load and on "Verify now": intact with the count and the head hash's start, or broken with the first bad event and why.
- "Export JSON Lines" writes every event the filter matches (up to 100 000), field names as the API's, made in the page and saved through the browser.

**Seams.** `IDashboardIdentity` (users, password, TOTP, sessions, tokens; `DashboardIdentity` over the Application services) and `IDashboardAudit` (`AuditQueryService`).
- Password checks take the admin app's derivation slots (`IPasswordCheckSlots`, the server hands over `AdminThrottle`'s). Busy is "try again in a moment".
- Successful identity changes are announced on `IdentityChangeFeed`, so lists of users, tokens and sessions follow other circuits without a timer. Changes through the API or CLI show on the next load.

**Secrets.** Passwords, the TOTP URI and secret, recovery codes and a new token's secret live in component state only while their step shows, and are dropped on close, on the next step and on disposal. No DTO the pages hold carries one.

**Shared pieces.** `SecretValue` (shown once, Copy), `RecoveryCodesView`, `DashboardBrowser` (clipboard and saving a file from a .NET stream, in `paduraksa.js`), `settings.css`. Toasts are at most 420 px wide with 20 px margins (full width less 8 px on a phone), and pages leave room below their last card to scroll clear of one. The review drawer's footer wraps its actions so Apply never leaves the window.

**Not built:** changing one's own display name without `users:write`; identity changes through the API or CLI reaching open pages live; a CSV export. The YAML export and import page is in [admin-surfaces.md](admin-surfaces.md) and the backup page in [vault-and-certificates.md](vault-and-certificates.md#backup-and-restore).

## Insight pages

### The logs page

**Logs** (route `/logs`, `logs:read`: Owner, Admin and Auditor by default, and an API token scope) reads `ILogFileStore` (`Paduraksa.Application.Logging`), the read-only side the dashboard and the API share:
- `List()`: the writer's names only, never a symlink or anything else in the directory. The live file first, rotated ones newest first by the timestamp in their name.
- `ReadTail(name, lines, filter)`: at most 1000 lines (200 by default). A plain file is read from its last 1 MiB; a compressed one is decompressed from its start for at most 8 Mi characters. Every line is cut at 4096 characters while it is being read, so no line, however long, corrupt or compressed, is decoded whole. It returns the last matching lines after an optional case-insensitive filter, with `Truncated` when there was more.
- `OpenRead(name)`: a read-shared stream for the download.

A name is resolved inside the logs directory, and checked to be a log name and not a link before and after opening, so `../`, an absolute path or a symlink is a 404, not a read. Reading the logs is a read like the audit log's, so it is not audited.

The page groups the files by log in a card per log (name, live, rotated or compressed, last written, size). A file opens in `LogFileDrawer`: monospace lines, a filter field, a 200/500/1000-line choice, a note when only the end was read, and the download. A fresh install says "No log files yet" with the directory's path.

The API: `GET /api/v1/logs` lists, `GET /api/v1/logs/{name}` downloads (streamed, `Content-Disposition: attachment`, `text/plain` or `application/gzip`), `GET /api/v1/logs/{name}/tail?lines=&filter=` gives the tail as JSON. All need `logs:read` and are in OpenAPI.

### The live tail

**Live tail** (route `/logs/live`, `logs:read`) is the next entry under Insight. **Decision: a page of its own, not a tab on Logs.** Logs is a browser of files; a stream has controls a file does not (follow, pause, filters that reshape what arrives). It reads `LogTailService` (Application): the hub described in [observability.md](observability.md#the-live-tail), a subscription per page.

**Table.**
- Newest first. **Decision:** the view stays put at the top while following, with no scroll handling, and pausing is a plain freeze.
- At most 500 rows: time (the browser's zone, to the millisecond); status (a badge by class: 2xx green, 3xx blue, 4xx amber, 5xx red, none grey); method and path with its redacted query (the row's link is the keyboard's way in); host as received; client; time taken; upstream; request ID. Below 900 px a row is a card with labelled cells.
- Rows are components that never re-render (`ShouldRender` false after the first), keyed by sequence, so a batch costs the new rows and not 500 diffs.

**Controls.**
- *Pause/Follow* freezes the view while the channel keeps filling to its bound. What overflows is counted ("N lines skipped"); "N waiting" says what a Follow will show. *Clear* empties the table.
- *Filters:* host (a picker of the configured hosts' first names, "Any host" and "No host matched"), status (one box taking `404` or a class `5xx`), client IP (an address or its leading part), method, and path contains. A text filter applies after 400 ms. Any change opens a new subscription (the backlog of 200 newest matches, then the stream), so the filter runs in the hub, not in the page. "Clear filters" shows only while one is set.
- **One filter contract:** `LogTailFilter` (Contracts) holds the status grammar (`TryParseStatus`, shared by the API's two parameters and the page's box) and the 256-character cap on every text. `LogTailService.Open` and `Recent` refuse a filter that fails its `Problem()`, so the circuit is held to the API's rules.
- `?path=` and `?host=` pre-set the filters (a host the picker does not offer is ignored; a path is cut at the cap). Analytics links its paths here.

**Pushing.**
- The page reads its channel every 300 ms (`PeriodicTimer` on `TimeProvider`; a `BatchInterval` parameter for tests) and renders once if anything changed. A batch larger than the table keeps its newest 500 and counts the rest as skipped.
- The tab's visibility is the `paduraksa.pageVisibility` helper. Hidden, the page stops reading, so nothing is rendered or sent to a tab nobody sees; a show reads at once. The helper keeps one listener per owner (`watch(owner, ref)`, `detach(owner)`; the owners are `overview-strip`, `live-tail`, `analytics`, `security`, `security-strip`), so a page that is leaving takes down only its own.
- **Load**, proven in `ThousandsOfRequestsASecondStayBoundedAndRenderInBatches`: 20 000 entries a second through the hub against a page at a 40 ms batch keep renders within one per batch and the table at 500 rows, newest on top.

**A row** opens `LogTailEntryDrawer`: status and matched host as chips; the request ID in the copy control `SecretValue` gives; the request line, host, client, time taken, upstream with its time, response size, `CF-Ray`, referer and user agent; which request of its connection it was ("Request 1, TLS handshake 3.4 ms", "Request 14, TLS reused", "Request 2" on plain HTTP); and a **waterfall** of the stages laid out like Chrome DevTools' Timing tab (`RequestWaterfallView`, laid out by `RequestWaterfall`; the stages are defined in [observability.md](observability.md#request-timing)):
- one axis from 0 to the request's total (or the furthest a bar reaches), with a thin scale under the rows;
- each bar at its measured start with its measured length (the API's `*StartMs`; the gate starts at 0), so overlapping stages overlap and time no stage accounts for stays a gap;
- the rows in the order the stages started, the pipeline's order breaking a tie: Gate, Upstream connect (only when the forward opened a connection), Request body (only with a body), Waiting for upstream (TTFB), Response send;
- **derived rows** fill the time between the measured stages, in a neutral colour, from the same `timing` figures (nothing on the request path): *Forwarding the request* (after the gate and before the wait starts, where no connect or body covers it; a request that opened a connection can have two), *Preparing the response* (the upstream's answer to the response's start), and *Unaccounted* (hatched: any other stretch, including a local answer's time between the gate and the send). A gap under about 1 us is rounding and gets no row;
- **rounding:** each row's time goes through `DisplayFormat` at its own precision, and rows that run in sequence are nudged by single steps (largest remainder, never hiding a row that has time) until they match the exact total. Overlapping rows can't add up and are shown as they are;
- **one tooltip per row** (the charts' tooltip style, a CSS-only box under the row): the stage's start and duration, then a sentence on what happens in it. The whole row is one tab stop, `:hover` or `:focus` opens it (not `:focus-visible`, which a tap never matches), and the text is the row's `aria-describedby`. It sits below its row so it never covers the bar it describes;
- the tracks share one grid (a subgrid per row), so every bar sits on one axis. On a phone (600 px and under) the label goes above its track. The geometry (`left` and `width` in percent) is the one other file the formatting guard lets spell a number;
- the TLS handshake happened before the request started, so it is not on the axis: the connection line above it says it.

A **Simulate this request** button (for a role that may simulate) links to `/simulator?url=https://<host><path><query>&clientIp=<client>&method=<method>`. The scheme is always `https` (the entry does not say), and the query is the redacted one, so a simulation of a redacted token runs with the literal `REDACTED`.

**Copy.** Two lines say something the data does not: the empty state ("Requests show up as they end. A host with its access log off never does.") and the skipped badge's tooltip.

**The stream** (`GET /api/v1/logs/tail`, `logs:read`): `text/event-stream`, `Cache-Control: no-store`, `X-Accel-Buffering: no`.
- The first line is the comment `: open`, so a client knows the stream is live before the first request ends.
- Each entry is a default (`message`) event: `id:` its sequence and `data:` one `LogTailEntry` JSON on one line. The fields are `sequence`, `time`, `clientAddress`, `host`, `matchedHost`, `method`, `path`, `query`, `protocol`, `status`, `responseBytes`, `requestBytes`, `totalMs`, `upstreamMs`, `upstream`, `requestIdentifier`, `cloudflareRay`, `referer`, `userAgent`, and `timing` (`connectionRequest`, `tlsHandshakeMs`, `tlsReused`, `requestBodyMs`, `gateMs`, `upstreamConnectMs`, `upstreamWaitMs`, `responseSendMs`, and where the stages start: `requestBodyStartMs`, `upstreamConnectStartMs`, `upstreamWaitStartMs`, `responseSendStartMs`). Nulls are omitted. The request ID is `requestIdentifier` because the architecture rule keeps any `*Id` property of a contract a typed ID.
- A reader that falls behind gets `event: skipped` with `{"count":N}`. A comment `: heartbeat` goes out every 15 seconds of silence (`LogTailService.Heartbeat`), so an idle proxy or client does not time the stream out and a vanished client fails the write.
- Query parameters: `host` (a configured host's name), `unmatched=true`, `statusClass` (`5` or `5xx`), `status`, `ip` (prefix), `method`, `path`, and `backlog` (0 to 1000, default 0, the newest matching entries first).
- Every bad parameter, an over-long text and `host` with `unmatched` are 400 problems. The 33rd stream is a 503 with `Retry-After: 5`. There is no maximum duration: the stream ends when the client disconnects (`RequestAborted`), which disposes the subscription.

### The security overview

The first entry of the Security group (`/security`, `config:read`) answers "what is protecting my hosts, and is it working".

**`SecurityOverviewService`** (`Paduraksa.Application.Security`) is a shared singleton that reads a range (`ReadAsync(MetricsRange)`) from the metric sampler and the rollups, so a restart doesn't reset it (see "Security counters" in [observability.md](observability.md#security-counters)).
- Per enabled host it reads the *live compiled snapshot*, not the stored document, so it is what the gate is doing and a revision the compiler rejected never shows. That needed `CompiledHost.Names`, `CompiledHost.RateLimit` (the host's own policy object, which an inheriting route's chain shares, so a route whose `RateLimitPolicy` has another ID, or none, is an override) and `RateLimitPolicy.PolicyId` and `PolicyName`. Nothing on the request path changed.

**What a host reports:**
- **Proof of work:** its switch and difficulty, plus how many routes differ, either way. Redirect, Respond and Admin hosts report it off, since the gate only runs on proxy routes.
- **Rate limit:** the policy, with the routes that apply another policy or none, grouped.
- **Robots.txt:** the mode.
- **Access list:** read from `CompiledHost.AccessList` and `CompiledRoute.AccessList`, not from the policies in the chain (a host with routes on different lists holds only the address-only union). The column names the list, linked to its page, with what it asks of a visitor ("2 users" or "addresses only"). Routes on another list, or none, are grouped like the rate-limit column's. `AccessListRef.AddressRanges` (merged ranges) is in the DTO for the API; the page does not print it.

The overview is one immutable DTO (`Paduraksa.Contracts.Security.SecurityOverview`): the range, the counters over it, the refusals by reason (`RefusalReasons`, with "unknown" for minutes stored before the reasons were kept) and over time (`RefusalStep`s), and the hosts with what each refused. `GET /api/v1/security/overview?range=` returns it (`config:read`).

**Decision (owner): the page shows the protections; the counters over time live on Analytics.** The counters are a self-contained component (below) that takes the range.

**The page:**
- **The security strip** (`Components/SecurityStrip.razor`) opens the page: four `SecurityTile`s in the Overview strip's grid, for what the gate refused in the **last hour**: Refused (every reason), Proof of work (challenged or refused), Rate limited, and Denied (access lists and rule sets together). Each links to `/analytics?tab=security&range=1h`. Each figure is the count over the hour, with a sparkline of that family's refusals a second per step in the colour the Security tab draws it in. A step nobody observed breaks the line.
- **Why the hour (decision):** the hour is the sampler's memory, so it costs no store read, polls like the Overview's strip, and has a sparkline from the same ring; the counters still don't reset on a restart. The 24-hour figures stay in the hosts table's **Refused, 24 h** column.
- It reads the hour every 5 s while the browser tab is visible (owner `security-strip`), with no error state. It is drawn pending (the same tiles with a bar for the figure) until the permission check is done (`Active`), so the skeleton and the loaded strip are one frame, which `SkeletonShiftEndToEndTests` holds to 1 px.
- The hosts table has no card heading: the page's content is one section, so the title is its heading. Protected hosts by name, then those with nothing on, each cell a muted "None" when empty; a stacked card per host on a phone. Its **Refused, 24 h** column is the host's refusals from the host series. The Rules and Sign-in cells count over the same 24 hours.
- It reads `ReadAsync(Day)` every 2 s (`PollInterval`): the protections are the live snapshot's on each read, the 24 hours come from the service's 30 s cache. Polling pauses while the tab is hidden. While the day fails (the rollups can't be read) the poll reads only the hour, so the protections stay live; "Try again" reads the day once more.
- **Rollups that can't be read don't hide the protections.** The page falls back to the hour for the table, drops the Refused column and shows an alert with "Retry"; the strip is the hour and stays.

**Gaps:** the permission is checked when the page opens, not on each tick (the session re-check still ends a revoked session's circuit). The rate-limit share counts only requests on routes with a policy. The process counters are not kept per host; only the refusals are.

### The security statistics (`SecurityStatistics`)

A component (`Components/SecurityStatistics.razor`) with no page of its own: it takes `Range` and `Visible` and reads, polls and draws by itself. It is the Security tab of Analytics.
- **Refused per second**, stacked by reason: a `TimeSeriesChart` with `Stacked` (each line a filled band on the lines before it; the tooltip gives each reason's own rate). Only the reasons that refused something in the range are drawn. Unknown reason is a neutral grey; the reasons have `pdk-chart-r1` to `r9`. Gaps are shaded, and the total refused is beside the title.
- Cards: proof of work (served, solved with its share, bad solution, not a browser, the four cookie reasons), rate limits (checked, limited with its share), access lists, connections (the live limit and deny list, what was refused, counted now, and table full once it happened), sign-in, and "Not served" (unknown host, no route, unavailable, dropped) once there are some. Short cards align to the top rather than stretching.
- Numbers and times go through `DisplayFormat`. Polling as the overview's: the hour every 2 s, the longer ranges a tenth as often, paused while hidden and read at once when shown. A range that can't be read is an alert with "Retry" (`data-part=security-failed`). A role without `config:read` sees a lock.
- It has no section heading (the tab is the heading) and no host scope.

### The Analytics page

**Analytics** (route `/analytics`, `metrics:read`, which every role holds) is the one place for statistics over a range, in three tabs under one range picker. The overview keeps only the live strip.

**What lives where.**
- **Overview** (`/`): the live hour at a glance, linking here. No range.
- **Analytics, Traffic:** the proxy's own counters (`MetricsService`): requests and latency charts, the busiest hosts, the upstream errors.
- **Analytics, Visitors:** the access-log drain's analytics, for every host or one (`AnalyticsService`).
- **Analytics, Security:** the gate's counters over time (`SecurityOverviewService`).
- **Security overview** (`/security`): live tiles for the last hour of refusals and the protections per host; its tiles link to the Security tab.

What a person asks ("how is it doing now", "how did it do over a week") has one answer in one place.

**Address.** `?tab=traffic|visitors|security&range=1h|24h|7d|30d`, declared as `SupplyParameterFromQuery` parameters (`Pages/AnalyticsView.cs` writes and reads them). With nothing, or an unreadable value, it is Traffic over 24 h. Choosing a tab or a range rewrites the address (replacing the history entry, so Back leaves the page), and a switch of tab keeps the range. The tab is a `SegmentedControl` under the header (there is no other in-page tab control, and a one-off would break the shared-controls rule), the range picker (the same control) is in the header, and the host picker is there too, only on Visitors, and not in the address.

**Only the open tab is mounted,** so only it reads and polls. The page owns the browser-tab visibility watcher and passes `Visible` down. Each tab takes `Range` and `Visible`, starts over on a new range, and reads at once when shown again. The three tabs derive from `StatisticsTab<TKey, TOverview>`, which owns the hold, visibility, read and poll frame; a tab supplies its key, read, `Shape` and markup, and `BeginAsync` for what must precede the first read.
- **Polling:** the hour reads fast (Traffic and Security every 2 s, Visitors every 10 s) and the longer ranges a tenth (Traffic, Security) or a sixth (Visitors) as often. Hidden, nothing reads. A failed read stops the polling until "Try again".
- **The tabs are held from the prerender:** `Analytics` renders the tab row and the open tab's frame at once, with `Hold` set until the circuit has checked who is asking, so nothing is read for a person who may not see it.
- **Is 1 h live on Visitors?** Yes, to within a few seconds, so there is no caveat line (`AnalyticsService` merges the stored buckets, the closed buckets waiting to be written and the open bucket). What is not in it is a host with its access log off.

**Traffic tab** (`Components/TrafficStatistics.razor`): two charts (requests with a 5xx line; latency p50, p95, p99 and upstream p95) and two tables (busiest hosts, linked, the requests no host owns unlinked; upstream errors, named from the config). No live tiles: a figure for the last 30 s under a range picker would contradict the range. A longer range's tables splice the last hour from memory. A range that can't be read is an alert with "Try again".

**Visitors tab** (`Components/VisitorsStatistics.razor`) takes `Range`, `Host`, `Unmatched`, `HostName` and `Visible`. There is no page-wide accuracy note. The visitors tile carries its error as its subtitle ("+/-1.6%", the sketch's standard error), and a table count a sketch may have overcounted is marked with the approximation sign and a tooltip "Between X and Y".
- **Top.** Four tiles: requests (with the share from bots), bandwidth (what was sent and, when anything was uploaded, how much was received), unique visitors, and the error rate (5xx). A range with no requests draws one empty state saying that a host with its access log off is never counted.
- **Visitors and content.** Three `TimeSeriesChart`s (requests with a 5xx line, bytes sent, unique visitors). **A step nobody observed is a shaded gap and its line breaks. An observed quiet step is a real zero** (from the service's `coveredSeconds`). `ShareList` bar lists with shares: status classes (green, blue, amber, red) and the top exact codes, methods, HTTP versions. Tables: top paths (requests, bandwidth, p95, errors), top referers, slowest paths, largest responses.
- A path is a link to the **Live tail filtered to it** (and to the host when the page is for one). If the analytics cut a path at their cap, it is plain text. **A user agent is not a link:** the tail has no user-agent filter, and adding one would reach into the tail's contract for little.
- **Clients and bots.** Browsers, operating systems and devices as bar lists; bots by kind; the top bots; the bots ignoring robots.txt (its empty state says only hosts that serve a robots.txt are checked); the raw user agents.

**Security tab.** The security statistics over the page's range.

## Settings fields

`HostSettingsFields` works on the contract layer itself: a `HostSettings` where null means inherit, plus what that layer would inherit. It hands back a changed record with `SettingsChanged`. The form page owns it (global defaults, a profile, a host), with no flat form model in between.
- The inherited values are `HostSettingsLayers.MergeAll` of the layers below over `BuiltIn`, so the form cannot promise a default or a merge the proxy would not apply.
- Each field changes its own record with `with`. `HostSettingsEdit.WithTls`, `WithHsts` and `WithServerHeader` collapse a nested record that sets nothing back to null by `ISettingsLayer.SetsNothing`, so clearing a field leaves no key behind.
- `SettingRow` is the row's title, help and control. `InheritSelect<T>` is a nullable value as a select whose first option says "Inherit (...)" (or "Built-in default (...)" on the global defaults) in the words of the value the layer below has. It carries the label, `data-field`, error and read-only state every field shares.
- `SettingsSummary` holds one phrase per setting in the contract's order, so the cards and profile summaries need no per-field code elsewhere.
- `HostSettingsFieldsTests` is one table over every field (setting it through the control, the record that leaves, the inherited words, clearing it, an error at its path), with a reflection completeness check over the contract, the counterpart to `SettingsInheritanceTests`.
- The "Adding a field to the settings UI" checklist in [config.md](config.md) is the whole cost of a new setting here.

**HSTS group**, shared by the global defaults, the profiles and the host form:
- A switch ("Inherit (off)", On, Off) brings the rest with it.
- **Remembered for:** a select of 5 minutes (for testing), a day, six months, a year and two years, plus custom seconds (`HstsMaxAgePresets`, kept as the number of seconds the config holds). Its preset-or-custom state is `HstsMaxAgeField`.
- **Every subdomain** and **Preload list**, each "Inherit (...)" with the inherited value in words.
- The copy says what a browser remembers and for how long, that a subdomain without a certificate stops working over plain HTTP, and that preload is hard to undo.
- The validator's errors show at the field they are about. The group shows the rows that apply: inheriting "on" shows them, an explicit Off hides the preload pair, and **Remembered for** also shows when the layer sets an age with the switch off, so a number the validator refuses has a field to appear at.
- Cards and profile summaries say "HSTS: 1 year, subdomains, preload", "HSTS when on: ..." when the switch is left to the layer below, or "HSTS off".

**Server header group:** a select with "Inherit (...)" naming the inherited mode in words, then `Paduraksa (no version)`, `Custom value`, `The upstream's header` and `Remove`. A **Custom value** field appears for the custom mode, and also when the layer already sets a value or the validator refuses one. The one helper line says that hiding the upstream's header stops it advertising its software and version. Summaries say "Server header: Paduraksa", "Server header: edge/1.0", "Server header: the upstream's header" or "Server header: none"; a value without a mode says "Server header value: ...".

## Drafts

An edit used to be its own review and its own revision. A **draft** collects edits made anywhere (hosts, upstreams, profiles, rate limits, access lists, rule sets, sign-in providers, the global defaults) and applies them as **one revision** when the admin is ready. A change that spans several pages therefore goes live at once or not at all.

Code: `Paduraksa.Application/Drafts` (`DraftService`, `DraftReplay`), `Contracts/Drafts`, the `ConfigDrafts` tables, `/api/v1/drafts`, and the dashboard's `DraftState`, bar and list.

### What a draft is

A per-user list of **entries**, in the order they were added. Each holds an object command (`UpsertHostCommand`, `DeleteUpstreamCommand`, ...), the version of the object it was made against, the object it is about (`DraftEntry.Target`: `hosts/host_...`, `settings`) and what it does (create, update, delete), plus the revision the draft was started on. It is not a frozen document.

**One entry per object.**
- A later edit of the same object replaces the earlier one and keeps its place and number (numbers are never reused).
- An edit that leaves the object as the head has it takes the entry out. Deleting an object the draft itself creates removes its entry. An entry can be removed on its own.
- A whole-document command (replace, rollback) is not an entry.
- A host form's staged objects (an upstream made for it) go in as entries of their own, ahead of the host.

**Stored server-side** in the config database (migration `ConfigDrafts`: one row per user with a unique owner and a `Version` that moves on every change; and `ConfigDraftNotices`). It survives a reload, a second tab and a restart. Nothing in it is live, committed or audited until it is applied. A token acts on its owner's draft (an agent proposes, the person applies).

**Not drafted: what is not config.** Certificates, ACME accounts, DNS providers, users, tokens, notification channels and backup act at once (a draft can reference a certificate created meanwhile). So does a YAML import, which replaces the whole document.

### Lifetime

Untouched for 30 days (`DraftService.Lifetime`; every change restarts it), a draft is dropped, with the vault values bound to it, by `DraftExpiryJob` (once at start, then hourly). Its owner gets a `DraftNotice` at the next dashboard load: a toast that stays until closed ("Your draft with 3 changes was dropped after 30 days untouched."), shown once.
- **Until the job runs, an expired draft is `DraftStatus.Expired` and nothing writes it.** Its reads, review, simulation, add, remove and apply answer 410 `draft.expired` (the list leaves it out). Dropping it on the way would break "a read changes nothing": MCP dispatches the reads, the review and a simulation as reads with no `confirm`.
- **Only the two writes that end a draft remove an expired one early:** a discard, and opening a new draft over it (the API's `POST`, or the dashboard's first "Add to draft"), which drops it as the job would, notice included.
- The dashboard never shows an expired draft: the bar is hidden and the pages read the live config. The notice toast appears once the job (or the next "Add to draft") has dropped it.

### The head with the draft applied

One read model, `IDashboardConfig.GetWorkingAsync` over `DraftService.GetConfigAsync`: the latest revision with each entry replayed in order (`DraftReplay`). Versions are left as the head's, so a form that reads an object of the draft sends back the version the entry will be checked against.
- Every config page and form reads it, with all their pickers. The overview, history, simulator, analytics and live tail read the live config.
- A picker marks an object the draft creates or changes "Draft" (`DraftChip`, beside "New" for what a host form staged). The host list's live warnings index the live document, so they are left out while a draft is open.
- `Watch` also fires, with the latest revision and no changes, when the caller's draft changes in any tab (`DraftService.Changed`).

### Adding an entry checks it

The command is dry-run through the real pipeline (`ConfigPipeline.AttemptAsync` with the draft's base document, a simulation: the same validator, compiler and certificate guard) on the head with the draft's *other* entries applied. The errors are the ones a live command gives, relative to the object, and a form shows them at its fields. A refused command adds nothing (`FailedCommand` says which of several). A version that does not match the object in that view is the usual conflict.

### Secrets

A password typed into a drafted access list never reaches the row.
- The entry's user carries a **new** `credentialId` (never the one the list has now: the live password stays until the draft is applied). The password goes to the vault as a credential no config names yet.
- The draft row keeps the list of those IDs (`PendingCredentials`) and nothing else: no secret in the row, the API output, the audit log or any log.
- They are deleted with their entry (removed, replaced by a newer password, taken out because the object went back to the head) and with the draft (discarded, expired). Applied, they are the credentials the revision names, and the credentials it stopped naming go as after any commit. The startup sweep of the vault's orphans leaves what an open draft holds.
- A DNS token is not draftable, so access-list passwords are the only draftable secret.

### Review and apply: what the person confirmed is exactly what is applied, or nothing

**Review** (`DraftService.ReviewAsync`) replays the entries on the **current** head under the writer lock and dry-runs the set as one whole-document command. It gives the review panel the whole set (the diff grouped by object, the compiler's warnings, the lock-out guard (`?force=true`), Simulate against the set) and a **review token**.

**Apply** (`ApplyAsync`) must send that token (`If-Match` on the API). **Under the writer lock it replays on the head as it is now, plans again, and commits only if the token of that plan is the one it was given.**
- Otherwise nothing is committed, and the **fresh review** (the plan as it is now, with its token) comes back: `DraftStatus.ChangedSinceReview`, the API's 412 `draft.changedSinceReview` with the fresh review as the problem's `review`. The person confirms again on purpose.
- The commit is **one revision**. The note defaults to "Draft (3 changes): add upstream app, change host x, ..." (editable, 1000 characters at most). The audit event is one `config.draft.apply` with every entry listed in its details. **Undo on that revision undoes the whole draft.**
- The draft's row leaves in the commit's own transaction, so a crash cannot leave an applied draft to be applied again.
- A set that does not validate as a whole is refused as any command is (422, errors by place), with no token to confirm, and the draft stays as it was.

### The review token (`DraftReviewToken`)

`{draft version}.{22 characters of base64url}`. The hash is SHA-256 over:
1. **The draft's ID and version.** Any change to the draft makes an older review stale.
2. **For every entry in order:** its number, target and action, and **the object it leaves** (its canonical JSON in the candidate document, or "gone"). That covers the diff field by field without the document positions an unrelated commit would shift.
3. **Every object those entries refer to and the draft does not write** (the upstream of a drafted host, its profile, the lists it names), as it is now. The diff does not show it, and the person still confirmed it.
4. **The compiler's warnings the draft changes:** the candidate's against the live config's, by code and message, counted (their paths are positions).

**Choice:** the hash is over what the draft does, not over the head. A commit that touches none of that (another host edited or added, a revision number moved, positions shifted) leaves the token as it was, so a busy instance does not make applying impossible. A change to an object the draft writes blocks its entry (below). A change to one it refers to, to the draft, or to anything that makes the draft warn differently, does not match.

### Someone changed the config meanwhile: blocked entries

**A draft applies whole or not at all.** Replaying on the new head is the merge, with no three-way merge.
- An entry whose object changed since the entry was added (the version check an object command makes), or went away, is **Changed elsewhere**.
- So is every entry that **refers to what a blocked entry would have written**, transitively and whatever the order. Example: the draft creates upstream U under an ID it chose and host H using U. Someone else creates an object with that ID. Both are blocked, H's reason names "add upstream U", and H is not applied on top of their upstream.
- A blocked *delete* blocks nothing that refers to it. What still points at an object that stays is the validator's to refuse (a review of the rest is a 422).
- **If any entry is blocked, apply refuses the whole draft** (`DraftStatus.Blocked`, 409 `draft.blocked` with one error per blocked entry) and commits nothing. A review still shows the rest, without a token.
- **Reload this entry** takes it out and opens the object's form on the config as it is now; an edit of the new version is a new entry. Then the person reviews again and applies. The draft never silently overwrites another admin's change, and never applies around one.

### API (`/api/v1/drafts`, every operation behind `config:write`)

| Operation | Notes |
|---|---|
| `GET` | Lists the caller's (at most one) open draft; an expired one is not listed. |
| `POST` | Opens one (409 `draft.exists`). |
| `GET {id}` | The draft's version as `ETag`. |
| `GET {id}/config` | The head with the draft applied (one replay). Another user's ID is a 404. |
| `POST {id}/entries` | Adds object commands (a list). Each is checked as above, with 422, 412 and 404 like the live command. An access-list password goes to the vault. |
| `DELETE {id}/entries/{number}` | Removes an entry: 204, with the draft's new version as `ETag`. |
| `POST {id}/review` | A dry run. The answer's `reviewToken`, also its `ETag`, is what apply must send. None when an entry is blocked, and 409 `draft.blocked` when all are. |
| `POST {id}/apply` | `If-Match` is **the review token**. 428 without it, with the current review as the problem's `review`. 412 `draft.changedSinceReview` with the fresh review and its token, also as the `ETag`. 409 `draft.blocked`. 409 `draft.nothingToApply` for an empty draft. `?message=`, `?force=`. `?dryRun=true` is the review. The body is a `DraftOutcome`. |
| `DELETE {id}` | Discards, an expired draft too. |

Someone else's draft is a 404. An expired one is a 410 `draft.expired` on every operation but the list and the discard, and nothing is written.

**MCP** classifies by the existing rules (`EndpointRules`, checked against the generated description by `McpRequestPolicyApiTests`): the reads and the review (a read-only POST) are allowed; open, add, remove, apply and discard are writes that need `confirm`. **An apply with `confirm` must pass the review token it confirmed (`ifMatch`).** Without it, or with another, it does nothing and answers with the fresh review.

### Dashboard

`DraftState` (per circuit) follows the draft.

**The bar.** A slim bar under the app bar on every page (`DraftBar`) while a draft is open, for a user who may change config. It shows "Draft", the change count and, when there are, how many are changed elsewhere, with **Review** and **Discard** (which asks). Lists mark rows the draft creates or changes with "Draft".

**Making a change.** Every draftable change takes one path (see "Making a change" under [Dashboard conventions](#dashboard-conventions)):
- A form has one primary button, **Review changes**, and no draft button of its own, whether or not a draft is open.
- The **review sheet** (`ReviewPanel`) is where the person chooses **Apply now** or **Add to draft**. The buttons keep one order and one look (Cancel, Add to draft outlined, Apply now filled), draft open or not. Decided by the owner: a button that changed style with the draft's state read as inconsistent.
- Add to draft opens a draft when there is none. The toast says which ("Added to the draft: ..." or "Started a draft: ...").
- A delete, a host's switch or wrench in the list, a row's delete: the same sheet. Maintenance from the list asks first for the mode's options (`MaintenanceDrawer`: status, Retry-After, who is let through, the page, prefilled from what the host inherits), then opens the sheet. A toggle's host comes from the head with the draft applied.
- Applying now a change the draft also holds drops the draft's stale entry; the sheet says so in one line.
- If the live config refuses a change while a draft is open (a host naming an upstream only the draft adds), the sheet shows the refusal as a warning, keeps Add to draft and turns Apply now off. Add to draft checks it on the draft's view and shows a refusal at the form's fields.
- A whole-document change (rollback, Undo, YAML import) has no draft choice: it is not draftable (an entry is one object). While a draft is open its sheet carries one notice (`DraftOpenNotice`): the draft was made on the config as it is, applying this moves it, and entries for the objects it changes become changed elsewhere. **Review the draft first** closes the sheet and opens the draft. The Undo toast asks the same in a confirm.

**The draft drawer is the review.** The bar's Review opens it (`DraftReviewDrawer`).
- **Entries:** in the order they were added, each a section with its summary line, a toggle, a remove button, and its diff rendered by the same `ChangeList` the review sheet uses. Each entry is applied on the document the entries before it left, so its "before" is the object as the head has it and its names resolve (`DraftEntryDiffs`).
- **Collapse:** a draft of up to 3 entries opens them all (`DraftReviewDrawer.OpenByDefaultUpTo`); a longer one starts collapsed to its summary lines.
- **The whole set:** below the entries, the review's warnings, the lock-out guard's errors and Simulate for the set.
- **Footer:** **Apply** (primary), **Discard** (danger, asks) and Close, with the note for the history. Apply sends the review token of exactly what is on screen.
- **Cost:** the review runs once when the drawer opens, and again only when the draft's version or the head moved. It reads the draft service's cached replay.
- **When Apply is refused** because the config changed since the review, the drawer stays open and shows the new review **in place**, with one line: "The config changed since this review. This is the review as it is now: check it, then apply." Apply needs a new click on that review, never an automatic one. A change that lands while the drawer is open shows the same way before any click.
- **When entries are blocked**, the drawer says how many ("N changes are changed elsewhere, so nothing can be applied"). Each blocked entry shows its reason and its own **Reload**, the diff of the rest is still shown, and Apply is disabled until they are resolved.
- Other open tabs follow a change through the draft's `Changed` event, not a poll.

### Decisions

- One draft per user (a token's is its owner's). An entry replaced in place keeps its number and position.
- **A draft is applied whole or not at all.** The first version applied the unblocked entries and kept the blocked ones; the owner's rule that what was confirmed is what is applied replaced it.
- The review of a set is a whole-document dry run, so its errors are by place in the document and the lock-out guard applies to the set. The token covers what the draft does, not the head. A delete of an object something still uses is the validator's to refuse.
- **The choice is in the review sheet, not in the form** (owner's rule). A form, a toggle and a delete are all "make this change"; Apply now and Add to draft are what the person then decides, so every control offers the same two, in the same order. Quick toggles never apply or draft silently.

### Cost

- A read replays the draft once per (latest revision, draft, draft version). `DraftService` keeps one replayed view per user (at most 256, then the table starts over), so every render of every page of a circuit costs a head read and a key comparison while nothing changed.
- A row stores each entry's number, action, target, command and time and **not its state or reason**, which are worked out on every read. Older rows that carry a `state` still read.
- The hourly expiry reads each draft's ID and time first and loads only the expired rows, under the writer's lock, which is where "untouched" is judged.

### Tests

- **Unit/data:** `DraftReplayTests`, `DraftReviewTokenTests`, `DraftServiceTests`, `DraftSecretsTests` (replay and ordering, the version check per object kind, changed-elsewhere and blocking that follows references, what the token covers, a review that no longer matches, one revision with one audit event, secrets never in the row or audit, expiry leaving an expired draft's row, version and vault values as they were).
- **Integration:** `DraftApiTests` (the whole flow, `If-Match` with 428 and 412, blocked entries, two users, permissions, OpenAPI conventions), `DraftLifetimeEndToEndTests`, `McpDraftTests`, `McpRequestPolicyApiTests`.
- **bUnit/Playwright:** `DraftTests`, `ChangeActionsTests`, `DraftsEndToEndTests` (the bar across pages and tabs, the draft drawer's diffs and one Apply, an upstream changed while the draft is open, 390 px).
- **Architecture:** `DashboardChangePathTests` reads the IL of the dashboard assembly and fails on any call to the config's execute, add-to-draft or apply-draft methods outside the review sheet, the undo toast, the draft state and the draft drawer.

**Not built:** merging an entry's fields with someone else's change (an entry is reloaded, not rebased), and a YAML import through a draft.

## Notification history

**Settings, Notification history** (`/settings/notifications/history`, `providers:read`) and the delivery figures on the channel list and in the channel drawer are described with the outbox in [observability.md](observability.md#durable-delivery-and-history). It sits under Settings, not Insight, because it is read and retried with the channels' permissions and linked from the channel list.

## Dashboard conventions

Shared pieces first. If a page needs its own look for one of these, the shared piece is missing something.

**`SegmentedControl<T>`** is the one single-choice-among-a-few control: time ranges, Mine and Everyone's, a host's kind, a token's expiry, the certificate filter.
- Options are `SegmentOption<T>` (value, label, optional icon, tooltip, `data-value` key, count, tone dot).
- It is a `radiogroup` of `radio` buttons. The chosen segment is the one tab stop. The arrow keys, Home and End move the choice (it follows focus, as native radios do, and wraps), and `paduraksa.js` keeps the page from scrolling on those keys.
- It is always one 40 px row beside a dense outlined field, with the chosen segment raised. When the segments do not fit (a phone) it fills the width and wraps onto further rows.
- Tests find it by `[data-field=...] [data-value=...]` and read `aria-checked`. A segment with a zero count is muted, or left out when it is a filter.
- A choice whose options need a sentence each is `ChoiceCards` (below).

**Stylesheets** are sliced by area, and `App.razor` loads the slices in cascade order, then the older area files (`certificates.css`, `setup.css`, `settings.css`).
- The order is the cascade: a rule moves only together with everything between it and its new place.
- `app.css` holds the palette, shared pieces, the shell, the open draft and the user menu. Then `pages.css`, `lists.css`, `forms.css`, `drawers.css`, `history.css` (diff, History, simulator), `feedback.css` (toasts, errors, narrow screens), `auth.css`, `reconnect.css`, `rule-sets.css`, `controls.css` (traffic statistics, segmented control) and `analytics.css`.
- A new rule goes in the file of the area it styles, and a rule that several areas share in `app.css`.

**Page header.** The title (h1) and the actions share one centre line in a row at least 40 px tall, so a title sits at the same height on every page. A lead, only where it says something the page does not, sits under the row.

**Headings.**
- A card's title is h6 (`Typo.h6`). A card under a section heading, on a page that has several (Analytics), is `subtitle1`. The section heading itself is h6 with `.pdk-section-heading`. A small label inside a form is `subtitle2`.
- A page whose content is one section has no section heading: the title is the heading, and the controls that sat beside it go in a row at the top of the content (the security overview's hosts card, the Security tab of Analytics).

**Filter cards** (Audit, Live tail) are `.pdk-filters`: an optional bar (a segmented control, "Clear filters" at the end), then the fields in one wrapping grid. Every field is dense outlined and floats its label (give it a placeholder).

**A drawer's subtitle** says something the title and the body don't, or it is left out (the header keeps its 80 px and centres the title).
- Kept: what a staged change waits for ("Not applied yet: it goes in with the host, in one revision."), what a certificate form does with the key, which names a certificate covers, and what uses the object ("Used by 2 hosts, 1 route", only while something does).
- Left out: how every change works ("reviewed as a diff"), a line the body or the alert already says, and a placeholder for a drawer that has nothing to add. `DrawerSubtitleTests` holds the host drawer to none and the used-by line to its count.

**Heights and sizes.** Dense outlined fields and the segmented control are 40 px. Icon buttons are 36 px (`Size.Small`, never `Medium`) and carry a tooltip or `aria-label`. A drawer's header is at least 80 px.

**Buttons.** One filled primary action per view. Outlined for a secondary action that stands alone, text for the rest, and destructive actions in the error colour.

**A list page shows its primary action once.** While the list is empty, the empty state carries it (`Size.Small` only inside a compact card) and the page header hides its own (`PageHeader.ListEmpty`). Once the list has items the header has it. Both placements keep the one `data-action` hook, so a test clicks the same selector either way. An empty state that offers a different, secondary action is outlined.

**Labels.** Every outlined text, numeric, select and autocomplete field floats its label on the border, empty or not, so forms never mix floated and resting labels. MudBlazor floats it only for a field with a value or a placeholder and has no global default for `ShrinkLabel`, so `app.css` applies MudBlazor's own shrink rules to every outlined field, with no call site setting anything. A field with a placeholder shows it. `FormLabelsEndToEndTests` holds an empty and a filled field's label to one position.

**Choices with explanations** (`ChoiceCards<T>`, `ChoiceOption<T>`: title, one line of help, optional icon) are used for Import's merge or replace, Global defaults' unknown-host answer and client addresses, the ACME challenge, the DNS provider and the notification service.
- One look: cards in a wrapping grid (at least 190 px each), the chosen card outlined and tinted in the primary colour. A radiogroup with the segmented control's keyboard behaviour; there is no MudRadio list in the dashboard.
- The help sits on the option, so the setting's own help line does not say it again.

**A form section's head** (`FormSection`) wraps: the title and text take at least 14 rem, and a wide aside drops under them on a phone.

**Text a person copies** is `CopyableCode`: the text in one box with the copy control inside its top-right corner.
- `SecretValue` (shown once) wraps it with a dashed, tinted box. The MCP snippets, the local CA fingerprint, the TOTP key and the restore command use it too. Recovery codes keep their Copy and Download pair.
- A command to run on the server is never a bare `<code>`: it is `CliHint`, which shows the Docker form (`docker compose exec paduraksa paduraksa ...`, or `run --rm` when the server must be stopped) and the bare form for a systemd install, each in a `CopyableCode`.
- The sign-in and setup pages are plain HTML, so their `CopyableCode` is `Static`: a native button that `paduraksa.js` answers. The text is in the markup, so it is never a secret. Sign-in keeps the password-reset commands behind a muted "Reset password" button that opens a native popover (`popovertarget`): light dismiss and Escape need no circuit.
- Inline `<code>` stays for a name inside a sentence (a setting, a flag), never for something to run.

**Linking to a field** (`settings-links`). An element with an `id` and `data-highlight-target` is a place an address's fragment can name, and `paduraksa.js` (`highlights`) does the rest.
- It watches for the element, since a page loads its data after the circuit starts and Blazor scrolls to a fragment only when the element is already there (up to 8 seconds). It runs on a full load with a fragment, on a navigation inside the app (it wraps `history.pushState`) and on back and forward, because Blazor takes a navigation to another fragment of the same page in the browser alone.
- It scrolls the element just under the app bar and the open draft's bar (`scroll-margin-top` in `settings.css`), moves focus to its first control, and adds `.pdk-highlight` for 2 seconds: a soft ring and tint in the accent colour, drawn by a layer over the element, so nothing moves. With `prefers-reduced-motion` the ring appears at once and fades, with no pulse.
- The router's own `FocusOnNavigate` moves focus to the page's `h1` a few milliseconds later, so for a second a heading that takes focus hands it back, unless the person pressed a key or clicked.
- `SettingRow` takes the two attributes. `ServerSettingField` sets them from `ServerSettingDefinition.Anchor`, the key in kebab-case (`mcpEnabled` is `mcp-enabled`), and `SettingsRoutes.SettingLink(setting)` builds the address (`/settings/server#mcp-enabled`). A hint that sends a person to a setting uses it. Another page adopts it by putting the two attributes on a row.
- `SettingLinkEndToEndTests` and `ServerSettingsPageTests` (an anchor for every setting of the catalogue) hold it.

**Pills.** `.pdk-chip` and `.pdk-pill` are status (one look, tone by meaning: success, warning, error, info, default). `.pdk-badge` is the smaller label or count (an icon inside it is sized by the badge, never by a `Style=`; there is no inline style in the dashboard). `.pdk-tag` is a neutral tag. A row that summarises and filters is a segmented control with counts, never a row of pills; a row that only summarises is stat tiles.

**Share-bar lists** (`ShareList`: status codes, methods, browsers, bots by kind) are one grid with a subgrid per row. The label, bar, count and percentage columns are as wide as the widest row needs, so every bar starts and ends at the same x. A tiny share is at least 3 px of fill. Counts and percentages are tabular and right-aligned.

**Number fields** hide their spin buttons, so a number beside text fields has the same edges (`DashboardMarkupConventionTests` fails on one that doesn't). They commit when focus leaves them, as text fields do (`NumberFieldCommitTests`, `NumberFieldsEndToEndTests`). A numeric field is never `Immediate`: with a `Min` (a status code from 200) MudBlazor would clamp the first digit typed. Nothing in the dashboard styles itself inline: a size or colour is a class in the stylesheets.

**A switch that is off says what that means.** A health switch's off state has one `pdk-muted` caption under it: what happens to a destination that is down (`UpstreamDrawer`).

**Units.** A compact control writes units short ("1 h", "24 h", "7 d", "30 d", "1 y"), with the long form as its tooltip. Prose writes them out ("7 days").

**Making a change.** Every control that changes the config goes through one path, so the choices, labels, order and styling are the same everywhere.
- **Draftable changes** (everything that is a config command on one object or the global settings): a form, a toggle, a row's delete or a menu item builds a `ReviewRequest` and opens the **review sheet**. Nothing applies or drafts silently.
- **The form:** one primary, **Review changes**, then Cancel and Delete (danger, at the start) where they apply. No "Add to draft" in a form.
- **The sheet:** Cancel, **Add to draft**, **Apply now**, in that order. Apply now is always the filled button and Add to draft the outlined one; Apply now is red when the change removes something.
- **Quick toggles** (a host's switch or wrench, a destination's switch in its form) open the same sheet. Maintenance from the list asks for its options in a small drawer first.
- **Acts at once by design** (certificates, ACME accounts, DNS providers, users, tokens, notification channels, backup): their own confirm where destructive, no review sheet, no draft choice. Their drawers use the same footer (`DrawerActions`).
- **Whole-document changes** (rollback, undo, YAML import): their own review, no draft choice, and the draft-open notice while a draft is open.
- **A guard** (`DashboardChangePathTests`) fails when a component calls the config's execute or add-to-draft methods outside the allowed types.

**A reference is a link.** Wherever a page or drawer names another configured resource (a host, upstream, profile, rate limit, access list, rule set, sign-in provider, certificate, ACME account, DNS provider, local CA, notification channel, user or revision) it links to that resource's page or drawer through a `DashboardRoutes` or `SettingsRoutes` helper. A link to another resource has one look: the accent colour with a soft underline, full when pointed at (the accent is the text colour in this palette, so the underline is what says "link"). In a drawer, a fact or a cell it is `pdk-ref-link`; a chip is `pdk-tag pdk-tag-link`; a status chip that names a certificate or a channel is wrapped in `pdk-chip-link`. `pdk-row-link` (bold, the colour of the text until pointed at) is a row's own title in its own list, never a link to something else.
- **Drawer headings.** A drawer's sections are `FormSection` (a heading, then the rule between sections).
- **A reference to what is gone stays text.** The audit log and the history outlive what they name: an event's target links only when the viewer's page can see the object now (`AuditText.LinkOf(target, existing)`), and a past revision's diff links its objects that still exist (`ChangeList.LinkOf`). A revision always links.
- **Pickers.** `RefPicker.OpenHref` puts a small arrow button beside the select that opens the chosen object (the host form's certificate and profile, the rate limit, access list, rule set and sign-in pickers, the default certificate). There is none for nothing chosen, an object staged in the open drawer (it has no page yet), or "inherit". Two pickers have none on purpose, and `DashboardMarkupConventionTests` lists them with the reason: a route's upstream (the route menu's "Edit upstream" stacks its drawer and keeps the form) and the ACME request's account and DNS provider (a step of the add-certificate drawer, which leaving would drop). A new picker passes `OpenHref` or is added to that list.
- **Not links, with the reason.** A review or draft shows what it is about to change, and leaving it would drop the change (the diff, the simulation of a change). A simulation links its upstream and matched host only against the live config. The live tail links the matched host and leaves the upstream, which the access log keeps as an address, not an ID. A notification event's subject is display text, not an ID.

**All display formatting goes through `DisplayFormat`.** A scoped service (inject it as `Fmt`) and the one place that spells a time, a date, a duration or a number a person reads.
- **Times** are written in the browser's time zone, with its offset where the time stands alone: `2026-11-12 21:03:20 UTC+7`, `21:03 UTC+7`, `12 Nov 2026`. The offset ("UTC", "UTC+7", "UTC-3:30") needs no zone abbreviation table. Ages ("5 min ago") are the same in every zone.
- **Where the zone comes from.** `paduraksa.js` writes two cookies on every load, `paduraksa-tz` (the IANA zone) and `paduraksa-locale`, the way it writes the theme's. `App` reads them, `Routes` hands them to `DisplayFormat.Initialize`, and the circuit keeps them, so server-rendered text and bUnit tests are plain strings with no JS round trip. The first load of a browser that has never opened the dashboard shows UTC (the sign-in or setup page is always the first to load the script). A zone the server's tzdata doesn't know falls back to UTC.
- **Preferences** are one record, `DisplayPreferences` (zone, culture, 24 hour clock), with UTC, invariant and 24 hours as the default. A per-user setting would call `DisplayFormat.Use`; there is no such page. The build runs with `InvariantGlobalization`, so numbers and months are spelled the invariant way until ICU is switched on.
- **Numbers:** `Count` (grouped), `Compact` (axes), `PerSecond`, `RateText`, `Percent`, `Share`, `Bytes` (decimal units, as a network is measured), `FileSize` (binary units, as sizes are typed into settings), `Duration` and `Latency` (ms or s), `Seconds`, `Number`.
- **Not through it:** exported data, API responses, log files and machine values (IDs, fingerprints, what a form field holds), and SVG geometry. `DashboardMarkupConventionTests` fails when a `.razor` or `.cs` file in the dashboard formats with `ToString("...")`, a format string in an interpolation hole or `UtcDateTime`, apart from a short allowlist with the reason for each.
- **Old names.** `TrafficFigures`, `AnalyticsFigures`, `SecurityFigures`, `LiveTailText.Duration`, `RevisionText.Exact` and `Ago`, and `CertificateDisplay.Date`/`DateTime` stay as shims over `DisplayFormat.Invariant`. New code injects the service.
- **A `datetime-local` input has no zone**, so the audit filter reads its value in the zone the page shows times in.

**Charts (`TimeSeriesChart`).** One inline SVG per chart, drawn on the server, and a tooltip drawn by the browser.
- **The tooltip is a DOM element, not an SVG `<title>`.** Every point has a transparent hit column carrying its text in `data-time` (the time in the person's zone), `data-values` (one formatted string per line, in the legend's order, as JSON) and `data-note` (a step observed in part, or "No data"). `paduraksa.js` fills one element on `<body>` from the column under the pointer with delegated listeners. No round trip to the circuit, no library.
- **It appears at once and follows the column.** It sits to the right of the column, flips to its left near the right edge, and is clamped to the chart and the viewport. The column under the pointer is shaded (`is-active`).
- **Blazor re-draws the chart on every poll.** The columns survive and get new attributes; while the tooltip is shown a `MutationObserver` refills it, or finds the column now under the pointer if it was replaced.
- **Touch:** a tap shows it, a tap elsewhere hides it; scrolling hides it. **Keyboard:** the figure is one `role="img"` with an `aria-label` summary (the range, the peak, the steps with no data); there is no tab stop per point.
- **The axis labels** are `TimeFormat` (`TrafficFigures.TimeFormat(range)`) in the person's zone. Parameters: `Name`, `Points`, `Lines`, `Format` (optional, up to two decimals), `TimeFormat`, `ChartPoint`, `ChartLine`.
- **`Compact`** draws the line alone, 36 px tall (`--pdk-spark-height`), for a sparkline in a tile: no axis, grid or time labels. The legend stays in the markup, hidden, because the tooltip takes its line names from it.

**Skeletons on the statistics surfaces.** A skeleton is the loaded component's own markup with placeholders, never a box of its own, so nothing moves when the figures arrive.
- **One markup, two states.** The overview's strip, the three Analytics tabs, the count cards and the security overview render the same frame before and after the first read. What exists only once loaded is read inside content that is not rendered while pending.
- **The shared pieces.** `SecurityTile` and `TimeSeriesChart` take `Pending`; `StatsCard` is the card of the statistics pages and `StatCard` keeps its value and detail lines when `Loading`. A pending piece carries `data-pending="<name>"` instead of `data-section`/`data-tile`/`data-chart`, so a test that waits for the loaded card isn't satisfied by the skeleton.
- **Sizes that data doesn't set live in CSS, once.** `--pdk-spark-height` and `--pdk-chart-plot-height` are read by both states. A placeholder is a real glyph, hidden (`.pdk-pending`), with a bar drawn over it. The count card reserves its detail line (`min-height: 1lh`).
- **An empty chart keeps its frame.** `TimeSeriesChart` with `EmptyMessage` draws axis, plot, time labels and legend row, with the message over the plot.
- **Tables and lists.** Their length is the data's. The body of a table or list card (`.pdk-stats-body`) has a minimum height (12 rem for a table, 8 rem for a list), the skeleton fills exactly that, and only a longer card grows. The Security tab's counter cards have a fixed number of rows, so their skeleton matches exactly. The overview's host and recent-changes cards and the other list pages keep a fixed-height skeleton of their own.
- **The guard.** `SkeletonShiftEndToEndTests` holds each page at its skeleton by blocking the script that starts the circuit, then loads it and compares the bounding boxes of the paired elements within 1 px at 1366 and 390 wide, dark and light, and checks that what sits below keeps its offset. `SkeletonFrameTests` checks the skeleton and loaded markup have the same elements, and `DashboardMarkupConventionTests` that these surfaces use the shared frame and no `MudSkeleton` of their own.

**The connection snippets** of an AI assistant (`McpConnect`) take the token just issued and fill it into the commands. The token drawer shows them open, with the token only in that component's parameter (it goes with the drawer), and says how to turn MCP on when it is off.

**Visual pass.** A UI change gets a throwaway Playwright tour (not committed) on the Dashboard E2E harness in dark, light and 390 px, and what it shows is fixed (see [development.md](../development.md)).

## Voice and vocabulary

Paduraksa is an appliance and reads like a tool: it states things as they are, in a fixed vocabulary. This applies to every string a person reads: the dashboard, the built-in visitor pages, the CLI and `doctor` output, notification messages, API problem texts and the user docs.

**Register.** A string is a neutral declarative. Rewrite any string that:
- addresses the person warmly or chattily ("needs you", "you're all set", "nice", "let's", "just", "simply", "oops", "welcome back");
- uses an exclamation mark;
- personifies the software ("we couldn't", "Paduraksa thinks");
- turns a status into a question or a conversation where a statement would do;
- instructs where the fact would do. A one-time secret says "This token won't be shown again.", beside its copy button, and not "Copy this now" or "Make sure to save this". An instruction stays only where the action is not obvious from the screen and the fact alone would not say it ("Change your password"), phrased neutrally.

Errors say what failed and what to do, without apologising. The second person is for an instruction that needs it, not for warmth. A confirmation the person states themselves ("I have saved the recovery codes", "I have read and agree to the CA's terms") is theirs, not the software's voice, and stays.

**Terms.** One word per concept. The words in the last column are not used for it.

| Concept | Use | Not |
|---|---|---|
| A proxy entry: names, routes, settings | host | site, domain, virtual host |
| A name a host answers for | hostname (a host's "name" in a list) | domain |
| The computer Paduraksa runs on | the machine (or "the server" for the process) | host |
| What a route forwards to; its addresses | upstream (the object), destination (one address of it) | backend, origin, target service |
| Where a host's requests go: an upstream, a redirect or a fixed answer | Target (the Hosts list column) | destination (an upstream's address only) |
| X.509 | certificate | cert |
| A named set of path, method and user-agent rules | rule set (the page, the nav entry, the buttons; a set holds "rules"); "Rules" is the name of the host setting that picks one | ruleset |
| Who may reach a host: addresses, countries, users | access list | ACL, allow list, IP allow list |
| Forward-auth in front of a host | auth provider (the page, the nav entry, the buttons); "Sign-in" is the name of the host, profile, route and defaults setting that picks one | login, forward-auth provider |
| Entering the dashboard | sign in (verb), sign-in (noun, adjective) | log in, login |
| The routing configuration in the database | config | configuration |
| Startup configuration (environment, appsettings) | configuration, "setting" for one value | config |
| A config change waiting to be applied | draft | batch, staging |
| One stored version of the config | revision | version (an object's version is its own) |
| Refusing a request | refuse (answered), drop (closed without an answer) | block, reject, deny (except an access list's own deny rules) |
| A host's, route's or profile's setting | setting | option, preference |

**Button verbs.**

| Verb | Means |
|---|---|
| Apply now | Makes a config change live, in the review drawer. |
| Add to draft | Collects the change for a later, single apply. |
| Review changes | Opens the review of an edit. Nothing applies without it. |
| Save | Stores a change that is not config: a name, a user, a token's scopes, a notification channel. |
| Add | A new entry in a list on a page, or a row in a form ("Add host", "Add route", "Add hostname"). No article. |
| New | Creates the referenced object from beside its picker. |
| Create | Makes something Paduraksa generates: a token, recovery codes, the Owner, the local CA. |
| Edit | Opens a stored object's form. A row that is only read opens ("Open", "Details"). |
| Cancel | Leaves a form or dialog and changes nothing. |
| Discard | Throws away work that is not stored or applied: a draft, a drawer's edits. |
| Close | Leaves a view that has nothing to save. |
| Done | Closes a view that showed a one-time secret. |
| Retry | Reads again after a failed read. |
| Reload | Drops what the page shows and reads the object again (it changed elsewhere). |
| Refresh | Reads a list again in place. |
| Continue | The next step of a short flow. |

**Toasts.** A toast after a config change reads "Applied: " and what changed ("Applied: host app.example.com added", "Applied: rate limit web deleted", "Applied: 3 changes" for a draft), then the revision it is live as, with Undo. The verbs of a stored object that is not config are their own: Saved (a name, a user, a token's scopes, a channel), Created (a token, codes), Added, Revoked, Deleted (a user, a channel, a DNS provider). Adding to a draft says "Added to the draft" or "Started a draft", since nothing is applied yet.

"Confirm" is a dialog's title and a password field's label, never a button. "Update" and "Commit" are not used in the interface; a revision "saved as #N, but it isn't live" is stored and not yet published.

**Removal verbs.** Each has one meaning.

| Verb | Means | Undo |
|---|---|---|
| Delete | Removes a stored object for good (host, upstream, certificate, profile). Confirmed. | The history, for config objects. |
| Remove | Takes an item out of a list inside a form or the draft (a route, a hostname, a header, a draft entry, a chosen file). Stored data is unchanged until it is applied or saved. | Cancel. |
| Revoke | Ends a credential for good (an API token). | None. |
| End | Closes a session. | Sign in again. |
| Disable | Turns an object off and keeps it (user, token, channel, destination). | Enable. |
| Deactivate | Ends an ACME account at the CA. | None. |
| Retire | Stops offering a replaced local-CA root; what it signed keeps working. | None. |
| Clear | Empties filters or the live tail's rows. Stores nothing. | None. |

**Status words.**
- **On** and **Off** are the values of a setting. **Enabled** and **Disabled** are an object's switch (a destination, a channel, a user, a token). A disabled channel is "Disabled", not "paused".
- **Active** and **Deactivated** are an ACME account. **Live** is the revision being served. **Serving** is a host or upstream answering. **Paused** is only the live tail's own pause.
- **Refused**, **Failed**, **Expired**, **Revoked** and **Disabled** are final words of their own; none stands for another.

**Time and counts.** A sentence says "in the last hour" and "the last 24 hours". A tight table header or tile takes the compact unit ("1 h", "24 h", "7 d", "5 min"). A count is a figure before its noun ("3 errors to fix", "1 item needs attention"); "now" is the moment, never "just now".

**Decided.** Four names the sweep left open:
- Auth providers: the page title and the navigation entry, as the buttons and drawers already said. "Sign-in" stays the name of the host, profile, route and defaults setting that picks one. The page is `/auth-providers` (`DashboardRoutes.AuthProviders`).
- Rule sets: the page title and the navigation entry. "Rules" stays the name of the setting. The page is `/rule-sets`.
- The Hosts list column is "Target", since a host's requests can go to an upstream, a redirect or a fixed answer. An upstream's addresses are "destinations".
- Config-change toasts say "Applied" (see Toasts above), not "Saved", "Created" or "Change applied".

**Contracts are not renamed.** API fields and codes (`ipAllowList`, `Enabled` on a channel), YAML keys, CLI flags and the `data-*` attributes keep their names whatever the interface says. The interface calls a channel's `Enabled` flag "Enabled" and "Disabled"; the contract's comments say the same.


## About Paduraksa

`/about` (`DashboardRoutes.About`, `Pages/About.razor`) shows which build is running; `/about/licenses` (`DashboardRoutes.AboutLicenses`) is the same page with the open-source licenses drawer open. Any signed-in user may open either (the router's plain `[Authorize]`): nothing on them is secret, and they show no path, data directory or environment value.

**Where it is linked.** The user menu, below My account and API tokens. There is no side-navigation entry: the navigation groups its entries by area and an entry without a group joins Overview at the top. The user docs mention it in `docs/operating/upgrading.md`, where a person checks which version is running after an upgrade.

**The page** is short, left-aligned, in a column of 640 px, with no page header (the identity card holds the page's `h1`) and no captions:
- **Identity card:** the mark (`BrandMark` at 64 px in a tinted tile), the name, the version large, then one row with the short commit SHA (a `pdk-ref-link` to the commit on GitHub, new tab, `rel="noopener noreferrer"`), a copy button that copies the full SHA, and the build date. If a build has no commit, the card says "Built without a commit ID" and shows neither link nor button. The date of a build that recorded only its day is shown without moving it into the viewer's zone (`DisplayFormat.UtcDay`).
- **Facts**, in the same card, set apart by spacing alone: API `v1`, Runtime (".NET 10.0.14"), the system and architecture, uptime (two largest units; the start time is its tooltip). Whether it runs in a container is not shown: nothing detects it.
- **Links:** Documentation, Source, Changelog, API reference, as compact outlined links (`pdk-about-link`; plain anchors, because `MudButton` overwrites `rel` with `noopener`). The changelog link is the file at this build's commit (`main` without one). The API reference is `DashboardRoutes.ApiReference` (`/api/docs`), which the admin app serves to everyone; `ApiConventionTests` holds the two paths equal.
- **License:** one line, "Apache-2.0 · © 2026 Hizkia Felix" (the holder from `NOTICE`), and the button "Open-source licenses (33)", a link to `/about/licenses`.

**The licenses drawer** (`AboutArea/LicensesDrawer.razor`, opened through `RoutedDrawer`, so the address opens it and Back closes it; another address after `/about/` opens the not-found drawer):
- **Paduraksa first:** the `LICENSE` and `NOTICE` texts behind two `Disclosure`s. The files are embedded from the repository root by the Dashboard project (`ProductLicense`), so there is no copy.
- **Third-party software:** every component of `THIRD-PARTY-NOTICES.json` (`ThirdPartyNotices`), with name, version, SPDX pill, its own copyright line, project link (and the runtime's own notices link) and its license text behind a `Disclosure`. A search box (name, license or copyright) narrows the list, and the count says "2 components of 33". Each row is keyed by its name, so an open text stays with its component when the search changes the list.
- **Alphabetical, not grouped by license:** a person looks a package up by name, and the SPDX pill already says which license it is. A component carries no explanation of why it is there.

**`Disclosure`** is a button that draws its content only while open. The drawer has about 40 license texts, and the closed ones cost no markup; native `<details>` would render all of them.

**The build.** `BuildInfo` (Core) reads the informational version, the `SourceRevisionId` commit and the `BuildDate` assembly metadata; `GET /api/v1/version`, `paduraksa doctor` and this page use it, so they cannot disagree. The Server project writes `BuildDate` at build time: the commit's timestamp from `SOURCE_DATE_EPOCH` (CI and the container build pass it, so a rebuild of a commit is the same), else the UTC day of a local build (a timestamp that changed on every build would rebuild the server project and everything that references it); a `SOURCE_DATE_EPOCH` that is not digits fails the build. The Server registers the `BuildInfo` it reads from its own assembly; a host that does not gets the Dashboard assembly's version and commit.

**The notices.** `scripts/generate-third-party-notices.py` publishes the server (or reads `--publish-dir`) and writes `THIRD-PARTY-NOTICES.json` (what the page renders, embedded in the assembly) and `THIRD-PARTY-NOTICES.md` (the same, to read).
- The packages are those in the publish output's `paduraksa.deps.json`, so test, analyzer and build-only packages are out and so are the ones the shared framework provides. Each one's SPDX license, copyright line (the authors when a package states none) and project URL come from its nuspec in the NuGet cache.
- What no package describes is in `scripts/third-party-notices/extra.json`: the .NET runtime and ASP.NET Core as one MIT entry with a link to the runtime's own notices, SQLite's native library (public domain), the Material icon paths inside `MudBlazor.dll` (Apache-2.0; no icon font or image is served) and libmsquic, which the Dockerfile installs (its version is read from the Dockerfile).
- The script writes values onto single lines, refuses a nuspec without an SPDX expression or an address that is not `http(s)://` (naming `packageOverrides` as the fix), refuses a publish that serves static files from a source it has no entry for, or any font file, and `--output-dir` writes the two files elsewhere. The license texts are `scripts/third-party-notices/licenses` and the repository's own `LICENSE` for Apache-2.0.
- **The guard.** `NoticesCompletenessTests` holds libmsquic equal to the Dockerfile's `LIBMSQUIC_VERSION` and the runtime entry equal to the runtime's major.minor, and runs the generator against the test's own build output (skipped without python3) to assert it writes the checked-in files byte for byte. It fails, naming the script, when a shipped package or its version is not in the notices, when a notice names a package that is no longer shipped, or when the Markdown lacks a component of the JSON.

**Not done.** The Dockerfile does not copy `LICENSE` and `NOTICE` into the image (the assembly in the image embeds all three). Scalar's script bundle carries its own JavaScript dependencies, which the Scalar entry covers but does not list one by one. The CLI has no command that prints the license or the notices.

**Tests.** `AboutPageTests` (bUnit: the identity card, the commit link, a day-only date in another time zone, the facts, the links and their `rel`, the drawer at its address, a component's text staying through a search, the not-found address, the user menu's link for every role) and `BuildInfoTests`.

## First-run setup

**Security.** Until setup completes, the admin app serves **only** the setup page. That requires a one-time **setup token** printed to the logs and the console (`paduraksa setup-token` reprints it), which stops anyone else on the LAN from claiming the instance.

**What was dropped.** The original plan was an eight-step wizard (setup token, Owner, admin access, ACME account, DNS provider, wildcard certificate, global defaults, first host), and a trimmed version was built for a while (`/welcome/{step}`, `SetupWizard`, `WizardStepper`). **Owner decision: no wizard.** It was more ceremony than help. Everything after the Owner is done on its own page, when the person wants. Not done: the admin-access step, the global-defaults step (the defaults have their settings page), passkeys, "export config" in the setup flow, and a backup reminder.

**The token.** At startup (the core host, after the config store) the server writes a 256-bit setup token to `<data dir>/setup-token` (mode 0600; a copy readable by others is discarded and replaced), and deletes it once an Owner exists.
- It logs the token at Warning on **one line** (the console logger is single-line, so a multi-line message would be flattened).
- It prints **one boxed banner** to standard output, which `docker logs` captures. The box is sized to its longest line, and uses `+`, `-` and `|` when the console's output encoding is not UTF-8. `paduraksa setup-token` prints the same banner, or says setup is complete.

**The service.** Setup is incomplete while no Owner exists. `SetupService.CompleteSetupAsync(token, username, password, displayName)` compares the token in constant time, then checks that no Owner exists and creates the Owner in one write transaction (two simultaneous attempts make one). It audits it (a wrong token too) and deletes the token. `SetupService.IsCompleteAsync` is what the admin app asks.

**The page** is one static form in two numbered steps (token, then Owner), posting to `/auth/setup`. It calls the same flow as `POST /api/v1/setup` and signs the Owner in. Like the API's setup endpoint it is refused through the proxy (`setup.viaProxy`). It has a password strength hint (`setup.js`, a classic script from this origin; the form works without it). On success it sends the Owner to the overview.

**CLI.**
- `admin reset-password <user>` reads the password from standard input or a no-echo prompt (never argv), ends the user's sessions and clears a lockout.
- `admin disable-2fa <user>` turns the second factor off.
- Both are audited as the `cli` actor.

**The setup checklist** (`SetupChecklist`, from `SetupProgress`, read from the data, not remembered) on the overview lists the open tasks (`SetupTasks`): two-step sign-in, an ACME account, a DNS provider once there is an account, a certificate (other than the admin's and the root), a first host. Each links to the page that does it. What the user cannot read is not listed. It stays until the tasks are done or it is dismissed.
- **It is in the prerender.** What is open comes from the data in `OnInitializedAsync` (as the signed-in user) and is carried into the circuit with `[PersistentState]`, so nothing is read twice and the overview does not shift when the circuit goes live.
- **Dismissing** writes the `paduraksa-checklist` cookie through `paduraksa.dismissSetupChecklist` (ten years, `SameSite=Strict`). `App` reads it and `Routes` hands it to the scoped `ChecklistPreference`, like the theme and the time zone. A dismissed checklist reads and renders nothing, in both states. Its close button is disabled in the prerender, because nothing listens to a click before the circuit is live.
- **A dismissal from local storage moves to the cookie.** On the first load after the upgrade `paduraksa.js` writes the cookie, removes the stored key and hides the checklist with a `data-checklist-dismissed` attribute on the page, so it never comes back; from the next load the server leaves it out.

**Reused from the old wizard.** Two-step enrolment is `TwoStepEnrollment`, shared with My account: it goes through `IDashboardIdentity` (so its password check takes one of the API's derivation slots), asks the password again, draws the `otpauth://` URI as an inline SVG QR code by `QrCode` (a byte-mode level-M encoder of our own, checked with zbarimg for every version), shows the key for typing, and shows the ten recovery codes once. The first host's reachability check is `UpstreamProbe` (Application): a TCP connection within 3 s to the URL's host and port, closed at once, nothing sent or read. It needs `config:write`, and a refusal is audited.

**Tests.** bUnit for the checklist's tasks and links and the forms; Playwright `FirstRunEndToEndTests` (token, Owner, the overview with its checklist, two-step through the QR step, a local-CA certificate and an uploaded PEM, and the checklist with only what is still open).
