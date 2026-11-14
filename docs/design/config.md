# Config: typed IDs, data model and lifecycle

The config is one document. Every change goes through a command that validates it, compiles it into an immutable runtime snapshot, commits it as a new revision and publishes it. This page covers how IDs are built, what the config contains, how settings layer on each other, and how a change travels from a command to live traffic. The identity tables are in [identity.md](identity.md).

Contents:
- [Typed IDs](#typed-ids)
- [Data model](#data-model)
- [Upstream Host header modes](#upstream-host-header-modes)
- [Settings layers](#settings-layers)
- [Server settings](#server-settings)
- [Write path](#write-path)
- [The runtime snapshot](#the-runtime-snapshot)
- [State that survives a publish](#state-that-survives-a-publish)
- [Build decisions (M2)](#build-decisions-m2)
- [Startup, last-known-good and degraded starts](#startup-last-known-good-and-degraded-starts)
- [Application-layer cleanup after M7](#application-layer-cleanup-after-m7)

## Typed IDs

Every ID in the system is a typed struct, from the entity to the EF column, so a `HostId` can never be passed where a `UserId` is expected.

### Declaration

```csharp
[TypedId("host", IdKind.UuidV7)]   public readonly partial record struct HostId;
[TypedId("usr",  IdKind.UuidV7)]   public readonly partial record struct UserId;
[TypedId("chl",  IdKind.UuidV4)]   public readonly partial record struct PowChallengeId; // public-facing, no timestamp leak
[TypedId("aud",  IdKind.Int64Ordered)] public readonly partial record struct AuditEventId; // high-volume
```

For each declaration the generator emits:
- `Value` (`Guid` or `long`), `New()`, `Empty` and `IsEmpty`, and the `static abstract` contract `ITypedId<TSelf>`.
- `ToString()` (for example `host_0J9zb3xQk1Lr8m2V7aYc4D`), plus `ISpanFormattable`, `IUtf8SpanFormattable`, `ISpanParsable` and `IUtf8SpanParsable`. Parsing validates the prefix and allocates nothing.
- A `System.Text.Json` converter (registered in a source-gen `JsonSerializerContext`), a `TypeConverter` (Blazor routing and Minimal API binding) and a YamlDotNet converter.
- An EF Core `ValueConverter` and `ValueComparer`, applied by convention (`ConfigureConventions` calls `AddTypedIdConventions()`, which recognises `ITypedId<,>` member types during EF's property discovery), so no per-property config.
- An OpenAPI schema transformer: `{ type: string, pattern: "^host_[0-9A-Za-z]{22}$", example: ... }`.

### Representations

| Kind | Use for | String form | DB storage |
|---|---|---|---|
| `UuidV7` (default) | All config entities, users, tokens, revisions, certs, orders | `prefix_` + 22-char base62 | 16-byte **BLOB, big-endian**. Byte order equals time order, so indexes stay append-mostly. Never EF's default TEXT GUIDs. |
| `UuidV4` | IDs that are publicly visible where creation time must not leak (challenge IDs, share links) | `prefix_` + 22-char base62 | 16-byte BLOB |
| `Int64Ordered` | High-volume append-only tables (audit events, metric rollups, access-log index) | `prefix_` + 11-char base62 | `INTEGER PRIMARY KEY` (the SQLite rowid: clustered, fastest) |

**UuidV7 details.** IDs come from our own RFC 9562 `UuidV7Generator`. It uses a 12-bit per-millisecond counter, so IDs created within one millisecond still sort in creation order. `Guid.CreateVersion7()` fills those bits randomly and is banned. Bytes are written with `Guid.TryWriteBytes(span, bigEndian: true, out _)`.

**Int64Ordered layout.**
- 44-bit millisecond timestamp (epoch 2025-01-01 UTC), then a 19-bit monotonic counter seeded randomly at startup. The sign bit is always 0. Parsing rejects values above `long.MaxValue`.
- These IDs are **predictable, so they are internal and admin-only.** Never use one for anything exposed publicly.
- They are collision-free on a single node, provided the Data layer seeds the generator at startup with `Int64OrderedIdGenerator.Observe(MAX(id))`. That protects against a clock that is behind the last persisted ID.

**Base62.** The alphabet is `0-9A-Za-z` (ASCII order), fixed width and left-padded. Ordinal string sort therefore equals numeric sort, and sorting IDs as strings in the UI or API gives the same order as the database.

### Generator notes (M0)

- Generated `New()` uses the process-wide generators on `TimeProvider.System`.
  - Decided in M2: `Shared` stays there. `TimeProvider` is the clock abstraction, and code that needs a controllable clock (tests, the audit log, the revision store) creates its own generator on its own `TimeProvider`.
- Every source-generated `JsonSerializerContext` that serializes typed IDs must register `TypedIdJsonConverterFactory` through `[JsonSourceGenerationOptions(Converters = [typeof(TypedIdJsonConverterFactory)])]`. The STJ source generator cannot see the `[JsonConverter]` attribute our generator emits, and without the factory IDs serialize as `{}`.
- The generator is added as an analyzer to every project through `Directory.Build.props`. Prefix uniqueness is checked per compilation (PDKID004) and across all src assemblies by an architecture test.
- EF value converters cannot call static abstract members inside expression trees (CS8927). Generic converters go through a plain generic helper: `static T Wrap<T, TV>(TV v) => T.FromValue(v)`.

### EF mapping (`Paduraksa.Storage.Ids`)

- `configurationBuilder.AddTypedIdConventions()` swaps in a `PropertyDiscoveryConvention` that accepts typed IDs. They are found from the model's own CLR member types (no scanning, no type lists), in entities and complex types, as keys, FKs and nullables. Typed-ID properties are `ValueGenerated.Never`.
- **Inside JSON complex types, IDs are stored as their canonical string** (`host_...`), not BLOB or INTEGER. The document stays readable, and JSON members compare against a string parameter.
- ID lists (`List<RouteId>`) are primitive collections of canonical strings, as a column or inside JSON.
- Owned types mapped to JSON are rejected at model build. Use complex types.
- **Hard rule: a JSON ID (TEXT) never equals a column ID (BLOB or INTEGER) in SQL.** References inside JSON settings are resolved in memory by the compiler, never joined or compared in SQL.
- LINQ `==`, `!=`, `<`, `>`, `CompareTo`, `Contains` and `OrderBy` on IDs translate. `id.Value` does not, because EF cannot see through a converter.

### Secrets that look like IDs (but are not)

- **API tokens** are `pdk_<tokenId base62>_<32 random bytes base62>_<crc32 base62>`. The prefix and checksum let secret scanners (GitHub, gitleaks) detect leaked tokens. The database stores `tokenId` and `SHA-256(secret)`. A fast hash is fine for 256-bit entropy, and lookup by ID makes verification constant-time.
- **Session cookies** are opaque random 256-bit values; only the SHA-256 is stored. A session ID (`ses_`) is UuidV4: a handle shown in the session list that should not tell when the session began. The cookie value is the credential.
- Decided in M4: **no ASP.NET Data Protection around the cookie value.** It is already unguessable and stored hashed, so a key ring would add rotation to manage and no security. The Data Protection key ring in the vault arrives with antiforgery and Blazor in M5.

## Data model

Two database files, deliberately separate:

| File | Holds | Owned by |
|---|---|---|
| `data/paduraksa.db` | Config revisions, users, audit, metric rollups | `Paduraksa.Data` |
| `data/vault.db` | Encrypted key material and secrets | **Only** `Paduraksa.Vault`, through its own internal DbContext |

Both share the typed-ID conventions, SQLite pragmas and timestamp storage from `Paduraksa.Storage`. Keeping the vault apart means backing up or exporting config never sweeps up keys by accident.

SQLite settings: WAL, `synchronous=NORMAL`, `foreign_keys=ON`, `busy_timeout`. A single writer is fine because all writes go through the Application layer. Reads use `IDbContextFactory`, which Blazor Server also requires.

### The config document is the only copy

The config objects below are the content of the config document (see [Build decisions](#build-decisions-m2)). Through M7 they were also written to tables of their own on every commit, and nothing read those tables. The migration `DropConfigProjection` dropped them.

The one rule those tables enforced, a unique host name among enabled hosts in any case, is now the validator's (`host.name.duplicate`).

Certificates, ACME accounts and orders, DNS providers and the identity entities are still tables, because they hold state outside the document.

### Config entities

| Entity | Key fields |
|---|---|
| `GlobalSettings` (singleton row) | `Defaults: HostSettings` (JSON complex type), default-host behaviour, trusted proxies, admin settings, ACME defaults |
| `Profile` (`prof_`) | Named preset of `HostSettings` overrides, for example "Public app", "LAN only", "Media server" |
| `Host` (`host_`) | `Kind` (Proxy, Redirect, Respond, Admin), `Enabled`, `ProfileId?`, `Settings: HostSettings` (overrides), `CertificateBinding` (Auto, a specific `CertificateId`, or None), `Tags`, `Notes`, concurrency token |
| Host names | A host's `Names` list, no ID of their own (host names are natural keys, see the M2 build decisions), unique across hosts ignoring case. Normalised on write (see below). Originally planned as a `HostName` (`hn_`) table, which was never kept. |
| `Route` (`rte_`) | Host FK, `Match` (path exact or segment-aware prefix; the most specific wins), `Order` (tiebreaker), `UpstreamId`, per-route `Settings` overrides, transforms. Equivalent to NPM "custom locations". |
| `Upstream` (`ups_`) | Load-balancing policy, health checks, HTTP version, Host header mode, TLS-to-upstream trust, timeouts, affinity, connection and DNS refresh (see below) |
| `Destination` (`dst_`) | Upstream FK, address, weight, enabled |
| `AccessList` (`acl_`) | CIDR allow/deny, basic-auth users (a username and the ID of a password credential in the vault; no hash in the document) |
| `RuleSet` (`rs_`) / `Rule` (`rule_`) | Path, UA, header, method and country deny rules, ordered |
| `ErrorPageSet` (`eps_`) | Per-status templates (HTML), assets |
| `AuthProvider` (`authp_`) | ForwardAuth (authentik, Authelia and oauth2-proxy presets; built in M7), OIDC (later) |
| `Certificate` (`crt_`) | Source (Acme, Uploaded, SelfSigned, LocalCa), SANs, NotBefore/NotAfter, issuer, public chain (PEM), fingerprint, renewal state. **No private key.** |
| `AcmeAccount` (`acct_`) | Directory URL (LE, LE staging, ZeroSSL, Google or custom), contacts, EAB key id and **secret ref**, status, account URL, key algorithm |
| `AcmeOrder` (`ord_`) | One row per certificate: status, challenge type, names, CA order URL, staged-key token and CSR (resume state), last error, attempts, retry time, ARI window, next renewal |
| `DnsProvider` (`dnsp_`) | Kind (Cloudflare, deSEC, ...), label, credential `SecretRef` (`sec_`) into the vault. The hosts the token may reach are stored with the secret. |
| `NotificationChannel` (`ntf_`) | See below. |

**Host names** are normalised on write: trimmed, lowercased, IDN converted to punycode (`IdnMapping`), at most 253 characters with labels at most 63, a wildcard only as the leftmost `*.` label, no trailing dot.

**Notification channels.** An operational table like `DnsProvider`, not part of the config document.
- Columns: label, kind (ntfy, Gotify, Discord, webhook, email), enabled, subscribed events, the kind's non-secret settings, vault `SecretId`s for its secret and a webhook's signing key, and its delivery state (last sent, last error).
- Its deliveries live in the outbox (`NotificationEvents` and `NotificationDeliveries`, see "Durable delivery and history" in [hot-path.md](hot-path.md)).
- Events: upstream down and up (also per destination), expiry warning for every cert regardless of source (uploaded, self-signed, local CA leaves and the local CA root; default 30/14/7/1 days), ACME failure and recovery, sign-in lockout and new address.
- Health state and notifications were built in M9 (see [hot-path.md](hot-path.md)).

**Host kinds in v1:**
- **Proxy** (YARP).
- **Redirect** (precomputed target).
- **Respond** (fixed status and body from the compiled snapshot, for example 404, 410 or "coming soon").
- **Admin** (see [hosting.md](hosting.md)).
- **Static** (serving files from disk) is **cut from v1**: it needs path-traversal, symlink, MIME and caching semantics designed properly. When it comes back, the plan is a chroot-like root under `data/www/<hostId>`, symlinks disabled, `PhysicalFileProvider` with `ExclusionFilters.Sensitive`, ETag and Last-Modified, and precompressed `.br`/`.gz` siblings.

### Upstream settings

- **Host header** (as built, see below): `Original` (the default) sends the client's; `Destination` sends the destination's address; `Custom` sends a fixed name, for example for a hosted service that answers only to its own.
- **TLS to the upstream, trust** (as built):
  - `System` (the default) verifies against the system roots and the name sent.
  - `Pinned` accepts exactly the listed SHA-256 fingerprints, whatever their name, chain or dates. It is for self-signed homelab gear.
  - `Any` skips verification, with a compile warning.
  - Still planned: SNI apart from Host (decided against for now, see below), a custom CA, and an optional client cert `CertificateId` for mTLS.
- **SNI with Pinned.** SNI is the Host sent: the client's, unless the upstream's Host header mode says otherwise. An upstream with several names may present a certificate per name. The dashboard's fetch therefore asks each destination with its own name and with every name of the hosts that route to it, and offers to pin every distinct fingerprint. A refused handshake is logged with the fingerprint presented and the name asked for.
- **Connection and DNS refresh.**
  - `PooledConnectionLifetime` defaults to 60 s, so a restarted container with a new IP is picked up quickly.
  - The connect timeout (10 s) and the idle timeout (1 hour, "never" allowed) are per upstream settings, built; see "Upstream timeouts" in [hot-path.md](hot-path.md). `PooledConnectionIdleTimeout` is still planned.
  - A destination that fails to connect is passively marked unhealthy, which forces new connections (and so a fresh DNS lookup) on retry.

## Upstream Host header modes

Built in the host-header-acme branch.

**The setting.** `UpstreamConfig.HostHeader` is `{mode, value}`.
- `original` is the default and the absence of the key. Canonical JSON writes none, so stored revisions keep their bytes.
- `destination`, or `custom` with a `value`.
- A custom value is a host name or IP literal normalised as host names are everywhere else (trimmed, lowercased, IDN to punycode, no wildcard, IPv6 in brackets), then an optional `:port` from 1 to 65535. It is exactly the string the proxy sends.
- Validator codes: `upstream.hostHeader.value.required` (custom without a value), `.unused` (a value under another mode) and `.invalid` (not a name, a bad port, or an IPv6 literal without brackets).
- The compiler checks a custom name again as a backstop (printable ASCII with no space, `/`, `\`, `?`, `#`, `@` or `%`), so a hand-built model cannot put anything but a name on the wire.
- The enum is shared like the others (`UpstreamHostHeaderMode` in Core, both JSON contexts, `SharedEnumWireTests`). YAML and the admin API need nothing of their own: the key flows from the contract (`hostHeader:` with `mode` and `value`), and the round trip is covered by a plain test and by `ConfigSamples`.

**Where it applies.**
- The compiler gives each `CompiledRoute` its upstream's `UpstreamHost` (mode and name), one shared instance per upstream. The request transform (`UpstreamHostTransform`) reads a precomputed string off the route and writes the one header the old `AddOriginalHost(true)` also wrote. Nothing is formatted per request.
- `UpstreamHostAllocationTests` pin the cost:
  - choosing the Host allocates 0 B in every mode;
  - writing a `Host` into `HttpRequestHeaders` allocates inside the framework, whichever name it carries;
  - a request now clears the header (`AddOriginalHost(false)`) and then writes it once;
  - `ARequestPaysForOneHostWriteAsItDidBefore` measures that whole sequence, `ApplyAsync` included, against the single write `AddOriginalHost(true)` made. In `Original` and `Custom` mode it allocates no more.
- `Destination` sends no Host, so the HTTP client sends the destination's own authority: its host (punycode for an IDN) and its port unless it is the scheme's default. An upstream with several destinations sends each its own.
- The admin hop's route is always `Original`.
- A custom name is also put in the cluster's metadata (`paduraksa.upstream-host`, beside the pins), which the active health probe reads.
- **Pitfall found while building it.** When the builder has no host transform, YARP adds one of its own at the end of the chain that drops a `Host` equal to the client's. A custom name equal to the client's Host vanished, and the destination's address went out. The builder therefore registers `AddOriginalHost(false)`: its transform clears the header, and its presence stops YARP adding the default. Ours sets the header afterwards.

**SNI follows the Host sent; there is no setting of its own.** Decided.
- The forwarder's `SocketsHttpHandler` takes the TLS server name, and the name its certificate is checked against, from the request's `Host`. `Original` sends the client's name (as before), `Destination` the destination's host, `Custom` its name without the port. An IP literal sends no SNI.
- A second knob ("SNI apart from Host") would need the handler's connect callback and a per-cluster client, for a case nobody has asked for. `Pinned` trust is already independent of the name. It stays planned.
- The dashboard's certificate fetch for pinning asks for the same names (`UpstreamEditModel.SniNames`): `Original` the address's own name plus every host name routing here, `Destination` the address's own name only, `Custom` its name only.

**Active health probes.**
- With `Custom`, `UpstreamHostProbes` (a wrapper of YARP's `IProbingRequestFactory`) sends the name on every probe. An upstream that answers only to its own name would otherwise fail them all and be taken out.
- With `Original` and `Destination` the probe goes with the destination's address as before, because a probe has no client whose Host could be copied.

**Simulator.** The Upstream step carries `hostHeaderMode` and `hostSent` facts and says "forwarded to upstream X with Host: Y". For `Destination` it lists the distinct destination hosts. `UpstreamHostHeaderTests.TheSimulatorReportsTheHostTheUpstreamReceived` compares it with what a real upstream received, in every mode.

**Dashboard.** The upstream drawer's Advanced panel has "Host header sent to the upstream" (the client's, the destination's address, a fixed name) and, for the last, the name. One line of help says SNI and the certificate check follow it.

**Tests.**
- Proxy: `UpstreamHostHeaderTests` covers:
  - an upstream that echoes Host and SNI, in every mode, with the client on HTTP/1.1 and on h2c, plain and TLS;
  - a republish of the same route switching mode, and the simulator's fact;
  - two Host names to one HTTPS destination opening a TLS connection each, and no SNI to an IP destination;
  - a custom Host equal to the destination's or the client's still being sent.
- Proxy: `UpstreamHostAllocationTests` covers the 0 B choice, the transform against the old single write, the probe's Host, and a setup with no factory to wrap failing at startup. `SnapshotCompilerTests` cover routes, metadata, backstop errors and the destination's own Host by address.
- Unit: validator, canonical form, mapping, YAML. Dashboard: model and drawer (bUnit).

**Left out.** A Host per route or per host (the upstream is the unit; two routes that need different Hosts point at two upstreams). `ConfigGen`, the owner's property generator, does not generate the new key yet.

## Settings layers

`HostSettings` is one record whose every field is nullable. Null (absent in JSON) means **inherit**. A layer is stored as an EF Core 10 JSON complex type, so a new setting needs no schema migration.

The layers, from the bottom: built-in defaults, global defaults, profile, host, and on top of the host a route's own subset. A route carries only the route-level subset of settings.

One merge owns the layers for every consumer: `HostSettingsLayers` in `Paduraksa.Application`.
- `Merge(lower, upper)` lays one layer over another and keeps the result a layer.
- `Resolve(layers)` puts `BuiltIn` underneath and yields the concrete values the compiler and the validator read.
- The dashboard's fields merge `BuiltIn` under what they inherit the same way.
- `BuiltIn` is the one place a built-in default is stated.
- IDs inside `HostSettings` (JSON, stored as strings) are resolved by the compiler in memory, never by SQL joins or comparisons against ID columns.

The dashboard shows where each value comes from ("inherited from Global" or "from profile *LAN only*"), with a one-click "override here" or "reset to inherited". It goes through the same merge, so a form cannot promise a value the proxy would not apply.

**Nested records** merge field by field. They are `Tls`, `Hsts`, `ServerHeader`, `ProofOfWork`, `RateLimit`, `Robots`, `Cloudflare`, `AccessList`, `RequestLimits`, `RuleSet`, `Headers` and `Compression`. `Headers` merges rule by rule, keyed on the header name (see "Custom headers" in [hot-path.md](hot-path.md)).
- A layer lifts an inherited rate limit, access list or rule set with `enabled: false`. Everything else is a plain override.
- A nested record that sets none of its fields is no record at all. Canonical JSON writes no key for it, so a stored revision keeps its bytes (and its hash). `ISettingsLayer.SetsNothing`, which each record states, is the one rule that decides it.

The `HostSettings` fields are: force HTTPS, TLS (minimum version, TCP protocols, HTTP/3), HSTS, access log, request ID propagation, the `Server` header, proof of work, rate limit, `robots.txt`, Cloudflare (origin lockdown, Authenticated Origin Pulls), access list, request limits, rule set, auth provider, error pages, maintenance mode, custom request and response headers, and compression. Path rewriting (strip and add a prefix) is a route's own, `RouteConfig.Rewrite`, not a layered setting (see "Path rewrite" in [hot-path.md](hot-path.md)); `X-Forwarded-*` is not a setting either, the proxy always writes it (see [hot-path.md](hot-path.md#e-yarp)).

### Adding a host setting

This is a checklist, and nothing else.

1. **The property** on `HostSettings` (or on a nested `TlsSettings`, `HstsSettings` or `ServerHeaderSettings` record), at the end, so existing documents keep their key order.
2. **Its merge line** in `HostSettingsLayers.Merge` (or in the nested record's own merge), and its value in the `Concrete` mapping, where the resolved contract value becomes the proxy's.
3. **Its built-in default** as a `const` of `HostSettingsLayers`, from which `BuiltIn` is assembled. Nowhere else states one.
4. **A row in `SettingsInheritanceTests`**, the table that walks the contract: the field accessor, a layer that sets it, what the compiled host then carries, the built-in default and two sample values.
   - The table's own completeness test fails until the row exists.
   - The merge itself is also walked from the contract by `SettingsMergeReflectionTests`, so a field missing from `Merge` fails without a row. The table covers what each layer resolves to end to end.
   - A field added to a **nested** record also has to be added to `ISettingsLayer.SetsNothing` (or `EveryNestedRecordKnowsWhenItSetsNothing` fails) and to the record's line in the nested-merge cases (or `EveryNestedRecordHasANestedMergeCase` fails).
5. **Its rules in `ConfigValidator`**: field rules at the layer, and resolved-value rules on the host's resolved settings with the layer named by `HostSettingsLayers.LayerOf`. Add the compiler's backstop for the same value.
6. **If it sends a header:** its operation in `CompiledHost.ResponseHeaders` (the kind, stage and conditions in `ResponseHeaders`, and its line in `SnapshotCompiler.CompileResponseHeaders`).
   - Add its value or absence in every row of `ResponseHeaderMatrixTests`. The rows carry the expected header set, so the new header becomes a column each row must state.
   - Add one end-to-end test through `PaduraksaHost` if nothing else covers it on the wire.
7. **A new enum** that the document and the proxy share goes in `Paduraksa.Core.Model` with `[JsonConverter(typeof(ConfigEnumConverter<T>))]`.
   - Register it in **both** `ConfigJsonContext` and `AdminJsonContext`, because the source generator cannot see a converter attribute in another assembly.
   - `SharedEnumWireTests` fails until its row in the shared list and both registrations exist.
8. **The dashboard field:** one row in `HostSettingsFields.razor` (with its title, help, `data-field` and error path), its phrase in `SettingsSummary`, and its row in `HostSettingsFieldsTests`. The tests' completeness check fails until the row exists.

For comparison, the header refactor left adding a header setting at six files: the contract property, the merge, the compiled operation (`ResponseHeaders` and `CompileResponseHeaders`), the matrix rows, the end-to-end test and these notes.

### Adding a field to the settings UI

1. One `InheritSelect<T>` row in `HostSettingsFields.razor`, or the field's own small component (as `HstsMaxAgeField` is for the setting that keeps state of its own). It gives the title and help, the label and `data-field`, the path for errors, the inherited value and the options.
2. Its summary phrase in `SettingsSummary.Phrases`, in the contract's field order.
3. Its row in `HostSettingsFieldsTests`, which the completeness test walks the contract for: how the component sets it, the record that leaves, how it clears, and the words of its inherit choice. A new setting cannot be forgotten in the UI.

Nothing else in the dashboard names a field. The form binds to the contract's record, and the merge and the built-in defaults are the backend's (`HostSettingsLayers`).

### HSTS rules

- Built-in defaults: off. Once on, `max-age` is a year (31536000), with no subdomains and no preload.
- `MaxAge` is an integer in seconds, because that is the header's unit.
- The validator checks `max-age` from 0 (tell a browser to forget the host) to 63072000 (two years) per layer that sets it.
- `preload` is allowed only with `includeSubDomains` and at least a year. It is judged on each host's resolved settings, the way the certificate rule is:
  - a profile may leave `includeSubDomains` to the hosts that use it;
  - a layer no host uses is never refused;
  - the error is reported at the host (naming the layer its `preload` came from), because the host is the one that would send the header.
- Off, the preload pair is inert, since nothing is sent.
- A request for a name no host serves never carries HSTS, whatever the global defaults say. The proxy does not pin a name it does not serve.

### `Server` header rules

`ServerHeader` is the last field of `HostSettings`, after `AccessLog` and `RequestIdPropagation`, so the existing key order and hashes stay put.

| Mode | Effect |
|---|---|
| `Paduraksa` (built-in default) | Sends the name, never a version, replacing the upstream's header. |
| `Custom` | Sends its value, also replacing the upstream's. |
| `Upstream` | The upstream's own passes through. A gate answer has none. |
| `Remove` | Nothing at all, the upstream's included. HTTP/2 trailers are not touched. |

- A custom value is 1 to 128 visible ASCII characters or spaces, with no control characters and no leading or trailing space.
- It is required once the resolved mode is `Custom` (`serverHeader.value.required`) and checked as a value (`serverHeader.value.invalid`). The shape is checked where it is set and the resolved pair at the host, like HSTS's preload pair. An inherited bad value names the profile or global defaults it came from.
- Unknown hosts follow the global defaults' resolved mode. An admin host through the proxy uses its own like any host.

## Server settings

Values that tune the server itself (retention, the admin sign-in policy, notification timing, the integrations, the public connection limits) used to live only in the environment. Since the `server-settings` branch most of them are also in the config, under `settings.server`, and the dashboard edits them on Settings, Server.

### Where they live, and why the config document

- **`GlobalSettings.Server`** (`ServerSettings`), a flat record of nullable fields; null is the default. Canonical JSON writes no `server` key while it sets nothing, so stored revisions keep their bytes and hashes.
- **Why the document, not a table.** It gets the review drawer (Apply now or Add to draft), the audit event, History and Undo by rollback, YAML export and import, and MCP's draft-first writes, all for free. A table would have needed each of those again.
- **One version with the global defaults.** A change is an `UpdateSettingsCommand`, so editing the server settings and the global defaults at the same time is a conflict, like two edits of a host. That is rare enough on one node to accept.

### One declaration per setting

`ServerSettingCatalog` (Contracts) declares each setting once:
- its property, its configuration key under `Paduraksa`, its group and heading on the page;
- whether it applies live or after a restart, its title and help;
- its default and bounds, and optional review notes.

Everything else walks the catalogue:
- the validator: each value against its bounds (`server.setting.invalid`), then `ServerSettingCatalog.Rules` between settings over the values in force (`server.settings.conflict`): an idle timeout no longer than the session lifetime, and a data rate's grace over a second;
- the server's options validator, which holds the environment's values to the same bounds;
- environment detection, the API view, the page's fields, the review notes and the user guide's table, which `ServerSettingsGuideTests` regenerates and checks.

### Precedence

- **An explicit configuration value wins.** `ServerSettingEnvironment` asks the configuration providers whether a key has a value (appsettings, an environment variable, a test's in-memory source), never whether it differs from the default. A value equal to the default is still the environment's. The shipped `appsettings.json` no longer sets the log retention keys, so they don't lock the dashboard out.
- **Then the config, then the default.** `ServerSettingsState` resolves the three into the values in force and the source of each.
- **A mix the validator can't see.** The validator only sees the stored values. If an environment value breaks a rule with a stored one (an environment session lifetime shorter than a stored idle timeout), the state settles it (`ServerSettingsState.Settle`):
  - every stored setting the rule involves (`ServerSettingRule.Involves`) falls back to its default, both sides if both are stored;
  - then the rules are checked again, since a default can break another, until a pass drops nothing (at most one pass per setting);
  - a rule still broken with nothing stored in it is the environment against a default, which the options validator refuses at startup, so a running server never has one.
- **An environment-locked value is still stored.** The API accepts it and the page shows it read-only, so removing the variable makes the stored value apply at the next start.

### Taking effect

- **Following the queue.** The compiler copies the document's server settings onto `ConfigCompilation.Server`. `ConfigPublishing` raises them when a snapshot is queued, not when YARP confirms it. The revision is committed by then, and the startup values must be known before the public app binds, which is before the startup publish can complete.
- **The consumers' live options.** `ServerSettingsBindings` (Server) hands the values in force to each consumer's `LiveOptions<T>` (Core). A consumer reads `Current` when it acts, never a copy taken at construction.
  - Live: the rollup and analytics prunes, the notification dispatcher and health watcher, the identity services, the access-log drain and tail, the Cloudflare refresher and ACME issuance.
  - The log files take `Retain`, which also queues a trim at once.
  - The admin app reconfigures the sign-in throttle and switches MCP. The public app sets Kestrel's per-connection limits.
- **Restart only.** Kestrel reads the TLS handshake timeout and the connection caps when the listeners bind. `ConfigStartup` seals the values once the startup snapshot is queued (`ServerSettingsState.Startup`). The public app's Kestrel callback runs after that, so a stored cap applies at the next start, and the page marks a stored value the process doesn't run with as *Restart pending*.
- **Sessions.** A session is also checked against the lifetime in force from its creation, so a shortened lifetime ends existing sessions at their next check. A lengthened one never extends a session already issued. A completed session counts from the partial session's creation, so a shortening may end it up to the partial lifetime early: the safe side.
- **Touch interval.** It stays an environment setting. If a stored idle timeout is shorter than it, the bindings write the last-seen time at half the idle timeout, so live sessions don't idle out.

### Who may change them

- **`server:write`** (`Permission.ServerWrite`) belongs to the Owner and Admin roles, never the Operator.
- **Checked on what the plan does to the document,** not on the kind of command. `ConfigPipeline` requires it whenever `settings.server` differs between the head and the planned document. That covers `PUT /server-settings`, `PUT /settings`, an import, a rollback and a draft's entry alike.
- **Simulations aren't checked.** A simulation never commits.
- **Staging in a draft isn't checked either.** Adding an entry to a draft is a check of the entry, not a commit, so an Operator can stage a server-settings change. The draft's apply runs the pipeline as the person applying it, and that is where it is refused. This was kept as it is, on review: a draft holds nothing until it is applied.

### Pointing at a setting

- **Anchors.** Each setting's row on the page has the id `ServerSettingDefinition.Anchor` (the key in kebab-case), so `/settings/server#mcp-enabled` scrolls to it, focuses it and highlights it (dashboard.md, "Linking to a field"). A locked setting keeps its anchor: the link lands on the note that names the variable.
- **Hints use the link, not the variable.** A dashboard hint for a setting in the catalogue links to it with `SettingsRoutes.SettingLink`. The API tokens page's "MCP is off" says to turn on *Serve /mcp* in the server settings. Someone without `server:write` is told that an Owner or Admin can, and still gets the link, since the page shows them the value read-only. When the environment keeps it off, the hint says so, names the variable and links to the row.
- **What the sweep found.** `McpConnect` was the only hint in the dashboard that told a person to set a variable. The other settings are never mentioned by a hint.
- **Kept:** the server's own error and warning texts name settings that are not in the catalogue (`Paduraksa:Listen:AdminAllowedSources` in the forwarded-headers refusal, `Paduraksa:Listen:AdminInternal` in the compiler's warning). The OpenAPI description of the notification history names `Paduraksa:Notifications:HistoryRetention`: left alone, to keep the API's description stable.

### Adding a server setting

1. The property at the end of `ServerSettings`.
2. Its declaration in `ServerSettingCatalog` (key, configuration key, group and heading, live or restart, words, default, bounds, notes), and its place in `All`. `ServerSettingCatalogTests` fails for a property without one.
3. Where its consumer takes it: a line in `ServerSettingsBindings.Apply` (or the admin or public app's follower) onto the consumer's `LiveOptions<T>`. A consumer that reads it once is declared `Restart`.
4. The options validator's bound for the environment's value, from the declaration (`Bound(ServerSettingCatalog.X, ...)`). The option's default must be the declaration's: `ServerSettingOptionsTests` walks the configuration keys.
5. Regenerate `openapi.v1.json` and the guide's table (`PADURAKSA_UPDATE_DOCS=1` on `ServerSettingsGuideTests`).

The page, the API view, the review notes and the precedence need nothing.

### Left out

- **What stays in the environment:** the data directory, the listeners, the development switches, the trust files, the log directory, the timeouts of startup and shutdown, PBKDF2's iterations, the write intervals, the ring and tail sizes, the ACME scheduler and the test hooks.
- **DNS-01's resolvers and timings.** Only the check's switch and the fixed wait moved; the resolvers stay in the environment.
- **The storage-impact estimate and table sizes** on the page, which the brief called nice to have.
- **The CLI.** `paduraksa user` and `backup` read the environment's sign-in policy, not the stored one.

## Write path

The write path is cold: it may allocate and may lock.

```
Dashboard / API / MCP / CLI / YAML import
        |  (Contracts DTO + baseRevision)
        v
Application command handler
  1. authorize (role/scope)                     -> 403 + audit
  2. load current config, apply mutation
  3. validate (field rules + cross-object: dup hostnames, dangling refs, cert coverage, regex compile w/ timeout)
  4. COMPILE a candidate RuntimeSnapshot in memory   <- fails here, nothing is persisted
  5. BEGIN TX: write entities, ConfigRevision(doc, diff), AuditEvent; check concurrency token; COMMIT
  6. PUBLISH (single writer, coalescing to the latest candidate):
     a. swap the YARP provider config, cancel the old change token
     b. await YARP ConfigurationApplied / ConfigurationApplyingFailed (matched by RevisionId, timeout -> alert)
     c. capture YARP's new Endpoint objects into the candidate's slot -> Endpoint table
     d. SnapshotStore swap                       <- atomic reference swap; requests see it from here on
  7. notify: change feed (dashboard live update)
```

Step 5 as built: the config objects have no tables of their own (see "The config document is the only copy"), so the commit writes the revision (the whole document) and its audit event, nothing else.

- **Optimistic concurrency.** Each top-level object has a version, and commands carry the version they edited (or `baseRevision` for whole-document commands). Two admins editing the same host get a conflict dialog with a diff instead of a silent overwrite.
- **Dry run.** Every command supports `dryRun: true`, which runs steps 1 to 4 and returns the diff plus validation results. The dashboard's "Review changes" sheet, MCP's `execute` (a write without `confirm` is a dry run, see [admin-surfaces.md](admin-surfaces.md)) and `import --dry-run` all use it.
- **Rollback.** Load an old revision's document, run it through the *import* path (validate, compile) and commit it as a **new** revision ("Rollback to #37"). History is never rewritten.
- **Diff.** A structural diff over the canonical document (stable ordering by typed ID), rendered as an object-level change list plus a YAML side-by-side view.
- **YARP validation.** YARP's own `IConfigValidator` runs in step 4, so an invalid YARP config fails before commit. YARP otherwise swallows invalid configs: it logs and keeps the old one.
- **Startup recompiles the latest revision.** The database's latest committed revision is always the source of truth, and the process compiles it on every start.
  - A crash between step 5 (commit) and step 6 (publish) therefore costs nothing except that the change goes live on the next start.
  - In-process, a failed publish after a commit is reported to the command, logged as an error and audited, with no retry. The commit stands and the previous snapshot stays live. The next commit's publish or the next start makes the change live, since every snapshot is the whole config.
- **Last-known-good.** After each successful publish, the compiled snapshot's source document is also written to `data/last-good.json`. It is only a fallback. If the database is unreadable or migration fails at startup, the proxy still comes up serving traffic while the admin UI shows a degraded banner. Details are in [Startup, last-known-good and degraded starts](#startup-last-known-good-and-degraded-starts).

## The runtime snapshot

A `RuntimeSnapshot` is immutable, built once and read lock-free.

```csharp
public sealed class RuntimeSnapshot
{
    public required long Generation { get; init; }
    public required ConfigRevisionId Revision { get; init; }

    // Host resolution: exact names + wildcard suffixes, case-insensitive,
    // looked up via FrozenDictionary.GetAlternateLookup<ReadOnlySpan<char>>() -> no string slicing.
    public required HostTable Hosts { get; init; }

    public required CompiledHost[] HostsByIndex { get; init; }     // dense index used by metrics arrays
    public required CertificateTable Certificates { get; init; }   // SNI -> SslStreamCertificateContext (prebuilt chains)
    public required GlobalGate Global { get; init; }               // global IP deny trie, trusted proxies, conn limits
    public required IProxyConfig Yarp { get; init; }               // path-only routes + clusters, pure function of content
    public required Endpoint[] YarpEndpoints { get; init; }        // RouteSlot -> YARP Endpoint, filled at publish step 6c
    public required DefaultHostBehaviour Default { get; init; }
}

public sealed class CompiledHost
{
    public required int Index { get; init; }                       // position in THIS snapshot only
    public required HostId Id { get; init; }
    public required MetricSlot Metrics { get; init; }              // stable across generations
    public required IRequestPolicy[] Policies { get; init; }       // ordered, already specialised
    public required PrecomputedResponse? RobotsTxt { get; init; }  // pre-encoded UTF-8 with its headers; null = passthrough
    public required ErrorPages ErrorPages { get; init; }           // pre-rendered byte segments
    public required RedirectSpec? HttpsRedirect { get; init; }
    public required ResponseHeaderOperation[] ResponseHeaders { get; init; } // Alt-Svc, HSTS, Server, X-Request-Id
    // ...
}
```

`SnapshotStore` is just `volatile RuntimeSnapshot _current` plus `Publish` (`Interlocked.Exchange`, then fire the change token). Each request reads the reference **once** at the start and uses only that snapshot, so a request never sees half of one config and half of another.

### How YARP is integrated

Decided by spike S2 (`docs/spikes/S2-yarp-reload.md`): **the gate picks the YARP endpoint itself, and ASP.NET routing never matches hosts.**

- **IDs.** YARP route, cluster and destination IDs are stable typed-ID strings with no generation or revision in them. The YARP config is a **pure function of content**, so an edit that touches only policy is a YARP no-op and costs next to nothing.
- **Routes carry no Host match.** Each YARP route is path-only (or a catch-all per cluster). It exists only to give YARP an endpoint with the right cluster and transforms. Host-header matching was about 90% of the reload cost (500 hosts: 15 ms down to 1.9 ms; 2000 hosts: 64 ms down to 11 ms).
- **The gate resolves host and route** from its snapshot `S`, using a per-host route list ordered most specific first. It takes the policies **and** the YARP `Endpoint` from the same `S`, then calls `HttpContext.SetEndpoint(...)` *before* `UseRouting`, so endpoint routing skips matching.
  - A request therefore cannot mix generations. There is no version check, no snapshot ring and no 503 path.
  - It costs 0 B in the gate. S2 measured gate entry to proxy pipeline at 152 B per request, against 256 B with Host matching.
- **Publish order** (step 6 above). YARP applies first, then the endpoint table is captured from `EndpointDataSource` (keyed by route ID), then the snapshot is swapped. Until the swap, requests keep using the old `S` with its old `Endpoint` objects. S2 showed those keep proxying correctly even after YARP removed or replaced them, following YARP's live cluster state, so in-flight requests are fine.
- **Custom pipeline.** We use `MapReverseProxy(Action<IReverseProxyApplicationBuilder>)`, which replaces YARP's default pipeline. **Session affinity, load balancing and passive health must be added back explicitly** (`UseSessionAffinity()`, `UseLoadBalancing()`, `UsePassiveHealthChecks()`). Without them, passive health silently does nothing.
- **Client reuse.** A custom `IForwarderHttpClientFactory` must reuse the existing client when cluster config is unchanged. A new `HttpClient` counts as a cluster change and rebuilds every route on it.
- `ConfigureEndpoints` metadata is **not** used. S2 showed YARP re-runs it only for new or changed routes, so unchanged routes would keep stale metadata.

### As built in M1 (`src/Paduraksa.Proxy`: `Snapshot/`, `Gate/`, `Yarp/`, `Config/`)

**Types (M2).**
- The config model and the snapshot are keyed by typed IDs. `Revision` is a `ConfigRevisionId` (`Empty` when there is no committed revision: the bootstrap snapshot, the empty startup config). `CompiledHost.Id` and `CompiledRoute.Id` are `HostId` and `RouteId`. Upstreams, destinations and certificate bindings use `UpstreamId`, `DestinationId` and `CertificateId`.
- YARP's route, cluster and destination IDs are their canonical strings (`rte_...`, `ups_...`, `dst_...`), formatted by the compiler. Nothing on the request path formats an ID.
- YARP compares IDs ignoring case, so the compiler rejects route, upstream and destination IDs that differ only in case.
- `RuntimeSnapshot.Yarp` holds the YARP *content* (`YarpConfig`). The `IProxyConfig` with its change token and a unique `RevisionId` is created per publish by the provider.
- `Certificates` (`CertificateTable`) and `TrustedProxies` (`TrustedProxyPolicy`) are typed slots the compiler fills. `SnapshotStore` is the TLS selector's `ICertificateTableSource`, so a publish hot-swaps certificates for new handshakes.

**Unbound, then bound.** The compiler produces a candidate with generation 0 and no endpoints. The publisher `Bind`s it to YARP's endpoints and the next generation. The store starts at a bootstrap snapshot (generation 0, 503 for everything), so `Current` is never null.

**Routes.** Matching is exact or segment-aware prefix, in the evaluation order described below. The rule is **match leniently, forward unchanged.**
- Matching (and later path rules) uses a canonical *matching view* of the decoded path:
  - `\`, `%2F` and `%5C` separate segments, and repeated `/` collapse;
  - `;...` segment parameters are dropped;
  - trailing `.` and spaces are trimmed per segment;
  - dot segments resolve, and case is ignored.
- Many upstreams read those spellings as the same path, so `//admin`, `/admin;x`, `/admin.` and `/ADMIN` must hit the policies of `/admin`. Over-matching only makes a policy stricter.
- The view costs 0 B: as is when already canonical, else on the stack, or a pooled buffer past 256 characters. Kestrel's request-line limit bounds it.
- The compiler rejects route paths that are percent-encoded or not canonical.
- Routes carry **only their own** policies.
- YARP routes are `/.paduraksa/r/{routeId}/{**rest}`, and the proxy pipeline rejects (404) any request whose endpoint the gate did not set, so they cannot be reached directly.

**Route order (after M1).** A host tries exact routes before any prefix, then the longest prefix, then `Order`, then the listed order. The first match wins.
- The compiled snapshot fixes this order once, so request matching stays a scan.
- A catch-all listed first used to shadow every route after it silently, because the listed order decided. Now the listed order decides only between routes of equal kind, path length and `Order`.
- The canonical config document and the dashboard list routes in this evaluation order. YARP's internal routes carry no order, because the gate sets their endpoint.

**Forwarding.** The upstream gets the path exactly as received. A YARP request transform builds the URI from the raw request target (path part) plus the query, without re-escaping. YARP's default would turn `%2F` into `%252F`. M1 strips no prefixes.

**Publisher.** It is a single writer.
- A queued candidate is completed as `Superseded` when a newer one arrives.
- YARP's `IConfigValidator` plus a route-to-cluster check run first (`Invalid`).
- Content equal to what YARP runs skips the YARP round trip (policy-only edits).
- `ConfigurationApplyingFailed` gives `Rejected`, and the previous YARP config is re-applied so provider and YARP agree. No outcome within `ApplyTimeout` (10 s) gives `TimedOut`. On any failure the old snapshot stays live.
- **Before YARP exists** (the core host publishes first), an empty-YARP candidate publishes at once, and any other waits for `MapPaduraksaProxy`, which validates it and hands it to YARP's initial load.
- Startup code therefore **never awaits** a publish. The core host's start returns before the public app starts, so it queues the publish and `/readyz` (`HasPublished`) reports when it is live.
- Disposing the publisher (shared-services disposal) stops the writer and fails anything queued or in flight.

**Fixed answers** (Respond hosts, policy responses) with status 204, 205 or 304 must have an empty body (a compile error otherwise) and are sent without `Content-Length`.

**Default host.** 404 (default), 421, or close (abort: H1 closes the connection, H2 and H3 reset the stream). The HTTP-to-HTTPS redirect is 301 for GET/HEAD and 308 otherwise. Its `Location` (request host + raw target + public port) is the one string a gate-answered response allocates, built after the decision.

## State that survives a publish

Snapshots are immutable and thrown away on every publish, so **mutable runtime state must never live inside one**. Otherwise every config edit would silently reset rate limits and break metric series.

Long-lived state sits in process-lifetime singletons (`RuntimeState`). Snapshots hold only *keys* into it, and those keys derive from **stable identities**, never from array positions.

| State | Lives in | Keyed by | On publish |
|---|---|---|---|
| Rate-limit buckets | One global fixed-size bucket table (see [hot-path.md](hot-path.md)) | A long: a **16-bit policy slot** (stable, from an append-only `RateLimitPolicyId` to slot registry, like metric slots) plus a **48-bit hash** of the partition key (IP/64, IP+path, header) | Carried over untouched. If a policy's rate or burst changed, the new parameters apply on each bucket's next touch (tokens clamped to the new burst). Deleted policies' buckets are cleared by the sweeper (an unknown slot means clear). |
| Per-IP connection counts | `ConnectionRuntime` (one `ConnectionTable`), a process singleton | Client address (IPv4 whole, IPv6 /64) | Not snapshot-related at all. Only the *limit* and the deny list come from the snapshot (`RuntimeSnapshot.Connections`). A lowered limit applies to the next connection, and what is already open stays counted (see "Connection stage" in [hot-path.md](hot-path.md)). |
| Forward-auth decision cache | `ForwardAuthRuntime`, a process singleton | `AuthProviderId` plus the provider's **config hash** (built in M7: a hash of everything but the name) | Kept if the provider's effective config is unchanged, otherwise dropped. A changed auth config must never reuse old decisions. A snapshot's states become the live ones when it is published, not when it is compiled. |
| Basic-auth verify cache | The vault (`IPasswordVerifier`) | Per-process key, the credential's stamp (changes with its salt), masked client IP, and the user name and password as sent | Nothing to carry over. A changed or deleted password makes every earlier entry miss, and a publish touches nothing. |
| Metrics counters and histograms | `MetricsRegistry` | **`MetricSlot`**, allocated per typed ID (`HostId`, `RouteId`, `UpstreamId`, `DestinationId`) by an append-only slot registry | The compiler asks the registry for each ID's slot, so the same host keeps the same slot forever. Slots of deleted objects are freed after a grace period (one rollup interval). Built in M9 (see [hot-path.md](hot-path.md)). |
| Persisted metric rollups | SQLite (`MetricRollups`) | **Typed ID** plus minute, never slot or index | Not applicable: a deleted object's rows stay until retention removes them. The sampler flushes a slot's last minute under the old owner's ID before the slot is reused. |
| YARP health state | YARP (per destination) | ClusterId + DestinationId (typed-ID strings) | See below. |
| ACME HTTP-01 tokens | `IAcmeChallengeStore` | Token | Independent of config snapshots. |
| Cloudflare edge ranges | `CloudflareEdgeRangeSource` (process) and `<data dir>/cloudflare-ranges.json` | - | The compiler reads the current list at every compile, and the snapshot keeps the one it was compiled with. A refresh that changes the list has the live config compiled and published again, with no revision (see [hot-path.md](hot-path.md)). |

**YARP health state** (verified in S2) survives every reload that keeps the IDs, including changes to the cluster's own policies. Pitfalls:
- Removing and re-adding a destination resets it to *Unknown*, which YARP treats as routable.
- Changing a destination's address under the same ID keeps the old verdict.
- So rollback and import must keep destination IDs.
- Optionally, key destinations as `{DestinationId}~{hash8(Address, Host, Health)}` so an address change gets fresh state. This fixes only a stuck passive-unhealthy verdict, since the new key starts as Unknown, and it resets affinity for that destination.

**Continuity tests.** Spike S6 turns this table into a test: N generations are published under load, and it asserts that rate-limit decisions, counter totals and YARP destination health are all continuous.
- M1 covers YARP health and request errors (`StateContinuityTests`).
- M7's `RateLimitContinuityTests` covers the rate-limit part: buckets carried over publishes under load, and a lowered burst applied on the next touch.
- `MetricsEndToEndTests` (M9) cover counter continuity: counters stay monotonic and on the same slots across a publish every 100 ms under load.

## Build decisions (M2)

These refine the data model and write path for the M2 build.

- **The config document is the source of truth.**
  - `ConfigDocument` (in `Paduraksa.Contracts`, `apiVersion: paduraksa.dev/v1`) holds the global settings, profiles, upstreams and hosts with their routes. It contains references only, never secrets.
  - Each revision stores it as canonical JSON (deterministic bytes), Brotli-compressed, with the SHA-256 of the uncompressed bytes.
  - Startup, rollback, diff and export all read documents.
- **No table mirrors the document.** Queries (dashboard lists, "which hosts use this upstream") read the latest revision's document. M2 also wrote the objects to tables in the revision's transaction. Nothing read them, and the cleanup after M7 dropped them.
- **Host names are natural keys.** Each is stored normalised, with no `hn_` ID.
- **Concurrency.**
  - Every top-level object (global settings, profile, upstream, host) carries a `Version` that the pipeline increments on change. A command names the version it edited, so two admins editing different hosts never conflict.
  - Whole-document commands (import, rollback) name the `baseRevision`, which must be the latest.
  - One writer at a time (`SemaphoreSlim`) in the Application layer.
- **Settings layering.** The compiler resolves the layers above into the concrete `ProxyConfigModel`.
  - M6 added two scalar switches on the same path: `AccessLog` (`true` built-in) and `RequestIdPropagation` (`upstreamAndResponse` built-in).
  - Both are absent from canonical JSON when unset, so a stored document that never mentions them keeps its bytes, and therefore its revision hash.
- **Errors carry a path.** Validation and compile errors carry a JSON-pointer path into the document (`/hosts/3/routes/0/path`), a stable code and a message, so every surface can point at the field.
- **Audit hash chain.** `hash = SHA-256(prevHash || canonical bytes of the event)`. The first event's `prevHash` is 32 zero bytes. A verifier walks the chain in ID order and reports the first break.

### The compiler (`src/Paduraksa.Application/Compilation`)

- **Mapping.** `ProxyConfigMapping.Map` is a pure function from the document's canonical form to `ProxyConfigModel`, so two spellings of one config compile alike.
  - `ConfigCompiler` (the `IConfigCompiler`) runs it and `SnapshotCompiler` with the process's `SnapshotCompilerOptions`.
  - The compiler is split by concern. `Compile` is the orchestration, the per-host step (the admin-host rules included) is `SnapshotCompiler.Hosts.cs`, and upstreams, TLS/certificates and response headers have their own partials, each filling a small `CompileContext`.
- **Layering.** Per host: built-in defaults, global defaults, its profile (a missing profile adds no layer; the validator rejects it with `host.profile.unknown`), then the host. Per route, its own subset goes on top of the host's result. A null field inherits. TLS merges field by field. `ForceHttps` is judged after layering. (M2 had an inline IP allow list that replaced as a whole. M7 replaced it with named access lists.)
- **Certificate binding** (M2; M3 resolves it against the inventory, see [vault-and-certificates.md](vault-and-certificates.md)).
  - Auto means no certificate of its own (the default one). Specific uses its `CertificateId`.
  - None sets `HostSpec.Https = false`: no certificate-table entries and no `Alt-Svc`, and the gate answers HTTPS requests for the host 421 (a connection on the default or another host's certificate cannot reach it).
  - None with an effective `ForceHttps` is an error (validator: `host.certificate.noneWithForceHttps`; the compiler at the host).
  - Disabled destinations are left out. An upstream may have none left until an enabled host routes to it.
- **Compile errors carry paths.** `SnapshotCompiler` attributes each error to a host, route, upstream, destination (by typed ID) or the defaults and trusted proxies.
  - `ConfigCompiler` maps that to a pointer into the document *as given* (`/hosts/{i}`, `/hosts/{i}/routes/{j}`, `/upstreams/{k}/destinations/{m}`, `/settings`, `/settings/trustedProxies`) with code `compile.failed`, each (path, message) once.
  - An inherited value is reported where it is used (each host or route whose chain carries it). A duplicated ID points at its last occurrence.
  - The mapping itself reports the few invalid values the model cannot carry: an undefined certificate binding mode or load-balancing policy, or a respond host without a content type.
  - A test runs sample, edge and generated documents through the validator and the compiler. Whatever validates must compile, certificate references aside, which only the compiler can resolve against the inventory.

### Policy chains: where access lists, rule sets and auth providers go

**Access lists: host chain and route chains** (M2's inline allow list, as M7 reworked it).
- The gate runs the host chain before the HTTPS redirect, fixed answers and route matching. So a route override cannot *replace* a list that sits there alone.
- When every route ends up with the same list (none overrides the host's, or all override it alike) and it has no users, or the host has no routes, that list goes in the host chain only. It guards everything the host answers.
- Otherwise each route carries its own effective list, and the host chain carries the **union** of the routes' address rules. It drops a request only if every route's list drops:
  - a client no route admits is blocked on every path, over plain HTTP too (no redirect, no 403-versus-404 to map the host by, and a dropping host still looks dead);
  - a client that some route admits is judged again by the route it hits.
- If some route's list cannot refuse by address (or a route has none), the host chain carries no union, since anyone may reach that route.
- A list with users puts only its address rules in the host chain and the whole list (passwords too) in each route's chain. A password is never asked for before the HTTPS redirect.
- A test drives the real gate over generated documents (scheme x path x client IP, lists with and without users) against a reference that reads the document directly.

**Rule sets: host chain and route chains** (M7). Same shape as the access list's rule, without the union.
- When every route resolves to one set, or the host has no routes, the set is in the host chain. It goes after the host chain's address-only check or union; for a host with no routes, before a full list that has users.
- Otherwise each route's own chain carries its set, in front of its list when that has users (the list's address checks included). A route's set replaces the host's, and a deny cannot be lifted by a union.

**Auth providers: route chains only** (M7).
- The provider is not a chain entry the mapping builds. It is a route policy the compiler adds after the rate limit and before proof of work. Reasons: a forward-auth call is the one expensive thing a request can cost outside the upstream, so a 429 should come first; and a cookie must never be sent to an endpoint over plain HTTP (the route chain runs after the HTTPS redirect).
- The mapping only resolves which provider each route has (`RouteSpec.AuthProvider`, with the route's own override marked).
- An admin, redirect or respond host ignores the setting with a warning (`authProvider.ignored`).

### Commands (`Paduraksa.Application.Commands.ConfigService`; commands and results in `Paduraksa.Contracts.Commands`)

- **Versions are revision numbers.** A revision gives every top-level object it changes (a route or destination change counts for its parent) its own `Seq` as the new version. Untouched objects keep theirs, whatever a command sends.
  - A version is therefore issued once, ever, and stays at most the latest `Seq`.
  - A rollback to #37 committed as #52 gives what it changes version 52. An editor holding any older version of those objects (including one from #37, or of an object deleted and restored) gets a conflict. Editors of objects the rollback leaves alone do not.
- **The empty config** is the default document with no revision. Startup writes nothing. The first command creates revision #1, and a whole-document command on an empty database names no base revision.
- **Errors.**
  - Errors of whole-document commands point into the document as sent (a rollback: as stored).
  - Object commands report the sent object's errors relative to it (`/names/1`), and errors the change causes elsewhere (a deleted upstream still in use) as related errors pointing into the canonical document after the change, like the change list.
  - The sent object is validated last in its list, so a clash with another object is reported on it.
- **Commands that change nothing** (equal canonical config) commit nothing and return `unchanged`.
- **Audit.**
  - Commits are audited in the commit's transaction.
  - Denied, invalid, conflict and not-found commands are audited in a transaction of their own. It is written without the caller's cancellation once decided, and a failed write is logged rather than replacing the result.
  - A publish failure after a commit is audited as `config.publish` (failed), also when it happens after the command stopped waiting.
  - Dry runs and no-ops are not audited.
- **Publishing.** Queued with the publisher under the writer lock, so snapshots go live in commit order. The command waits for the outcome outside the lock (bounded, then `pending`). A superseded publish reports the outcome of the newer publish that carries it. A failed publish is reported, logged and audited by the command whose publish it was. The commit stands, with no retry.
- **One writer.** This process is the only writer of the config database. The CLI writes identity and the vault, never config. A second server process on the same data directory is refused at startup (the file lock described in [vault-and-certificates.md](vault-and-certificates.md)). If one got past it (a file system that cannot lock), its commit would fail on the unique `Seq` and parent indexes of `ConfigRevisions` rather than fork history.

## Startup, last-known-good and degraded starts

Code: `ConfigService.StartAsync` and `Paduraksa.Application.LastKnownGood`; composition in `Server/Hosting/ConfigStore` and `ConfigStartup`.

**Composition.**
- The config database (`<data dir>/paduraksa.db`), its context factory, the compiler (`ConfigCompiler` on `PublicListenerSetup.CompilerOptions`), the `LastKnownGoodFile` and the one `ConfigService` are shared instances, registered into all three hosts.
- The factory and the service come from a small private container (EF's factory and the service are meant to be resolved), which `SharedServices` owns and which alone disposes them.
- Disposal runs in reverse creation order after every host has stopped:
  1. the container (the service refuses new commands);
  2. the publisher (stops its writer and fails whatever is queued, so nothing goes live after it);
  3. the last-known-good file, created before the publisher (waits for its pending write). The file therefore never trails the last revision that went live.
- Certificate bindings resolve against the certificate inventory. The placeholder certificate is its fallback default (see [hosting.md](hosting.md)).

**Startup recompile.** The core host's `ConfigStartup` creates the data directory and runs `StartAsync` under the writer lock:
1. migrate (`ConfigDatabase.InitializeAsync`);
2. read the latest revision (none means the empty config);
3. compile;
4. queue the publish **without awaiting it** (as in M1: YARP attaches only when the public app starts).

The outcome is logged when it arrives. A superseded startup publish (a commit's newer publish carries the config) is not a failure.

**File modes** (threat model G7).
- The config database is created 0600, and so are its `-wal` and `-shm` (SQLite gives them the main file's mode).
- `PrivateSqliteFile` (in Storage, shared with the vault's database) creates it. At every `ConfigDatabase.InitializeAsync` it takes group and other access away from an existing file and its WAL files and reports which it changed (a warning is logged once).
- The database holds the users' password hashes, the session and token hashes and the audit log, so it no longer depends on the data directory's mode or the process umask.
- The data directory itself keeps the mode its creator gave it (the image's 0750, a bind mount's).

**Last-known-good file (`last-good.json`).**
- Shape: `{format, revision, seq, sha256, document}`. The document is the revision's canonical bytes verbatim, so the hash is the stored `DocumentHash`. References only, no secrets.
- It is offered after every successful publish (the startup one and each commit's, before the command returns) and written in the background, never under the writer lock. The newest offer wins; older ones are dropped.
- Each write goes to a new temp file with a random name (`CreateNew`, mode 0600 on Unix; leftovers of a crash are removed first, a symlink as a link). It is flushed to disk and renamed over the target, which replaces a symlink there instead of following it.
- The directory is not fsynced (.NET cannot open a directory). That is safe but not durable: a power loss right after a write may leave the previous, still valid, file.
- A failed write is logged and never faults the writer.
- Reading uses one bounded read, refuses a symlink, checks format, IDs and hash, and never throws. A damaged file is logged as a warning (also on a healthy start) and replaced at the next publish.
- The empty config is not written: it is no revision.
- One data directory serves one server process at a time, enforced since M10 by a file lock (`run/server.lock`, see [vault-and-certificates.md](vault-and-certificates.md)).

**Restore.** A database without revisions next to a usable last-known-good file has lost its config (a deleted or replaced `paduraksa.db`).
- The file's document is committed as revision #1 through the normal replace path (actor System, message "Restored from last-known-good #N (rev_...)", audited), logged at Critical, and startup goes on normally.
- If that commit is rejected or fails, the start is degraded on the last-known-good config (on the empty config if that does not compile either), accepting commands.
- To start over with an empty config, delete both files.
- A file newer than a database that has revisions (a restored backup) is only a warning: the database wins.

**Degraded starts.** The last-known-good document is compiled and published instead, logged at Critical, and `ConfigService.Status` says so (`Source = LastKnownGood`, the problem, the revision served). This happens when:
- the database cannot be opened or migrated;
- its latest revision cannot be read (corrupt document, unreadable by this build, or columns a converter rejects: a damaged ID or timestamp);
- the latest revision does not compile.

If the store itself is unusable, commands get `Unavailable` (`config.store.unavailable`, unaudited, since the audit log lives in that store) until the database is fixed or restored and the process restarted.

If only the compile failed (say, after an upgrade made the compiler stricter), the database is fine. Commands are accepted and planned on the latest revision as always, and the first one that goes live ends the degraded state. Refusing them would leave no way to fix the config short of editing the database.

**A failed startup publish** (YARP rejects it or times out) leaves no live config.
- If the last-known-good file holds another revision and no command has committed since, that is published instead, degraded and accepting commands.
- Otherwise the process stays up without a snapshot, and `/readyz` says not ready, as in M1.
- This is tested with a publish the publisher refuses. Every YARP setting the compiler emits is checked before YARP sees it, so no config makes YARP itself reject one.

**Readiness while degraded.** `/readyz` stays 200, with the body `degraded` instead of `ready`. The proxy serves traffic, and taking it out of rotation would turn a config-store problem into an outage. Keyword monitors can alert on the body. The cause is in the Critical log, and the admin UI shows the banner (M5).

**Nothing to serve.** With neither a usable database revision nor a usable last-known-good document, `StartAsync` throws `ConfigStartupException` and the process exits 2 (startup failed), logged once at Critical by the host. An empty proxy that refuses changes would only look alive.

**Change to live.** Through the whole process (real SQLite, YARP and Kestrel; one upstream and one host, two commits, until the first request for the new host comes back): median 11 to 15 ms on a dev machine with the rest of the suite running alongside.

## Application-layer cleanup after M7

An external review of the Application layer found four things. Each was checked before it was acted on.

- **One writer, one process.**
  - `ConfigService` carried a protocol for a second writer that does not exist (the CLI never writes config). The re-plan path for a commit that finds the head moved is gone, with its log line, its conflict fallback and its tests.
  - What stays is what one process needs: the writer lock, the unique revision indexes as the backstop, and the whole recovery set (last-known-good, restore of an empty database, degraded starts, the fallback after a failed startup publish). Each of those happens with one process: a deleted database, a corrupt revision, a stricter compiler, a YARP refusal.
- **`ConfigService` is split by responsibility, not by partial.**
  - `ConfigService` is the public surface and the writer lock.
  - `ConfigPipeline` runs a command's steps: authorize, plan, validate, compile, guard, commit.
  - `ConfigPublishing` carries a commit to live: queue, wait, last-known-good, warnings, validity timer, store status.
  - `ConfigRecovery` starts the store and republishes the live config.
  - `ConfigDrafts` serves the request simulator.
  - `ConfigCommandPlanner`, `AdminLockoutGuard` and `CertificateBindingGuard` are pure functions of documents, tested on documents alone.
  - `ConfigAudit` is how the writer records audit events.
- **The projection is dropped.** The seven tables (`GlobalSettings`, `Profiles`, `Upstreams`, `Destinations`, `Hosts`, `HostNames`, `Routes`) were written on every commit and read only by startup's own consistency check and by tests. `ConfigProjection`, the entities, the startup heal and the migration `DropConfigProjection` replace them. A test migrates a database created at the previous migration and keeps its revisions.
- **Authorization converged.**
  - There had been three typed authorizers (config commands, certificate operations, simulations), each with an allow-all default that identity registration had to replace in whichever order the two ran. On top of those sat an optional evaluator in the services outside the command pipeline, Admin endpoint filters, and the dashboard's permission sets.
  - The authorizers and their defaults are gone. `IPermissionEvaluator` is the one check and is required everywhere.
  - Admin keeps its minimal-API filters (`RequirePermission`, with the existing conventions that every caller requirement has its filter and every route its expected permission). The dashboard's sets come from the same evaluator.
  - This also fixed a real defect: the issuance services were built in a container that never registered the evaluator, so their own permission checks were silently off in production (the Admin filters still held). A host-level test now pins it.
- **No optional constructor parameters in Application.** Production dependencies are required. A nullable type is used only where null is a state of the world: no built-in certificates, no external account binding, no DNS provider. The ACME client provider's fallback registration is gone, and `ApplicationConstructorConventionTests` fails when an optional parameter comes back. Tests get their defaults from shared builders (`TestConfigServices`, `TestPermissions`, the issuance harness).
- **Left as it was.** The recovery cases, the commit-time unique-index backstop, and Admin's attribute-free filters (a decision of the owner). The dashboard still hides what the user may not do from its own permission set, the evaluator's answer at the last re-check. The services refuse regardless.
