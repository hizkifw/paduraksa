# Admin surfaces

The dashboard, REST API, MCP and CLI are thin skins over one set of Application services, with the same DTOs, authorization and audit. This page covers the REST API (conventions, errors, problem codes, concurrency), MCP in code mode and its sandbox, the health endpoints, the CLI, YAML import and export, and the planned declarative mode.

Contents:
- [The REST API](#the-rest-api-apiv1-admin-port)
- [MCP in code mode](#mcp-in-code-mode-m10)
- [Health endpoints](#health-endpoints)
- [CLI](#cli)
- [YAML import and export](#yaml-import-and-export)
- [Declarative mode](#declarative-mode-after-v01)

## The REST API (`/api/v1`, admin port)

- Minimal APIs, grouped per resource. A built-in .NET 10 OpenAPI 3.1 document plus the Scalar UI at `/api/docs`.
- **Conventions:**
  - typed-ID path parameters (`/hosts/{id:HostId}`);
  - `If-Match: <revision>` for concurrency;
  - `?dryRun=true`;
  - `?force=true` for the admin host's lock-out guard (see [hosting.md](hosting.md));
  - RFC 9457 problem details;
  - cursor pagination (UUIDv7 and Int64Ordered IDs make cursors trivial).
  - What a new endpoint must follow is in [Conventions for a new endpoint](#conventions-for-a-new-endpoint).
- **Resources:** hosts, routes, upstreams, profiles, certificates, acme-accounts, dns-providers, access-lists, rule-sets, error-pages, settings, drafts (see [dashboard.md](dashboard.md)), revisions (with diff and rollback), audit, users, tokens, sessions, metrics, health, `config/export`, `config/import` and `simulate`.

### Conventions for a new endpoint

The API was made consistent before v0.1 (branch `api-consistency`), because after it a change breaks users. A new endpoint follows these, and `OpenApiCompletenessTests` and `ApiConventionTests` fail when it does not.

- **Paths.** Kebab-case, plural collections: `/rate-limit-policies/{id}`. An action on an object is `POST /{collection}/{id}/{verb}` (`/certificates/{id}/renew`, `/dns-providers/{id}/credential`). A collection or object that is one thing in the config document keeps the document's name in JSON (`rateLimitPolicies` is a key of the document, not a path). No alias for an old path: the API is `v1` until a break, and a break is `v2`.
- **JSON.** camelCase members; IDs are typed strings named `id` (or `<thing>Id` for a reference); times are ISO 8601 in UTC (`createdAt`, `expiresAt`); enums are strings; a unit is part of a name (`timeoutSeconds`, `p95Ms`). A nullable member of a response is written as `null`, not left out, where a client has to tell "none" from "not sent" (a page's `nextCursor`).
- **Statuses.**
  - A create is `POST` to the collection: **201** with `Location` and the created object, **409** when it exists.
  - A change is `PUT` (replace) or `PATCH` (partial): **200** with the object. A `PUT` that creates what it names (a route) is 201.
  - A delete is **204**. Two documented exceptions answer 200 with a body: a delete that is a dry run (the plan), and a certificate delete with warnings to report (the vault key could not be removed).
  - An action that makes nothing addressable (`login`, `test`, `simulate`) is 200 with its result, or 204 when it has none. A verb with a body is a `POST`, never a `DELETE`.
- **Errors.** Every failure is an `ApiProblem` (`application/problem+json`), and every operation declares the ones it can give: `ProducesApiProblems(...)`, with 401, 403 and 500 added by the description. 400 for a body, header or parameter that can't be read (it names the parameter), 404 for an object that isn't there, 409 for a clash (an existing ID), 412 and 428 for the preconditions below, 415 and 413 for a body, 422 with `errors` for a request that reads and can't be applied. `ApiStatusMapping` is the one table from a service's result to a status.
- **Concurrency.** An object that carries a `version` is read with `ETag: "<version>"`, and a change or delete to it needs `If-Match` with that tag: missing is 428, stale 412, malformed 400. A whole-config change (`rollback`, `config/import`) pins the latest revision's ID the same way. Creates need none. **Exceptions, with the reason:** certificates, ACME accounts, DNS providers, notification channels, users, tokens and sessions carry no version, because each is changed by one operation that overwrites a field set (a credential, a settings block) and a lost update there costs a retry, not a config: their changes are last-write-wins and say so in their descriptions. A draft is its own case: entries carry the version of what they change, and apply needs the review's token.
- **Pagination.** Every collection answers `{ items, nextCursor }` (`Page<T>`; `nextCursor` is always present, `null` on the last page) and takes `limit` (1 to 200, default 50) and `cursor` (what the previous page gave, opaque; an unusable one is 400). Config and provider collections are ascending by ID, so a cursor survives the deletion of the item it names; history (revisions, audit, notification history) is newest first. Exceptions: the log files (a directory listing), the recent access log (a bounded snapshot, `limit` up to 1000, no cursor) and the upstreams' live health (a state read as a whole). A new collection uses `Pagination.Respond` (or `TryPage`) and `.WithPage()`.
- **Query parameters.** Declared once as a `QueryParameter` (type, range, enum, default, description), declared on the endpoint with `.WithQuery(...)` and read only through `Read(http)`. `HttpRequest.Query` is a banned symbol in the admin project, and reading a parameter the endpoint did not declare throws. A change that runs through the config pipeline is marked `.MayFailToPublish()`, which declares `dryRun`, `force` and `message`.
- **Time ranges.** A read over time takes `range` (`hour`, `day`, `week`, `month`; default `hour`) as a declared choice, read through `MetricsEndpoints.TryParseRange`, and an unknown value is a 400. The metrics overview, analytics and the security overview (`GET /api/v1/security/overview?range=`) share it, with the same reach and steps (`MetricsService.Reach`).
- **The description.** Summary, description where the summary is not enough, a schema for every success (a file or stream gets the schema of its kind), `x-required-permission` or a sign-in requirement, and the request body with `Accepts`. Regenerate `openapi.v1.json` and read its diff ([development.md](../development.md#the-api-description)).
- **MCP.** A path that matches `EndpointRules.SensitivePath` is classified in `EndpointRules` (refused, or reviewed with the reason), or `McpRequestPolicyApiTests` fails.
- **`GET /api/v1/version`** names the product version, the API version (`v1`) and, when the build was given `SourceRevisionId`, the commit. The container build takes it from the `PADURAKSA_COMMIT` build argument.

### The server settings (`/api/v1/server-settings`)

- **`GET`** (`config:read`): every declared setting with its default, its bounds, what the config stores, the value in force and its source (`environment`, `config` or `default`). A restart-only setting also has `running` and `restartPending`. The ETag is the global settings' version.
- **`PUT`** (`server:write`): the body is `settings.server` as it should be, sent as an `UpdateSettingsCommand` over the current global settings. It needs `If-Match` with that version, and it takes `dryRun`, `force` and `message` like any config write.
- **An environment-locked value** is accepted and stored, and isn't used while the environment sets it.
- **The pipeline checks the permission,** not only the endpoint: a change to `settings.server` through `PUT /settings`, an import, a rollback or a draft needs `server:write` too (config.md, "Who may change them").
- **MCP** reaches it like any config write: a dry run first, then `confirm`.

### The identity endpoints (M4)

Code: `Paduraksa.Admin/Identity`. All are under `/api/v1`, and bodies and responses are the `Paduraksa.Contracts.Identity` DTOs.

Route parameters carry the ID's route constraint (`users/{id:UserId}`, `sessions/{id:SessionId}`, `tokens/{id:ApiTokenId}`). A malformed, wrong-type or empty ID therefore does not match the route and is a 404, and the handler receives the typed ID. The `?userId=` filters parse the same way and name nothing when it does not parse.

| Endpoint | Notes |
|---|---|
| `GET`/`POST setup` | First-run setup. |
| `POST auth/login` | 200 with `secondFactorRequired`. The cookie is never in a body. |
| `POST auth/second-factor` | The cookie value rotates. |
| `POST auth/logout` | 204, cookie cleared. |
| `GET auth/me` | User, roles, effective permissions, `authKind` (`AuthKind`, an enum serialized by name: `session`, `token` or `unspecified`). |
| `POST me/password` | |
| `POST me/totp/enrollment` | Needs the password. Returns the otpauth URI once. |
| `POST me/totp/enrollment/confirm` | Returns the recovery codes once. |
| `POST me/totp/disable` | Password in the body (a `DELETE` has no body to rely on), 204. |
| `POST me/recovery-codes` | |
| `GET sessions[?userId]`, `DELETE sessions/{id}` | The list is a page like every collection; the delete is 204. |
| `POST sessions/revoke-others` | Needs a session. A token caller is refused by that requirement first with 403 `auth.sessionRequired`, so there is no 422 branch. |
| `GET tokens[?userId]`, `POST tokens`, `DELETE tokens/{id}` | `POST` is 201 with the secret once; the list is a page. |
| `GET`/`POST users`, `GET`/`PATCH`/`DELETE users/{id}` | The list is a page, `POST` is 201 with `Location`, `DELETE` is 204. `PATCH` takes `displayName`, `roles` and `disabled`, applied by one `UserService.UpdateAsync` call in one transaction, the last-Owner guard included: it all applies or none of it does. |

**A token cannot manage tokens or sessions.** Every `tokens` and `sessions` endpoint answers a token caller with a 403 (`code: auth.sessionRequired`, "API tokens can't manage tokens or sessions; use a signed-in session"). A leaked token therefore cannot mint a longer life for itself or end the session that is watching it.

The endpoints check `users:read` and `users:write` up front and otherwise only that there is a caller. The services decide the rest: a user reads themselves, and `tokens:all` and `sessions:all` reach other users'.

**Statuses:**

| Status | When |
|---|---|
| 401 with `WWW-Authenticate: Bearer` | No or bad credentials. `error="invalid_request"` for a malformed or ambiguous credential, `invalid_token` for a bad token. Sign-in refusals are always one generic message without a challenge. The endpoint filters run after the body is bound, so a malformed body is a 400 even for an anonymous caller. |
| 403 | Denied. |
| 404 | Not found (also another user's session or token). |
| 422 | Invalid (`errors`). A wrong password on a signed-in change is 422 (never 401, so a UI does not read it as a dead session). |
| 409 | Conflict: `identity.conflict`, or `identity.lastOwner`. |
| 429 | Too many attempts. |
| 503 | Busy. |
| 413, 400 | Over-size body, unreadable body. |

Problems never echo a password, code, token or cookie.

### Problem codes

Every problem `code` and every `errors[].code` is dotted segments in lowerCamelCase, matching `^[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*)+$`. That is the shape the config codes already had (`host.certificate.unusable`, `settings.defaultCertificate.unusable`). The former flat (`rate_limited`), dotted-underscore (`admin.internal_hop`) and mixed (`publish_failed` beside `publish.failed` in one answer) forms are gone.

**Where they live.**
- The problem codes are one static class per area: `Paduraksa.Admin.Api.ApiCodes` with `Auth`, `Setup`, `Csrf`, `Admin`, `Ca`, `Config`, `Identity`.
- The error codes live beside their validators: `ConfigErrorCodes`, `ConfigWarningCodes`, `CertificateErrorCodes`, `IssuanceErrorCodes`, `SimulationErrorCodes`. Also `command.message.invalid`, `config.store.unavailable` and `revision.corrupt` on `ConfigService`, and `json.invalid` on `CanonicalConfigJson`.
- A unit test (`ProblemCodeConventionTests`) scans both and fails on anything outside the pattern.
- **Every 403 carries one.** `ApiProblems.Build` fills `auth.permissionRequired` for a 403 that names no more specific code.
- The OAuth `WWW-Authenticate` `error=` values (`invalid_token`, `invalid_request`) are RFC 6750's, not codes, and stay as they are.

**The list (as of M4 to M6).** Later milestones add codes beside their validators (for example the `accessList.*`, `ruleSet.*`, `authProvider.*` and `errorPages.*` families in [waf.md](waf.md)).

*Problems:*
```
auth.required, auth.invalidToken, auth.invalidSession, auth.invalidAuthorization, auth.ambiguousCredentials,
auth.invalidCredentials, auth.invalidCode, auth.sessionExpired, auth.sessionRequired,
auth.permissionRequired, auth.rateLimited, auth.busy, setup.required, setup.unavailable, setup.complete,
setup.invalidToken, setup.viaProxy, csrf.refused, admin.internalHop, admin.sourceDenied, ca.notFound,
config.publishFailed, identity.conflict, identity.lastOwner, identity.refused, identity.password.wrong
```

*Config (validator and compiler):*
```
compile.failed, apiVersion.unsupported, id.empty, id.duplicate, version.negative, enum.undefined,
name.required, name.tooLong, name.duplicate, name.invalid, notes.tooLong, value.null, address.invalid,
ipAllowList.dropDenied.withoutAddresses, rateLimit.requests.invalid, rateLimit.burst.invalid,
rateLimit.rate.invalid, rateLimit.header.required, rateLimit.header.unexpected, rateLimit.header.invalid,
robots.customText.required, robots.customText.unexpected, robots.customText.tooLong,
robots.customText.invalid, hsts.maxAge.invalid, hsts.preload.needsIncludeSubDomains,
hsts.preload.maxAgeTooShort, serverHeader.value.required, serverHeader.value.invalid,
trustedProxies.proxies.required, trustedProxies.proxies.unexpected, trustedProxies.header.invalid,
trustedProxies.option.unexpected, upstream.destinations.required, upstream.destinations.noneEnabled,
upstream.maxConnectionsPerServer.invalid, upstream.passiveHealth.reactivationPeriod.invalid,
upstream.activeHealth.path.invalid, upstream.activeHealth.interval.invalid,
upstream.activeHealth.timeout.invalid, destination.address.invalid, destination.address.credentials,
destination.address.queryOrFragment, host.names.required, host.name.invalid, host.name.duplicate,
host.profile.unknown, host.rateLimit.unknown, host.certificate.idRequired, host.certificate.idUnexpected,
host.certificate.noneWithForceHttps, host.certificate.unusable, settings.defaultCertificate.unusable,
host.routes.required, host.routes.unexpected, host.admin.duplicate, host.admin.httpsRequired,
host.admin.lockout, host.redirect.required, host.redirect.unexpected, host.respond.required,
host.respond.unexpected, host.tag.invalid, redirect.target.invalid, redirect.target.credentials,
redirect.target.queryWithPreservePath, redirect.statusCode.invalid, respond.statusCode.invalid,
respond.body.forbidden, respond.body.tooLong, respond.contentType.invalid, route.path.invalid,
route.path.encoded, route.path.notCanonical, route.path.duplicate, route.upstream.required,
route.upstream.unknown, route.rateLimit.unknown, command.message.invalid, config.store.unavailable,
revision.corrupt, json.invalid
```

*Certificates:*
```
certificate.delete.inUse, certificate.names.required, certificate.name.invalid, certificate.chain.required,
certificate.validity.invalid, certificate.fingerprint.required, certificate.exists, certificate.builtIn,
certificate.text.tooLong, certificate.managed, certificate.replace.notAllowed,
certificate.reissue.notAllowed, certificate.issue.refused, certificate.localCa.missing,
certificate.localCa.inUse, certificate.adminCoverage, certificate.vault.keyNotDeleted,
certificate.import.tooLarge, certificate.import.unreadable, certificate.import.wrongPassword,
certificate.import.unsupportedKeyFormat, certificate.import.noMatchingKey, certificate.import.noUsableName,
certificate.import.weakKey, certificate.import.caCertificate
```

*Issuance:*
```
acme.account.directory.invalid, acme.account.contact.invalid, acme.account.eab.incomplete,
acme.account.eab.secretMissing, acme.account.algorithm.undefined, acme.account.inUse, acme.account.inactive,
acme.account.unknown, acme.text.tooLong, acme.dnsProvider.label.required, acme.dnsProvider.kind.undefined,
acme.dnsProvider.credential.missing, acme.dnsProvider.inUse, acme.dnsProvider.unknown,
acme.order.dnsProvider.required, acme.order.dnsProvider.notAllowed, acme.order.names.required,
acme.order.name.invalid, acme.order.names.tooMany, acme.order.name.wildcardNeedsDns01,
acme.order.name.ipNeedsHttp01, acme.order.challenge.undefined, acme.order.keySpec.undefined
```

*Simulation:*
`simulation.request.url`, `simulation.request.clientIp`, `simulation.request.peerIp`, `simulation.request.transportIp`, `simulation.request.method`, `simulation.request.headers`, `simulation.request.tls`, `simulation.request.tooLarge`, `simulation.request.conflicting`, `simulation.draft.rejected`, `simulation.revision.unreadable`, `simulation.busy`, `simulation.store.unreadable`.

*Warnings:*
`certificate.auto.noCoverage`, `certificate.specific.unusable`, `certificate.specific.noCoverage`, `certificate.default.unusable`, `host.admin.forceHttpsIgnored`, `host.admin.internalDisabled`.

### The simulator's targets

`POST /api/v1/simulate` takes `target`, a `kind` and what it names. A draft kind is validated and compiled as the real change would be, and never stored or published.

| `kind` | Names | Needs |
|---|---|---|
| `live` (the default) | the snapshot serving traffic | `simulate` |
| `revision` | `revision`, a stored revision | `simulate`, `config:read` |
| `draftDocument` | `document`, a whole config that is not stored | `simulate`, `config:read` |
| `draftCommand` | `command`, a pending command on the current config | `simulate`, `config:read` |
| `draft` | `draft`, the caller's stored draft | `simulate`, `config:write` |

- **`draft`** applies the stored draft to the current head as its review does (the same replay, then a dry run of one whole-document replace). It reads the draft when the request runs.
- **Who:** a draft is its owner's, and a token acts on its owner's draft. Another user's draft, an unknown one and a discarded one are all 404 (`SimulationStatus.DraftNotFound`), as the draft endpoints answer. It needs `config:write` because reading a draft does; a viewer may simulate the live config but not a draft (403).
- **Errors, as problems:**
  - entries changed elsewhere (or depending on one that is) are 409 `draft.blocked`, with an error per entry as the draft's apply gives (`SimulationStatus.DraftBlocked`);
  - what the dry run refuses (an invalid whole, a store that is unavailable) is the same 422 or 503 as for the other draft kinds;
  - a draft with no entries simulates the current config, and says so in its notes.
- **Decision:** the review of a draft with some blocked entries shows the rest; a simulation refuses instead (409 `draft.blocked`), because a simulation over part of what the draft says would show a change the draft does not make as a whole, and an agent would report it as checked.
- **The dashboard** offers it as "My draft" in the simulator's target picker while the user has a draft with entries (`?target=draft` selects it), and the draft drawer's "Simulate the draft" uses it too. The reviewed single change still offers "The change under review".

### The resource API (M4)

Code: `Paduraksa.Admin` with `AddPaduraksaAdminApi`, `MapPaduraksaAdminApi` and `UsePaduraksaAdminApi`, one file per resource group (`Api/*Endpoints.cs`). `AdminApp` only calls them.

**Pipeline.** `UsePaduraksaAdminApi` is the first middleware, called before `UsePaduraksaAdminAuth`. That is a call order, asserted by a test in which an exception inside the auth pipeline gets its 500 problem. Endpoints are mapped after the auth pipeline.

The security-headers middleware (auth) adds its headers when the response starts, so a failed response, reset by the problem middleware, keeps them. Its default CSP is added only when none is set, and the docs page sets its own before the response starts.

**Errors.** Every error under `/api` is RFC 9457 `application/problem+json`, in one shape: `ApiProblem` in Contracts, with `type`, `title`, `status`, `detail`, `traceId`, `instance`, and `code` for a stable machine name. `errors`, `relatedErrors`, `warnings`, `conflict`, `revision` and `version` appear when they apply.
- The auth pipeline's problems (401, 403, 429, 503 setup gate, identity outcomes) are the same shape with a `code`. Its former validation `errors` dictionaries are `errors` lists of `{path, code, message}` like every other 422.
- `ApiPipelineMiddleware` logs an unhandled exception with the trace ID and answers a fixed 500 with the ID and nothing of the exception. It gives routing's and binding's empty 404, 405 and 400 a body. A `BadHttpRequestException` keeps its status (413).
- A `JsonException` is answered with a fixed sentence and the member's path, never its message (which names CLR types). A polymorphic member without its discriminator is a 400.

**JSON.** Bodies are read and written through `AdminJsonContext` directly: JSON only (415 otherwise), strict.
- `AdminJsonContext.AddTo(options)` (idempotent) puts the same strictness, the typed-ID converter factory and the enum-name converters on the host's HTTP options. A context in the resolver chain alone brings neither.
- Core enums use their `ModelNames` names, and config enums camelCase.
- Every DTO a response or request carries reads back what the API writes. A nullable constructor parameter has a default (a test walks the DTO graph and fails for one without), and a unit test writes and strictly reads every DTO through the HTTP options.

**Typed IDs.** Route constraints per ID type (`/hosts/{id:HostId}`; a malformed or wrong-type ID is a 404). Parameters and schemas in the OpenAPI document are the typed-ID string schema (`pattern`, a valid example).

**Caller.** A handler that needs the caller takes `AdminCaller` as a parameter.
- It binds from the request feature the authentication middleware set, never from a body or the query, so it is absent from the OpenAPI document.
- Binding runs before endpoint filters, so an anonymous request is bound to a stand-in that the `RequireAuthenticated` or `RequirePermission` filter answers with the same 401.
- A handler that takes the parameter without one of those filters is a wiring bug and throws.

**Concurrency.**
- Reads carry `ETag`: an object's version (`"3"`), or the latest revision's ID for `GET /config` and the lists.
- A change to an existing object needs `If-Match` with one quoted strong tag. Missing is **428**. Stale or weak is **412** (the problem's `conflict` says what is current). An unquoted tag, a list, `*` or a version with a leading zero is 400.
- `POST` creates need none (an existing ID is 409).
- A route is changed through its host: `If-Match` is the host's version.
- `POST revisions/{id}/rollback` takes the latest revision's ID.
- `?dryRun=true` runs the whole command and returns the plan without committing. `?message=` is the revision note.
- Statuses: committed 200 or 201 (deletes 204, a PUT that creates a route 201 with `Location`; the conventions above are the rule, this is the config pipeline's part), invalid 422 (the pipeline's errors verbatim), denied 403, store unavailable 503.
- **A change that was stored but could not go live is a 500 problem with `code: config.publishFailed`**, the stored `revision` and the new `version` (also the `ETag` header). The client knows the change is stored and not live. Repeating the request with the old `If-Match` is a 412, not a duplicate. Nothing retries the publish.

**Pagination.** `?limit=` (1 to 200, default 50) and `?cursor=` (opaque: the last item's typed ID) on every collection (see the conventions above for the exceptions), answered as `{ items, nextCursor }`. Revisions, audit and notification history are newest first and their cursors are positions in the store; the others are cut from an in-memory list ascending by ID. An invalid cursor is 400.
- Audit filters: `from` and `to` (strict ISO 8601 with a zone; `from` after `to` is 400), `actor` (system, cli, a user ID or a token ID: what changes made through a token record is the token), `action` (exact or `prefix*`), `target`, and `outcome` (`success`, `denied`, `invalid`, `conflict`, `failed`; added with the dashboard's audit page in M5).
- A filter over 128 characters is 400.

**Body limits.** The auth pipeline's 64 KiB for JSON. `WithAdminBodyLimit` raises it for certificate uploads (1 MiB plus framing) and simulate (1 MiB). Uploads declare `AcceptsAdminContentTypes("multipart/form-data")` so a cookie-authenticated upload passes CSRF. The CSRF check covers every unsafe method on a cookie-authenticated request whatever its body, and is tested for the multipart upload and the bodyless renew.

**Secrets.**
- Uploads are streamed from the multipart request into `ICertificateImport`: parts `password`, `label`, then `file`. A duplicate `password` or `label` is a 400, nothing after the file is read, and the password lives in a cleared `char[]`.
- DNS credentials and EAB keys are stored by the services (`DnsProviderService.CreateWithCredentialAsync`, `RotateCredentialAsync`, behind `POST /dns-providers/{id}/credential`, `DeleteWithCredentialAsync`, and `AcmeAccountService.CreateWithEabAsync`). They authorize and validate before they store anything, and delete the secret again when the provider or account is not created. No response type can carry a secret.
- "Test credentials" (`DnsProviderService.TestAsync(id, origin)`) needs `providers:write` and is audited (`acme.dns_provider.test`, verdict only).
- `RevisionQueryService` and `AuditQueryService` (Application) are the public, DTO-only readers over the internal revision store and audit chain.

**Issuance.** ACME request and renew run under the application's stopping token, not the request's. A client that gives up does not cancel an issuance, and the answer is still synchronous when it finishes. A DNS-01 order waits for propagation and can outlast a typical client timeout, so `GET /certificates/{id}/order` shows progress.

**Description.**
- OpenAPI 3.1 at `/api/v1/openapi.json`. It is generated at development time into `OpenApi/openapi.v1.json`, embedded, and served from there. ASP.NET's generator rebuilds every operation's schemas and took 3 s warm and 6 to 9 s cold with some 2.7 GB of allocation.
  - `OpenApiSnapshotTests` keeps the copy equal to a fresh generation.
  - The typed-ID examples are constants so the bytes are stable.
  - `OpenApiDocumentCache` generates and caches only when no copy is embedded.
- Scalar at `/api/docs`: the script is embedded in the package and served locally, with no fonts, telemetry, agent or MCP hook.
- CSP `default-src 'none'; script-src 'self' 'nonce-...'; style-src 'self' 'unsafe-inline'; ...; connect-src 'self'`. Scalar's bundle contains no `eval` or `new Function` (a test checks), so there is no `'unsafe-eval'`. Both are anonymous (`AllowAnonymousAdmin`).
- Every operation, the identity ones included, has a summary and either `x-required-permission`, a sign-in requirement or `security: []`. The description names which resources take `If-Match`, the 428, 412 and 400 rules, `config.publishFailed`, the path, JSON, status and pagination conventions, and has a `servers` entry (the admin app's own origin).
- **Every query parameter is declared with a type, a range or enum, a default and a description** (`ApiQuery.cs`; the audit filters, `range`, the log filters, `revision`, `mode`, `userId` and the rest were prose before). Every 2xx has a schema, and every operation lists the problems its shape implies (a body: 400 and 415; a path ID: 404; a query: 400; a versioned change: 412 and 428), each described by what it means here.
- **`OpenApiCompletenessTests`** walks the generated description and the mapped endpoints: the declared and the listed query parameters are the same set, every success has a schema, the problem lists are complete and an operation answers a page exactly when it takes `limit` and `cursor`. `ApiQueryTests` covers the guard that throws on a parameter an endpoint reads and did not declare.
- Integration tests check that every mapped `/api/*` endpoint carries `RequiredPermissionMetadata`, `AdminAuthenticatedMetadata` (`RequireAuthenticated`) or `AdminAnonymousMetadata`. A table checks the exact permission of every resource route and method.

**Tests** use real authentication: users in every role from the identity services, an API token per role (scoped to the role) as the Bearer credential, and real cookie sessions for the CSRF tests.

**HSTS (M6).** Nothing new on the API. It is part of the config document, so `PUT /api/v1/settings` (the global defaults' layer), profile writes and host writes carry it. `?dryRun=true` returns the validator's HSTS errors like any other. The generated document describes `HstsSettings` beside `TlsSettings` (an integration test asserts the schema and its field descriptions, so the description cannot silently lose it).

**Not done:** `If-None-Match` and 304, metrics (M9), an ID-generation helper (IDs are client-chosen), and a hermetic test of the ACME happy path (needs a CA).

## MCP in code mode (M10)

`/mcp`, streamable HTTP, the official `ModelContextProtocol.AspNetCore` SDK. All four admin surfaces (dashboard, REST, MCP, CLI) call the same Application services with the same DTOs, authorization and audit. The audit record notes which channel was used.

### Off by default

- `Paduraksa:Mcp:Enabled` is `false` unless set. A bad value fails the start like any option.
- **Switched live** (since `server-settings`). It is a server setting (config.md, "Server settings"), so the Owner or an Admin can turn it on from Settings, Server. An environment value locks it.
  - The MCP server and `/mcp` are always registered and mapped.
  - `McpSwitch`, right behind the API's problem middleware and before authentication, answers `/mcp` with a bare 404 while it is off. No token is asked for, and nothing behind the switch sees the request.
  - Switching it off, or changing the idle timeout, stops and drains the sandbox pool. The next allowed call builds a new pool with the values then in force.
  - The pool is detached at once, on the config's writer thread, in the order the changes come (`McpToolkit.Detach`). Its children are killed in the background, one pool at a time (`McpSwitch.ChangedAsync`), so a late kill after a quick off and on only reaches the old pool, never the one the on's first call built.
  - The dashboard's card follows the switch, and turning MCP on says in the review drawer what it allows.
  - **Decided by the owner:** someone holding a stolen Owner session could switch MCP on; such a session can do worse anyway.
- While it is off, no sandbox child runs, and the dashboard's card says in one line how to turn it on.
- **Why:** the sandbox is new and runs a model's code. The operator opts in for this release, and the default can be reconsidered once the isolation has run in the homelab for a while.
- `Paduraksa:Mcp:IdleTimeout` (default 10 minutes, validated from 10 seconds to 24 hours) is how long an unused sandbox child lives. It is a server setting too, and a new pool takes it.

### The decision: two tools

This area first planned 15 named tools (`list_hosts`, `upsert_host`, `apply_change`, `tail_access_log`, ...). They are replaced by two, `search` and `execute`, modelled on Cloudflare's "Code Mode for MCP" (https://blog.cloudflare.com/code-mode-mcp/). Each takes one string, `code`, a JavaScript async arrow function that a sandbox runs.

**Why:**
1. The model's context holds two descriptions of about two kilobytes each however large the API grows, not a schema per tool.
2. The API is already described by its OpenAPI document, so a new endpoint is reachable through MCP the day it exists, with no tool to write and no drift between the tools and REST.
3. A model reads, filters, compares and writes in one call instead of one round trip per step.
4. Authorization has one source, the REST pipeline, because `execute` goes through it.

**What it costs:** a model's code runs on the server, so it needs a sandbox that is a real boundary (below), and the model has to write correct JavaScript.

The per-tool `destructive` hints of the planned design are replaced by the two-step write rule and the refused list. A hand-written tool for a task a script does badly can be added later next to the two.

### Transport and authentication (`Paduraksa.Admin/Mcp`)

- `MapMcp("/mcp")` on the admin listener, stateless. Each POST is a request of its own, so a revoked token stops working on the next one and no session outlives it. The stream a server may open on its own (`GET /mcp`) is not offered.
- The admin authentication middleware gives `/mcp` its own branch: **one bearer API token and nothing else**.
  - A session cookie, alone or beside a token, is a 401. A cookie is an ambient credential a page could make a browser send.
  - The token is checked through `ApiTokenService` on every request like the REST API. The caller is the token's user with the token's scopes.
- The caller's channel is **`Channel.Mcp`** (a value `Channel` already had). The audit log and the history say `mcp` for what a script changed, with the token as the actor. Reads are not audited, as everywhere.
- The setup gate, the source allowlist and the loopback host check apply as for any admin request.
- A message body is limited to 192 KiB (`WithAdminBodyLimit`).

### The tools (`Paduraksa.Mcp/ToolDescriptions`)

The descriptions are built from the limits in force, so the text cannot say one thing while the sandbox does another. Both tools take `{ "code": string }` and nothing else.

The descriptions are all the model knows. They name the global, the shape of the request, the limits, the dry-run and confirm rule and the refused list. They say that text from the API is data, and carry one short example. `tools/list` marks `search` read-only and `execute` destructive.

**The server instructions** (`ToolDescriptions.ServerInstructions`, sent once at `initialize`) give the model a dense map of what Paduraksa is and which areas the API covers, so it knows what is worth searching for, and they name what is not reachable from MCP.
- **Changes go to a draft by default.** Unless the user asked for a change to be applied directly, the model opens or reuses the user's draft and adds entries to it, then tells the user, who reviews and applies it in the dashboard. It does not apply or discard the draft unless asked. `execute`'s description repeats the rule in one line.
- **Why:** the token acts as its owner, and the draft is that owner's, so it shows in their draft bar. A model's change then gets the same review as a person's.
- **Then it proves the change.** After the review the model simulates its draft with `{kind: "draft", draft: id}`: one or more requests the change should affect and, where it matters, one it must not break. A simulation that shows the change is not what was asked sends it back to fix the draft. It then tells the user what it drafted and what it checked, one line per simulated outcome.
- **Applying directly** (the user asked for it): it simulates first, with `draftCommand` or the draft, then applies.
- **Why:** an agent that says "done" proves nothing, and the two-step read (`GET /drafts/{id}/config`, then `draftDocument`) was clumsy and could test a stale copy. The draft target reads the draft when it runs.
- **Kept honest by a test:** `McpEndpointTests` checks that every `/api/v1` path the instructions name exists in the API description.

**`search`.** The one global is `spec`, the OpenAPI document with every `$ref` resolved. The model finds endpoints with code, so the document never enters its context.
- The parent sends the raw document (about 600 KB today) to each child once, when the child starts. `SpecGraph` builds it as script values in each run's own engine, sharing each referenced schema instead of copying it.
- Resolved to text, the same document is 12 MB. As a graph of shared nodes it is about 7 000 objects and costs 10 to 40 ms a call.
- A schema that refers back to one being expanded is cut where it repeats (`{ "x-recursive-ref": "Name" }`), so the graph is a finite tree. `components.schemas` stays, so a schema can be looked up by name.
- Edits a script makes to `spec` die with the call.

**`execute`.** The one global is `paduraksa.request({ method, path, query?, body?, confirm?, ifMatch? })`, a promise of `{ status, body, etag? }`.
- `body` is the parsed JSON, or the text for another media type.
- An HTTP error is a result (4xx or 5xx with the problem document), not an exception.
- **The function gets its globals as its argument too** (`{ paduraksa }` for execute, `{ spec }` for search), and `globalThis` as `this`. A red-team agent reported `paduraksa` intermittently undefined. The global was there on every run (a fresh engine per run, tested over many, concurrent, post-idle and post-tampering runs); what failed were functions written `async ({ paduraksa }) =>` or reading `this.paduraksa`, which received the `undefined` that `then` passes and strict mode's `undefined` this.
- `ifMatch` is the one header a script can send (the quoted `etag` a read returned), because every change to an existing config object needs it. No other header, and never the token.

### The sandbox: a killable child process

`Paduraksa.Mcp`, **Jint 4.17.0** (pinned in `Directory.Packages.props`): a managed interpreter, no native V8.

**Decision, and what it replaced.**
- The first version ran Jint inside the proxy with guards: removed globals, replacements for `JSON.stringify`, `join`, `toString`, `repeat`, `padStart`, `replace`, `concat` and `[Symbol.replace]`, a per-statement allocation count, a BigInt pre-check. It was measured against and it held.
- But every review round found another native that one step could turn into a stack overflow or gigabytes. `replace(/a/g, "$'")` built a string as long as the input squared and took 8 GB. A numeric string or a `String` object walked past the string-length guard.
- A stack overflow or an out-of-memory in the proxy's process is the proxy down. Patching holes as they are found is the wrong layer: there is no end to the ways a script can misbehave. So what must hold is not "the script cannot" but "when it does, it costs one expendable process".
- The guards were deleted. **Jint runs stock** (the whole standard library, `eval` included) in a child process that the parent limits from outside and kills on any breach.

The surface is small enough to read:
- the child's entry point (`SandboxChild`);
- the frame codec and messages (`SandboxProtocol`);
- the parent's run protocol, watchdog and pool (`RunProtocol`, `ChildProcess`, `SandboxProcessPool`);
- and the request policy, unchanged.

#### The child

It is this binary started as `paduraksa mcp-sandbox` (hidden from `--help`), by `dotnet <assembly> mcp-sandbox` when the server was started that way.

It holds **no token, no configuration, no data directory and no network use**, and almost none of the server's environment. The environment is cleared: only `DOTNET_ROOT`, `DOTNET_ROOT_X64`, `DOTNET_ROOT_ARM64`, `PATH` and `LD_LIBRARY_PATH` as the server has them, and its own `DOTNET_*` and CPU-limit settings. The master key and the setup token are not in it.

**Frames.** It reads and writes length-prefixed JSON frames on stdin and stdout: a 4-byte big-endian length, then UTF-8 JSON.
- At most 4 MiB to the child (it carries the API description and the API's answers) and 1 MiB from it.
- A larger or zero length is a violation before a byte of the body is read.
- A frame that is not valid UTF-8, not JSON, not an object with a string `t`, or deeper than 16 is a violation.
- Messages: `ready` (with what hardening applied) and `spec` once. Then per run `run {tool, code, limits}`, the child's `request {rid, id, options}` answered by `response {id, ...}`, and `result {rid, ok, value|error, truncated, note, recycle}`.

**Every frame is bound to its run.** The `run` frame carries a fresh random run id (`rid`). The child echoes it in every `request` and `result`. A frame without it, or with another, is a breach: a result prepared before the code arrived, a request queued earlier, a replay of an earlier run's. All are killed, none served.
- While a child waits in the pool, the parent keeps a read pending on its stdout. Anything it says, or its end, kills it at once, and it is never handed out.
- A child that is compromised knows the id of its own run and can lie about that run's result, as any code can lie about its own answer. What the binding stops is a frame for code that was not run.
- Its stderr is drained into a 2 KiB tail that goes to the log when a child dies and to no caller. It is counted: more than 1 MiB over a child's life (a flood costs the parent a core to read) kills it.
- A fresh `Engine` per run. The engine thread has a 256 MiB stack.

**Jint's own constraints** (`MaxStatements` 300 000, `LimitMemory` 64 MiB, `LimitRecursion` 200, `StackOverflowGuard`) are on in the child, so that the ordinary runaway ends with a clean message in the script's own words. They are **not** the boundary and nothing relies on them. The tests run the same scripts with them off.

**The child hardens itself** at startup, before the file system is closed, on the thread that runs scripts (several of these are per thread):

| Measure | Value |
|---|---|
| Inherited descriptors | every descriptor above stderr that has no close-on-exec is closed, before anything else |
| `RLIMIT_CPU` | 300 s of CPU for its whole life |
| `RLIMIT_NOFILE` | 256 |
| `RLIMIT_CORE` | 0 |
| `PR_SET_NO_NEW_PRIVS` | set |
| `PR_SET_PDEATHSIG` | SIGKILL when its parent dies |
| **Landlock** | denies all file system access (every thread where the kernel has `LANDLOCK_RESTRICT_SELF_TSYNC`, else the script thread and its descendants) |

- **Inherited descriptors.** A descriptor survives `exec` only if it has no close-on-exec, so the ones the child holds without it are exactly what it inherited: the runtime opens its own with it, and a child with nothing leaked reports `Inherited descriptors: none`. .NET opens the server's descriptors with close-on-exec, but native code does not always (OpenSSL reads the CA bundle with `fopen`, and a child started in that moment inherits the file), and Landlock and seccomp never touch a descriptor that is already open: a leaked listener, client connection or database file would stay usable from inside the interpreter. So the child closes them first, before `RLIMIT_NOFILE` and the filters, and says how many it closed and what they were (a socket, a pipe, a file's path). The parent logs a non-zero count as a leak in the server.
- **Seen in production.** The first deployed child reported `closed 2 (2 x socket)`: the HTTP/3 listener's two UDP sockets on :443, which libmsquic opens without close-on-exec. Before the sweep every sandbox child held them, and neither the seccomp filter (it refuses creating a socket, not using one) nor Landlock (TCP only) stopped a script that escaped the engine from reading or sending QUIC datagrams on the public port. Any host with HTTP/3 on and MCP enabled was exposed between M10 and 2026-11-14. This is the child's side of the boundary and does not depend on the parent being careful; `close_range` would be shorter, but it would take the runtime's own descriptors too.
- A warm-up script and a preload of every referenced assembly run first, because the runtime loads assemblies lazily and a closed file system would otherwise kill the child at its first use of one.
- `RLIMIT_NPROC` is not set. It counts every process of the user, so a low value would stop the child creating its own threads.
- Each outcome is a line of the `ready` frame. The parent logs the ones that did not apply, once per child, and the whole report once per server run.
- The child also leaves when its stdin closes.

#### The child's network and its routes back to the parent are closed

Threat model G2. The child talks over its pipes and needs no socket and no other process.

**Landlock network rules** (ABI 4, Linux 6.7). A second ruleset handles `LANDLOCK_ACCESS_NET_BIND_TCP` and `CONNECT_TCP` and allows no port, so every TCP bind and connect is refused with `EACCES`. It is the kernel's refusal at the socket call, so it also covers what io_uring does. An older kernel reports `Landlock network: not available (ABI n, needs 4)`.

**The seccomp filter** (`SyscallFilter`). Landlock does not see UDP or raw sockets, so the filter answers `EPERM` to:
- `socket` and `socketpair` of any domain but `AF_UNIX`;
- a foreign architecture, and the x32 ABI (which would otherwise step around the compares);
- the calls in the table below.

Every other system call is allowed.

**Decision:** the filter is a short, explicit denylist, one reason per row, written for x86-64 and arm64 (an architecture it has not been checked on gets none, and says so). It is not a profile of the runtime's calls: such a profile would have to follow every .NET release, and a mistake in it kills the child at random.

**The principle:** the child is the boundary, and the list closes the routes from the child back to the parent. The child shares the container and uid with the parent, so the parent's memory, its descriptors with the vault's files and its listeners, and its network are otherwise one `ptrace` away wherever Yama's `ptrace_scope` is 0.

The filter returns `EPERM`, not a kill, so a library that probes does not take the child down. `SyscallFilterTests` runs the program through a BPF interpreter against a reference table of the kernel's numbers, per architecture, and installs it on a thread of its own to make the calls.

| Call (x86-64 / arm64 number) | Why it is refused |
|---|---|
| `ptrace` (101 / 117) | Attach to, read and write another process of the user, the parent first. |
| `process_vm_readv` (310 / 270), `process_vm_writev` (311 / 271) | Read and write the parent's memory without ptrace. |
| `pidfd_open` (434), `pidfd_getfd` (438), `kcmp` (312 / 272) | Take over the parent's descriptors, and find which ones. |
| `bpf` (321 / 280), `userfaultfd` (323 / 282), `perf_event_open` (298 / 241) | Kernel attack surface and a side channel on the parent. |
| `io_uring_setup`, `_enter`, `_register` (425, 426, 427) | Socket, connect and open as queued operations the filter would not see. |
| `socket`, `socketpair` (41, 53 / 198, 199) | Unless the domain is `AF_UNIX`. |

**Considered and not taken:**
- `PR_SET_DUMPABLE=0`. It would stop same-user processes from ptracing the child, but the parent inspects `/proc/<pid>` of its children, and the only other same-user processes are other children, which the filter already denies `ptrace`.
- Denying `kill` and `tgkill`. The runtime signals its own threads with `tgkill`, so a hostile child can still signal the parent: that is availability, not the vault.

**Without TSYNC.** If the kernel has no `SECCOMP_FILTER_FLAG_TSYNC` (the report says "the script thread"), the filter and Landlock cover only the script thread and the threads it creates afterwards. That is safe today because Jint runs on the script thread alone. But a future host function that hops threads would run outside both.

**Landlock file system mask.** It follows the kernel's ABI (REFER from 2, TRUNCATE from 3, IOCTL_DEV from 5), because an access a ruleset does not name stays allowed. With only the first thirteen bits, `truncate(2)` could empty `paduraksa.db`, `vault.db` or `master.key`. The report names the ABI used.

What stays open to an escaped script is what a process always has: the pipes it was given, local sockets, signals and the rest of the runtime's system call surface. Docker's default seccomp profile and Landlock: see install.md "MCP" (G14).

#### The parent is the guard (`ChildProcess`, `RunProtocol`)

It starts the child with `DOTNET_GCHeapHardLimit` 256 MiB, workstation non-concurrent GC and tiered PGO off. A **watchdog** per run kills the child's process tree (`Process.Kill(entireProcessTree: true)`) on:

| Breach | Limit |
|---|---|
| Wall time | over 10 s (not counting the time the parent spends answering the script's requests) |
| CPU time | over 5 s (from `/proc/<pid>/stat`, in `sysconf(_SC_CLK_TCK)` ticks) |
| Resident memory | over 384 MiB (from `/proc/<pid>/status`, every 50 ms) |
| The whole call | over 30 s |
| Requests | more than 50 `request` frames |
| Protocol | a frame that is malformed, oversized or not what the protocol allows there |
| Liveness | the child exiting or closing its pipe, or the caller going away |

- The caller always gets a short fixed message, `The script was stopped: <reason>.`. The parent never relays the child's stderr or a stack, and relays a script's own error message only after bounding it (600 characters, control characters replaced).
- A child that reports out-of-memory or an unexpected exception is replaced rather than reused.
- The output cap (64 KiB of UTF-8, cut with a note) is applied by the parent to the result frame.

**cgroup v2** where this process may create a sub-cgroup: each child is put in its own with `memory.max`, `cpu.max` and `pids.max`.
- cgroup v2 enables controllers for the children of a cgroup only while that cgroup has no processes, and the server is a process of the cgroup it was started in. So the standard delegated layout is made: the server moves into a leaf of its own cgroup (`paduraksa-server`), the controllers are enabled on the cgroup it left, and the children go in leaves of a sibling (`paduraksa-mcp/child-N`).
- If a step is refused, the server is put back and the log line names the refusal: `EACCES` (not delegated), `EROFS` (Docker's default read-only cgroup file system, which works without it) or `EBUSY` (the cgroup has other processes, as a shared user scope does).
- It is never relied on. The only case seen on the development box is the `EBUSY` of a scope shared with other processes, so the success path is written to the delegated-service pattern and not yet exercised.

**The pool** (`SandboxProcessPool`):
- Lazy: no child until the first call, which pays the cold start once.
- Warm while MCP is in use: a child that finished a run waits for the next, one run at a time in each.
- Up to 4 children spawned on demand. A fifth call waits 5 s for one, then is told to retry. One token can hold all four. That is accepted, and [the MCP page](../automation/mcp.md) says to give the token only to assistants you trust.
- A child is despawned after `Paduraksa:Mcp:IdleTimeout` without a run (checked by a timer on the pool's `TimeProvider`).
- A child is replaced after any kill, or after 200 runs or half of its CPU lifetime.
- The same timer, at most every 5 s, looks at each idle child's CPU time and kills one that used more than 0.4 s since the last look (a loop that outlived its run, a spinning child).
- Disposing the pool, which the admin app does when it stops, kills every child.
- Children are started from one thread that lives as long as the pool, because `PR_SET_PDEATHSIG` fires when the parent *thread* that started the child exits, and a thread-pool thread would be retired.

**Measured** (Release, this box):

| Measure | Value |
|---|---|
| Cold start of a child to its first answer | 175 to 195 ms |
| Resident memory of an idle child | 59 MiB (the budget: 384 MiB) |
| A warm `search` run | 0.3 to 0.8 ms |
| A warm `execute` with one request | about 3 ms over the pipe |
| From a breached wall limit to the answer | 75 to 130 ms with a 50 ms poll |
| The next call on a new child | about 180 ms |

**What the boundary does not do.**
- It does not stop a script from burning its 5 s of CPU four at a time, or from filling a child's 256 MiB, which is what the limits are for.
- It does not make a script's own output trustworthy (the parent bounds and cleans what it relays).
- A bug in the kernel's or the runtime's isolation is out of scope.
- An escape from the engine lands in a process with no secret, no network use, no files and a few seconds to live.

### The request path across the boundary

The request policy never moves into the child. `paduraksa.request` in the child sends its options as JSON (`JSON.stringify`) in a `request` frame. The parent's `RequestBridge`:
1. validates every field by name. A script's mistakes are TypeErrors it raises in the script: an unknown option, a body on a GET, a `dryRun` of its own, a malformed `ifMatch`, a body over 256 KiB or nested deeper than 32 levels (`SandboxLimits.MaxBodyDepth`, an object or array as the body is level 1: half the API's own JSON limit of 64);
2. applies the policy (below);
3. dispatches;
4. answers with a `response` frame the child turns back into `{ status, body, etag?, dryRun?, note? }` or into a rejection.

- **The depth is measured before the parse** with a reader that keeps no recursion, so a body one level too deep says `The request body is nested deeper than 32 levels.`. It used to be parsed with a `MaxDepth` of 32 on the whole options object, which failed with the generic `request takes one object` (a red-team agent's report).

Nothing the child sends is used before it is checked. A compromised child can ask for exactly what a script could, and no more. The fuzz target feeds the parent's run protocol arbitrary bytes as the child's output to hold that.

### The request client (`Paduraksa.Admin/Mcp/InProcessApiDispatcher`)

**Decision: in process, through the real pipeline**, as the token's caller.
- The admin app's pipeline is captured as a request delegate when it is built (`McpPipeline`, the first component of the app, ahead of the API's exception middleware).
- A script's request is a synthetic `HttpContext` with the MCP request's connection, `Host` and caller feature, run through it. The setup gate, routing, body limit, CSRF check, the endpoint's permission filter, binding, validation, the services and problem details are the REST API's own. There is one place that decides what a token may do, and nothing here can disagree with it.
- The alternative was a loopback `HttpClient` to the admin listener with the token attached outside the sandbox. It needs the listener's address, trust in the local CA's certificate and a place in `AdminAllowedSources` (loopback is not implied), and makes every call a TLS handshake. The in-process path has none of that.
- The answer is read into a bounded buffer.
- The caller's identity, scopes and channel are the ones authentication set. The code never sees the token because it is not in the request.

### The request policy (`RequestPolicy`, `EndpointRules`)

Applied before anything is dispatched, on top of the API's own permission check, which still runs.

**Only what the API description names.**
- A path must be a plain path under `/api/v1/`: no query or fragment in it, no dot or empty segment, no percent-encoding, no trailing slash. The router decodes after a check could have judged.
- It must match an operation of the generated description (case insensitively, as the router does). Anything else is refused with a pointer to `search`.
- Five methods: GET, POST, PUT, PATCH, DELETE.

**The refused list** (`EndpointRules.Refused`, one explicit list with a reason each):
- backups and restore;
- API token management, users, roles, sessions, and the account's own password and second factor;
- sign-in and setup;
- every request that would carry a secret into a model: a DNS provider's credential, an ACME account's EAB key, a notification channel's secret, a certificate upload with its private key;
- the live tail stream (use `GET /api/v1/logs/recent`);
- `GET /api/v1/metrics` and `/metrics` (use `/api/v1/metrics/overview`);
- the API description itself.

Refused whatever the token, scope or `confirm`. Rules for endpoints that do not exist yet (roles, restore) exist before the endpoints do (`InSpec: false`), so they are refused the day they appear.

**The guard** (`McpRequestPolicyApiTests`).
- The test walks the generated OpenAPI document. It fails for an operation whose path matches `SensitivePath` (backup, restore, acme, token, user, role, session, secret, credential, vault, eab, password, totp, recovery, auth, login, setup, key, metrics, dns-provider, acme-account, notification, upload, import, export, certificate, and the like) and is on neither the refused list nor `EndpointRules.Reviewed`. `Reviewed` is the list of such endpoints that were looked at and allowed, each with its reason.
- A second test shows the guard catches a new `/api/v1/vault/keys`.
- A third fails for a rule of an existing endpoint that covers no operation, and for an operation on both lists.

**Two-step writes.** A POST, PUT, PATCH or DELETE without `confirm: true` is not sent.
- If the operation has a dry run, the request is sent with `?dryRun=true`, and the plan comes back as `{ status, body, dryRun: true, note }`.
- Otherwise the answer is `{ status: 428, body: { requiresConfirm: true, method, path, note } }` and nothing is sent.
- Only the boolean `true` confirms (`"yes"` and `1` do not). A script cannot set `dryRun` itself.
- A POST that only reads (`simulate`, an error-page preview, a draft's review: `EndpointRules.ReadOnlyPosts`) needs no confirm.
- A draft's open, add, remove and discard are writes that need `confirm` and declare no dry run. Its apply declares one (the review). See [dashboard.md](dashboard.md).
- Which operations have a dry run is **read from the API description**. Every change that goes through the config pipeline reads `?dryRun=` (`Preconditions.TryGetDryRun`), and the description declares the parameter on exactly those operations: an operation transformer keyed on `config:write` plus the config pipeline's publish-failure metadata, with a test that the two sets are equal. The client has no list of its own.

**Read-only by default.** A token without write scopes can only GET, because the API's permission filters say so. A viewer's `confirm: true` write is a 403 and leaves no revision.

### Prompt injection

The proxy's data holds attacker-controlled text (user agents and paths in the tail, request hostnames). In this design it can only be data.
- A script can `eval` it (the child is a stock engine), but what runs there has no token, no secret, no files and no network.
- Its only way to act is `paduraksa.request`, which is read-only unless the token can write, refused for anything secret-bearing or destructive, and two-step for the rest, in the parent.
- The tool descriptions tell the model so.
- Tests: hostile text in the tail (a "run this script" instruction, quotes, `</script>`, `__proto__`) comes back as a string and nothing it says is executed or sent. A response with `__proto__` keys pollutes no prototype.
- The safe default is a token with read scopes only (the dashboard's token drawer offers read scopes first).

### The logs read

`GET /api/v1/logs/recent` (`logs:read`, `ListRecentAccessLog`) is the live tail's buffer as one JSON array, oldest first, with the tail's filters and `limit` (1 to 1000, default 100). It is what a client that cannot hold a stream open reads. The stream endpoint itself is refused in the sandbox.

### The dashboard

The API tokens page (`/settings/tokens`) has a closed-by-default card with:
- the endpoint (from the page's own address);
- a Claude Code command and a Claude Desktop config (through `mcp-remote`, with the token in an environment variable), carrying a `<token>` placeholder, never a secret;
- a line that a read-only token is the safe default and where a change shows (History and Audit);
- a note that the admin certificate comes from the local CA, so its root has to be trusted where the client runs (Node clients read `NODE_EXTRA_CA_CERTS`).

Client setup is in [the MCP page](../automation/mcp.md).

### Tests

The proof is that a misbehaving script costs only its child.

**Unit** (`Paduraksa.Tests.Unit/Mcp`):
- the frame codec: round trips, every length outside the limit, a stream that ends inside a frame, not-UTF-8 and not-a-message frames;
- the request bridge: dry run, confirm, 428, the read-only POST, refusals including `/mcp` itself, every option error, the query, the body and answer limits;
- the policy and the tool descriptions.

**Integration** (`Paduraksa.Tests.Integration/Mcp`, real children):
- values, errors with their positions, the spec, and the request round trip (ordering, unawaited requests, answers, a failing dispatcher);
- twenty scripts that used to be dangerous (`$'` and `` $` `` replacements, `repeat('1e9')`, `concat` bombs, deep `flat`, `String` and `JSON.stringify`, bind chains, BigInt shifts and powers, a catastrophic regex, loops, allocation, recursion, promise floods). They run with the engine's constraints on and with them off, each ending as a result, a clean error or `The script was stopped: ...`, with the parent unharmed and the next call served;
- the runaway ones stopped by the parent with no constraints;
- a real stack overflow, a memory breach, a wall-time breach, a CPU breach, the request cap, a total-time breach and a caller who goes away;
- ten broken fake children (garbage, an oversized, zero or cut frame, not JSON, no type, an unknown type, request ids out of order, an exit, a closed pipe) and three that never say they are ready;
- the pool: 4 at once, the fifth told to retry, replacement after N runs, idle despawn and lazy respawn on a manual `TimeProvider`, a warm child kept warm, shutdown killing every child with no orphan, no child before the first call or while MCP is off;
- the child's environment (no master key), what it applied, and that it leaves when its stdin closes;
- frames bound to their run: a fabricated result, a result with no or a wrong run id, a request queued before the code, a frame of an earlier run, a frame sent while idle;
- a stderr flood, a caller who gives up while the child starts, an idle child that spins;
- the descriptors a real child inherits (pipes only, none of the server's listeners or files), a listener, both ends of a connection and a file that the test leaks without close-on-exec being closed by the child, and Landlock denying a read to the thread that applied it.

**Admin** (`Admin/Mcp*`):
- the official client over streamable HTTP lists the two tools, searches, and executes as the viewer, operator and owner (the actor is the token);
- no token, a bad one, a Basic one, a cookie alone and a cookie with a token are 401;
- a revoked token stops at the next call;
- an oversized message is 413;
- limits end as tool errors;
- dry run versus confirm on a real host, the etag flow, 428, a viewer's write refused with nothing left;
- the revision and audit events on the `mcp` channel with the token as actor;
- the OpenAPI walk (above), the reviewed reads on a host that holds secrets, hostile tail text, off by default, and stopping the server killing its children.

**Dashboard:** bUnit tests of the card. **Fuzz:** `mcp-sandbox` (see [testing.md](testing.md)).

### Not built

- The current config as a YAML resource. The tools do not depend on it, and a resource can be added beside them.
- The OAuth authorization of the MCP specification (tokens only, as in [identity.md](identity.md)).
- A per-token switch for MCP.
- Auditing of reads.
- A rate limit on `/mcp` beyond the 4 children and the admin pipeline's own.
- A full seccomp profile of the runtime's system calls. Considered and not taken: it would have to follow every .NET release. Only the socket door is closed, above.
- Windows or macOS hardening. The child runs there with the parent's limits and kill but without the system-call hardening, and says so in its `ready` frame.

## Health endpoints

- `GET /healthz`: process alive, snapshot loaded.
- `GET /readyz`: listeners bound, a snapshot live, not draining, and (from M3) the vault unlocked.

Both are on the admin port, plus the loopback plain-HTTP fallback listener when enabled. They are unauthenticated, with no details beyond status.

Readiness means "serving traffic", not "the config database is reachable". A degraded start serves the last-known-good config and is still ready, so `/readyz` answers 200 with the body `degraded` instead of `ready` (see [config.md](config.md)).

They are for **external** monitors and orchestrators (Uptime Kuma, Kubernetes probes, Gatus), which can probe them over HTTP without spawning anything. The image's own Docker `HEALTHCHECK` is the exception (see [deployment.md](deployment.md)).

## CLI

The same binary, using `System.CommandLine`.

| Command | Purpose |
|---|---|
| `paduraksa serve` | The default. Runs the server. |
| `setup-token` | The first-run setup token. |
| `admin reset-password <user>`, `admin disable-2fa <user>` | Account recovery. |
| `config export\|import [--dry-run] [--mode]` | See below. |
| `vault rotate-kek` | See [vault-and-certificates.md](vault-and-certificates.md). |
| `backup --out <file>`, `restore <file>` | Built in M10. The passphrase comes from a file, the environment or a prompt, never argv. |
| `doctor` | The host check, read-only (see "`doctor`" below). `doctor --liveness` is the admin `/healthz` over loopback, for the container `HEALTHCHECK`. |
| `migrate` | Database migrations. **Not built**: migrations run at startup, and `paduraksa --help` has no such command. |

### `doctor`

Code: `Paduraksa.Server/Cli/Doctor/` (the checks, the seams, the runner) and `DoctorCommands.cs`. The user's page is [the CLI](../automation/cli.md#doctor); this is the design.

- **A check is a small unit** (`IDoctorCheck`) over a `DoctorContext`: the effective options and the seams to the host (`IDoctorFileSystem`, `IPortProbe`, `IDoctorHttp`, `IQuicProbe`, a `TimeProvider`, and the two database looks). The unit tests describe a host in memory with fakes; the end-to-end tests run it over a real data directory made by a real server.
- **Read-only is a design rule, tested.** `TheDoctorChangesNothing` compares the data directory before and after. That shaped four choices:
  - the vault's look (`IVaultInspection`, a Core capability, so the key stays inside the vault) goes through the same `KekProvider` the vault starts with, so the source priority, the key format and the mode rules are the vault's own, but with `generate: false`: a data directory with no key answers "none yet" instead of writing `master.key`;
  - the databases open read-only (`PaduraksaSqlite.ReadOnlyConnectionString`, `MigrationInspector`). Even a read-only connection to a WAL database creates `-wal` and `-shm` when they are missing, so with no `-wal` beside the file (no server holds it, and a clean close removes it) it is opened `immutable`; with one present it is opened normally, since the WAL may hold commits the main file lacks;
  - writability is `access(2)`, not a temporary file;
  - a port is tested by binding it and letting go (`[::]` dual-mode, like Kestrel's wildcard), never by connecting to a public port.
- **Migrations are compared by ID, both ways.** EF's own pending list ignores applied migrations it does not know, so `MigrationInspector` lists them: that is the downgrade failure. Pending is `[info]`: it is what every upgrade looks like before its first start.
- **Who holds a port.** A held port is "the running Paduraksa" when the admin `/healthz` answers `ok` or `unavailable` on this installation's loopback candidates (`DoctorCommands.HealthUris`, the same as `--liveness`), else "another process". Only the admin has a `/healthz`, so a held public port is attributed through it: if a Paduraksa answers on the admin port, a held 80 or 443 on this host is taken to be its own. A port below 1024 that this user may not bind says nothing about being free (EACCES comes before EADDRINUSE), so it is a warning, or info when a Paduraksa is running.
- **The key beside the vault** says what the dashboard's attention item says: both read `MasterKeyAdvice` (Core).
- **The options check** loads the options the way `serve` does (`PaduraksaHost.LoadOptions`, the same validator). An invalid configuration is a failed `options` check and exit 1, not 3, so the rest of the report still prints; the checks that need options are skipped with an info line. Settings that the configuration sets (`ServerSettingEnvironment`) are listed as locked, and a differing value stored in the dashboard is a warning.
- **The clock** compares the system clock, taken at the midpoint of the request, with the `Date` header of `https://www.cloudflare.com/cdn-cgi/trace`: over 30 s (a TOTP window) is a warning, over 5 minutes (ACME, certificate validity) a failure. No network, or no `Date`, is info. It runs only with `--check-clock`: the owner wants no outward request a person didn't ask for, so a plain `doctor` reports the clock as not checked. Cloudflare because the server already talks to it for its edge ranges; nothing is sent but a GET.
- **The running server's version** is read from `/api/v1/version`, which needs a token (`--token-file`, or `PADURAKSA_TOKEN`), and only over loopback, since the certificate check is skipped there. Without a token the line says the version was not compared. We chose that over an unauthenticated version on `/healthz`, which would tell any scanner of the admin port which build runs.
- **Output** is ASCII, like every line the CLI prints: `[ ok ]`, `[info]`, `[warn]`, `[FAIL]`. `--json` is `{ version, passed, summary, checks[] }` (`DoctorJsonContext`, source-generated), with lowercase statuses.

#### The CLI reads the sign-in policy as the server does

`admin reset-password` (and `backup`, which builds the identity services) read the identity options from `CliSettings.ApplyStoredAsync`: the environment's value, else the one the latest config revision stores (`StoredServerSettings`, read-only), else the default, through the same `ServerSettingsState` and `ServerSettingsBindings.ResolveIdentity` the server uses. It runs after the database is migrated, so an upgraded schema is read. A database that stores nothing or can't be read leaves the environment and the defaults, as the server does.

### `config export` and `config import` (M10)

```
paduraksa config export [--revision ID] [--output FILE] [--url] [--token-file]
paduraksa config import FILE|- [--mode merge|replace] [--dry-run] [--message] [--base-revision ID] [--force] [--url] [--token-file] [--allow-insecure-http]
```

Code: `Paduraksa.Server/Cli/ConfigCommands.cs`.

**Decision: the CLI is a client of the running instance's admin API, never of the database.**
- "One writer, one process" (see [config.md](config.md)) means the CLI cannot write config.
- A client keeps everything an import does (validation, the lock-out guard, the audit event with the token as the actor, the one revision) the pipeline's, with no second implementation.

**Authentication and address.**
- The token comes from `--token-file` (first line) or `PADURAKSA_TOKEN`, never from an argument. It needs `config:read` to export and `config:write` to import.
- The address is `--url`, else `PADURAKSA_URL`, else this installation's own admin listener. Those are the candidates `doctor --liveness` probes: the loopback plain-HTTP fallback when it is on, else HTTPS to the admin port.
- A loopback HTTPS address is not certificate-validated. The admin certificate is the instance's own CA's, which a short-lived process does not load. It is the same call `doctor --liveness` makes, and the request follows no redirect and uses no proxy.
- Any other address must present a certificate the system trusts.
- A token is never sent over plain `http://` to an address that is not loopback unless `--allow-insecure-http` says so (the CLI refuses).
- A token file that group or others can read is warned about, as `backup --passphrase-file` does.

**Decision: no offline export from the data directory.** An instance that cannot start has `last-good.json` and its backups for its document. A second reader of a database a server owns would need its own rules for a half-written WAL.

**Output.**
- A dry run prints the changes (`+`, `-`, `~` and the path) and warnings.
- A refusal prints each error with its YAML path and line and exits 1.
- Exit 0 is success and "nothing to change".
- Tested against a running test host (`ConfigCommandsTests`).

## YAML import and export

**Format:**

```yaml
apiVersion: paduraksa.dev/v1
kind: Config
settings: { defaults: {...} }
profiles: [...]
upstreams: [...]
hosts:
  - id: host_0J9zb3xQk1Lr8m2V7aYc4D   # optional on import; matched by id, else by first hostname
    names: [jellyfin.example.com]
    profile: media
    routes: [{ path: /, upstream: jellyfin }]
certificates: [...]   # metadata + binding only; never key material
```

- YamlDotNet is used with a source-generated context (see the decision below for what that became).
- Import goes through the normal command pipeline (validate, compile, diff, one revision). Modes: `merge` and `replace`.
- The same canonical document is what revisions store, so export is "current revision as YAML".
- The import format above is a friendlier DTO than the document (M10): optional IDs, references by name (`profile: media`, `upstream: jellyfin`) and `kind:`. Import maps it onto the strict canonical `ConfigDocument`, assigning IDs and resolving names. Only that document is stored, and export writes it back in a form that imports to the same document.
- Later: **declarative mode** (below), where a watched file is authoritative and the UI becomes read-only for the objects it manages.

### As built (M10)

Code: `Paduraksa.Application/Yaml`, the API in `Paduraksa.Admin/Api/ConfigTransferEndpoints.cs`, the page in `Paduraksa.Dashboard/Pages/ConfigTransfer.razor`.

**YamlDotNet is a parser and an emitter, nothing else.**
- Decision (the original design said "static (source-generated) context"): the YAML is read into a plain node tree (`YamlTreeReader`) and written from one (`YamlTreeWriter`). The typed side is the document's own source-generated `System.Text.Json` contract (`ConfigJsonContext`).
- So there is no YAML type model to keep in step with about 40 records, no reflection and no type that input can name. A YamlDotNet static context would have been a second description of the document, and a deserialiser that instantiates what the tags say.
- `ConfigYamlSchema` reads the shape from the JSON metadata the generator already holds (property names, nested types, which are required). A field added to the document is in the YAML with no change here.

**The format is the canonical document, friendlier in three ways** (all in `ConfigYamlSchema`):
1. `version` is not a key. The pipeline assigns versions on every change.
2. A reference to another object is its **name**, under the key without `Id`:
   - `profile: media`, `upstream: jellyfin`;
   - `rateLimit: {policy: strict}`, `accessList: {accessList: office}`;
   - `ruleSet: {ruleSet: Block AI crawlers}`, `maintenance: {bypassAccessList: office}`.

   It is looked up ignoring case among the YAML's objects and, when merging, the stored ones that are left alone. The built-in rule-set presets can be named too. A name two objects share is `yaml.reference.ambiguous`, not a guess. An ID is accepted in its place, taken as it is even when no object has it. A valid document can name an object that is gone in a layer nothing uses, so its export has to read back, and the validator refuses one that matters.
3. `id` is optional everywhere (hosts, routes, destinations and every named object).

Other rules:
- `kind: Config` and `apiVersion: paduraksa.dev/v1` head the file.
- A certificate is only its binding (`certificate: {mode, certificateId}`, an inventory ID). The document has no certificate objects, so no key material can be in an export or an import.
- Keys are camelCase like the JSON API.
- Unknown keys are errors with a "did you mean". For `version`, `password` and a reference written by ID, the error is a sentence of its own.

**Identity, so that importing twice changes nothing.**
- An entry with an `id` is that object.
- Without one it is the stored object of the same name: a host is any stored host that serves the first name it lists, a route is the same match and path on the matched host, a destination is the same address on the matched upstream. This holds unless another entry claims it by `id` or took it first.
- Anything else is new, with a new ID.
- This is the same in both modes. A replace that regenerated every ID would break every reference to them. A rename needs the `id`.
- New IDs come from `ConfigYamlImporter.RandomIds`, injectable so the fuzz target and the tests get the same ones every run.

**Merge and replace** (`ConfigImportMerge`).
- **Replace** is the document the YAML describes. A list it leaves out is empty, and `settings` it leaves out are the defaults, so what it does not name is deleted.
- **Merge** is the stored config with each of the YAML's objects replacing the stored object with its ID **as written**. It is not field by field: a key left out is gone from that object, which is the only meaning of "update" that can also unset something. The new ones are added after. Nothing is deleted, and `settings` are replaced only if the YAML has them.
- Both are one `ReplaceConfigCommand` built on the revision the import read. The caller's `If-Match` can name another, and then conflicts when it is stale.
- Validation, compilation, the lock-out guard, the diff, the audit event (`config.replace`, channel of the caller, the note "YAML import (merge)" unless given) and the single revision are therefore the pipeline's.
- A dry run returns the changes and warnings and commits nothing.

**Export is the revision, as YAML** (`ConfigYamlExporter`).
- The canonical document's JSON is walked through the schema, in the order revisions store it, without versions.
- A reference is written as the name when exactly one object has it (else the ID).
- It leaves out the empty lists and mappings that read back from absent as the same. `passiveHealth: {}` stays, because absent means something else. So does a required `names: []`.
- A password has no key in either direction, so an access list's user is `{username, credentialId}`.
- **The round trip is a property.** Any valid generated document is exported, read back and has the same canonical bytes (versions aside, which the YAML does not carry). `ConfigImportServiceTests` imports an export in replace mode on a fresh store and compares the SHA-256 of the revisions, which are equal. There is one commit on each side: the pipeline gives every object the revision's `Seq` as its version, so documents built by several commits have other versions than the same documents imported at once, and the hash covers them.

**Safety** (`YamlLimits`, `YamlTreeReader`). The input can come from the API.
- The bytes must be valid UTF-8 (a BOM is fine), at most 4 MiB (the API answers 413), with exactly one document.
- Limits: nesting 24 levels, scalars 512 KiB, keys 256 characters, anchors 64, alias uses 1 000, and the tree 200 000 nodes **counting every alias expansion**.
- An alias is expanded by copying inside that budget, so a billion-laughs document stops at it in tens of milliseconds and a few tens of megabytes.
- Every tag is refused (`yaml.tag`). Mapping keys must be unique scalars. A merge key `<<` is just an unknown key.
- The scanner throws `InvalidOperationException` for some malformed input (an unterminated flow collection followed by more lines), which is reported as a syntax error like the rest.
- Errors carry stable codes (`yaml.*`, `YamlErrorCodes`), a JSON pointer into the YAML and its line and column, and never name a CLR or parser type.

**Credentials** (decision).
- A user's `credentialId` in an import must be one the vault holds a password under (`IPasswordCredentials.Open(id).IsSet`), or one the live config already names (the vault lost it, which the pipeline reports as a warning; an export of such a config must stay importable).
- Otherwise it is `yaml.credential.unknown` at the key, and a **dry run reports it as an error too**, with the diff, so a dry run says what applying would do.
- A user written without a `credentialId` keeps the credential the stored list has for that name (`AccessListPasswords`). A user nobody has a password for fails the validator as it always did.
- The check runs only for a caller who may write config, so it cannot be used to find out which credential IDs exist.

**Errors point into the YAML.** The pipeline's validation errors index the document it was sent, and the compiler's backstop its canonical form. Both are mapped back to the entry that wrote the object and to the key that wrote the field (`/hosts/0/profileId` is `/hosts/0/profile`), with the line. In a merge, an error in a stored object the YAML left alone is a related error, still pointing into the document.

**API** (behind `config:read` and `config:write`, in the OpenAPI document).
- `GET /api/v1/config/export[?revision=ID]` answers `application/yaml` with the revision as `ETag`, `no-store`, 404 for an unknown revision, 400 for a malformed one.
- `POST /api/v1/config/import?mode=merge|replace&dryRun=true|false[&force][&message]` takes `application/yaml` (or `text/yaml`, `text/plain`; 415 otherwise).
  - Merge is the default.
  - An optional `If-Match: "rev_..."` names the revision the YAML was prepared against (412 when it is stale; without it the import uses the latest it read).
  - It answers like every whole-config command: `ConfigCommandResult`, 422 problem details whose `errors` point into the YAML, 403 without the permission.
- A session cookie may send the YAML types (`AcceptsAdminContentTypes`) but not `text/plain`, the one a cross-site form can send, which only a token caller (curl) may use. A cross-site or form post is refused as for every unsafe route.

**Dashboard** (Settings, "Export and import").
- Download writes the live config through the browser's download (`paduraksa-config-r42.yaml`).
- Import takes pasted text (capped at 10 000 characters, at most 30 000 bytes of UTF-8, under the circuit's 32 KiB message) or a chosen file (streamed, up to 4 MiB), merge or replace.
- "Review import" maps it (its errors listed with path and line, nothing else happens) and hands the command to the existing review sheet. Its dry run is the diff, its warnings are shown, Apply (red for replace) makes the one revision and offers Undo. A conflict reloads the review.
- The page follows the no-filler rule: the two help lines say what each mode deletes, and the one line under Export says what is never in it.

**Tests.**
- Unit: the reader's caps, tags, aliases and bombs; the writer's quoting (any text reads back as the same text, a country code `NO` stays text); the schema mapping and its errors; merge; and the export/import round trip as a property over `ConfigGen`.
- Data: `ConfigImportServiceTests` on a real database (modes, dry run, errors, credentials, permission, conflict, the revision hash).
- Integration: the API (`ConfigTransferApiTests`) and the CLI against a running host.
- Dashboard: bUnit for the page and one Playwright test (paste, review, apply, download, read back as a no-op).
- Fuzz: `yaml-import` (see [testing.md](testing.md)).

**Left out.** `certificates:` as a section (the document has none), offline `config export`, YAML comments (the exporter writes none and the reader drops them), and the declarative mode below, which builds on this.

## Declarative mode (after v0.1)

For declarative deployments (Kubernetes manifests, Helm, Compose with a mounted file, NixOS, a git repo synced by Flux, Argo or `git-sync`): the desired config lives in files the deployment owns, and Paduraksa converges to them. It is not a blocker for v0.1. It builds on the M10 YAML format and import pipeline.

- **Source.** `Paduraksa:Declarative:Path`, a file or a directory of `*.yaml` documents merged in name order (a ConfigMap mount is a directory).
  - It is watched with a debounce. Kubernetes updates a mounted ConfigMap by swapping the `..data` symlink, so the watcher follows the directory, not the inode.
  - It is also reconciled at startup, before the listeners open, so a pod never serves stale config after a restart.
- **Same pipeline.** A change in the files is one import (validate, compile, diff, one revision) with channel `declarative`, actor System, and the source file's SHA-256 in the message.
  - An invalid file changes nothing. The last good revision keeps serving, the error is logged and audited, and it shows in the dashboard, in `/readyz` details and in a metric (`paduraksa_declarative_reconcile_ok`).
  - Readiness stays green: a bad commit to the repo must not take the proxy out of rotation.
- **Ownership.**
  - `Mode: Exclusive`: the files are the whole config (import mode `replace`), and the API, MCP and dashboard are read-only for config.
  - `Mode: Managed`: the files manage the objects they name. Those objects are marked `managedBy: declarative` and refused by the other channels with a clear error, while objects the files do not name stay editable. Removing an object from the files deletes it, but only if it is marked managed. The dashboard shows a lock and the source path on managed objects.
- **Secrets stay out of the files.** Credentials are referenced, never inlined: `secretFile: /run/secrets/cloudflare-token` (a mounted Kubernetes Secret or Docker secret).
  - The reconciler streams the file into the vault (`ISecretStore.ReplaceAsync`, keyed by a stable secret ID declared next to it) and re-imports it when it changes. The value never enters the revision document or the audit log.
  - Certificates managed elsewhere (cert-manager, Vault PKI) are referenced the same way (`certFile` and `keyFile` on a `kubernetes.io/tls` Secret mount) and imported through `ICertificateImport`, so the trust boundary holds.
  - The KEK comes from `PADURAKSA_MASTER_KEY_FILE` on a Secret mount.
- **Identity and setup.** An Owner can be declared, with a password hash or a `passwordFile`, plus roles for other users and API tokens whose secrets come from files. When an Owner is declared, the setup-token gate (see [dashboard.md](dashboard.md)) is skipped. Alternatively `Admin: Disabled` runs the proxy with no admin listener at all, only health and metrics, for fully hands-off clusters.
- **CI support.** `paduraksa config validate <path>` (offline: schema, references, compile; exit code and RFC 9457-style messages) and `paduraksa config diff <path> --against <url|data-dir>` show what a merge would change before it is applied.
- **Kubernetes shape.** Single replica, because SQLite and the vault are single-node: a StatefulSet (or a Deployment with `Recreate`) with a PVC for `/data`, the config as a ConfigMap and the credentials as Secrets.
  - With declarative mode the database becomes mostly a cache of the files plus state (ACME accounts and orders, the vault, audit), so losing the PVC costs reissued certificates, not config.
  - A Helm chart and an example manifest ship with the feature.
  - Multiple replicas are fleet mode (see [deployment.md](deployment.md)), where the declarative files feed the control plane.
- **Possible later step.** A Kubernetes **Gateway API / Ingress controller** mode that derives hosts and routes from cluster resources instead of files. It is a different product surface (watching the API server, RBAC, status writes), so it is not designed here.

