# Config: typed IDs, data model and lifecycle

For contributors: how the config is stored, validated, compiled and published, and why.

## In short

- The config is **one document**. Every change is a command that validates it, compiles it into an immutable runtime snapshot, commits it as a new revision and publishes it. Nothing else holds a copy of the config.
- Every ID is a typed struct (`HostId`, `UserId`, ...) from the entity to the EF column, so one kind of ID can never be passed for another.
- Settings layer from built-in defaults, global defaults, profile and host, with a route's own subset on top. Null means inherit. One merge (`HostSettingsLayers`) serves the compiler, the validator and the dashboard.
- Server settings (retention, sign-in policy, connection limits) live in the same document under `settings.server`, so they get the review drawer, audit, history and undo for free.
- The write path is cold and may allocate and lock. The runtime snapshot is immutable and read lock-free, and a request reads it once.
- Mutable runtime state (rate-limit buckets, metric slots, health) lives outside snapshots and is keyed by stable identities.
- Startup recompiles the latest revision. A last-known-good file keeps the proxy serving if the database is unusable.
- Code: `Paduraksa.Core` (IDs, model), `Paduraksa.Contracts` (the document, commands), `Paduraksa.Application` (commands, compiler), `Paduraksa.Proxy` (snapshot, gate, YARP). The identity tables are in [identity.md](identity.md).

Contents:
- [Typed IDs](#typed-ids)
- [Data model](#data-model)
- [Upstream Host header modes](#upstream-host-header-modes)
- [Settings layers](#settings-layers)
- [Server settings](#server-settings)
- [Write path](#write-path)
- [The runtime snapshot](#the-runtime-snapshot)
- [State that survives a publish](#state-that-survives-a-publish)
- [The config document and the compiler](#the-config-document-and-the-compiler)
- [Startup, last-known-good and degraded starts](#startup-last-known-good-and-degraded-starts)
- [How the Application layer is organised](#how-the-application-layer-is-organised)

## Typed IDs

Every ID is a typed struct, declared with an attribute and completed by a source generator. The machinery is generic and is published as two NuGet packages, in the `Paduraksa.TypedIds` and `Paduraksa.TypedIds.EntityFrameworkCore` namespaces; the ID structs themselves (`HostId` and the rest) stay in `Paduraksa.Core.Model`.
- **`Paduraksa.TypedIds`** (`src/Paduraksa.TypedIds`, net10.0 and net8.0) holds `ITypedId`, `IdKind`, `[TypedId]`, `TypedIdCodec`, `Base62`, the three generators and the JSON and `TypeConverter` adapters. The generator (`src/Paduraksa.TypedIds.Generator`, netstandard2.0) is packed inside it, under `analyzers/dotnet/cs`, so one reference brings both and their versions can't drift.
- **`Paduraksa.TypedIds.EntityFrameworkCore`** (net10.0) holds the convention, converters, mapping and Int64 seeding. It needs `Microsoft.EntityFrameworkCore.Relational`, not SQLite.
- The namespace is plural on purpose: a `Paduraksa.TypedId` namespace would shadow the `[TypedId]` attribute in all code under `namespace Paduraksa.*`.
- Stored data is unchanged by the move: the string form, the BLOB and INTEGER columns and the JSON form are byte-identical, and the migration snapshots did not change.
- Paduraksa-specific rules stay here: the architecture tests (`TypedIdRuleTests`, `TypedIdPrefixTests`) and the `Guid.NewGuid()` ban in `BannedSymbols.txt`.
- `TypedIdCodec`, `TypedIdJsonConverter<T>` and `TypedIdTypeConverter<T>` exist for generated code: public, hidden from completion, documented as such. Generated code disables CS1591, so a consumer that turns on documentation files and warnings-as-errors still builds.
- The generic tests live in `tests/Paduraksa.TypedIds.Tests`. The sample model and in-memory database they share with the SQLite setup tests are linked from `tests/Shared/TypedIdSample`. `Paduraksa.Storage` no longer references Core: it held only the EF code that moved.
- Releases: `typedids-v*` tag, version in `src/Packaging.TypedIds.props` ([development.md](../development.md#publishing-the-nuget-packages)).

```csharp
[TypedId("host", IdKind.UuidV7)]   public readonly partial record struct HostId;
[TypedId("usr",  IdKind.UuidV7)]   public readonly partial record struct UserId;
[TypedId("chl",  IdKind.UuidV4)]   public readonly partial record struct PowChallengeId; // public-facing, no timestamp leak
[TypedId("aud",  IdKind.Int64Ordered)] public readonly partial record struct AuditEventId; // high-volume
```

For each declaration the generator emits:
- `Value` (`Guid` or `long`), `New()`, `Empty`, `IsEmpty` and the `static abstract` contract `ITypedId<TSelf>`.
- `ToString()` (for example `host_0J9zb3xQk1Lr8m2V7aYc4D`) and the span and UTF-8 formattable and parsable interfaces. Parsing validates the prefix and allocates nothing.
- Converters for `System.Text.Json`, `TypeConverter` (Blazor routing, Minimal API binding) and YamlDotNet.
- An EF Core `ValueConverter` and `ValueComparer`, applied by convention (`AddTypedIdConventions()`), so there is no per-property config.
- An OpenAPI schema transformer: `{ type: string, pattern: "^host_[0-9A-Za-z]{22}$", example: ... }`.

### Representations

| Kind | Use for | String form | DB storage |
|---|---|---|---|
| `UuidV7` (default) | Config entities, users, tokens, revisions, certs, orders | `prefix_` + 22-char base62 | 16-byte **BLOB, big-endian**. Byte order equals time order, so indexes stay append-mostly. Never EF's default TEXT GUIDs. |
| `UuidV4` | Public IDs whose creation time must not leak (challenge IDs, share links) | `prefix_` + 22-char base62 | 16-byte BLOB |
| `Int64Ordered` | High-volume append-only tables (audit events, metric rollups, access-log index) | `prefix_` + 11-char base62 | `INTEGER PRIMARY KEY` (the SQLite rowid) |

- **UuidV7** comes from our own RFC 9562 `UuidV7Generator`, with a 12-bit per-millisecond counter so IDs made in one millisecond still sort in creation order. `Guid.CreateVersion7()` fills those bits randomly and is banned. Bytes are written with `Guid.TryWriteBytes(span, bigEndian: true, out _)`.
- **Int64Ordered** is a 44-bit millisecond timestamp (epoch 2025-01-01 UTC), then a 19-bit monotonic counter seeded randomly at startup. The sign bit is always 0, and parsing rejects values above `long.MaxValue`.
  - These IDs are predictable, so they are **internal and admin-only**. Never expose one publicly.
  - They are collision-free on a single node if the Data layer seeds the generator at startup with `Int64OrderedIdGenerator.Observe(MAX(id))`. That covers a clock that is behind the last persisted ID.
- **Base62** uses `0-9A-Za-z` (ASCII order), fixed width, left-padded. Ordinal string sort equals numeric sort, so the UI, API and database order IDs alike.

### Generator notes

- Generated `New()` uses the process-wide generators on `TimeProvider.System`. Code that needs a controllable clock (tests, the audit log, the revision store) creates its own generator on its own `TimeProvider`.
- Every source-generated `JsonSerializerContext` that serializes typed IDs must register `TypedIdJsonConverterFactory` through `[JsonSourceGenerationOptions(Converters = [typeof(TypedIdJsonConverterFactory)])]`. The STJ source generator cannot see the `[JsonConverter]` attribute our generator emits, and without the factory IDs serialize as `{}`.
- The generator is an analyzer in every project (`Directory.Build.props`). Prefix uniqueness is checked per compilation (PDKID004) and across all src assemblies by an architecture test.
- EF value converters cannot call static abstract members in expression trees (CS8927). Generic converters go through a plain helper: `static T Wrap<T, TV>(TV v) => T.FromValue(v)`.

### EF mapping (`Paduraksa.TypedIds.EntityFrameworkCore`)

- `AddTypedIdConventions()` swaps in a `PropertyDiscoveryConvention` that finds typed IDs from the model's own CLR member types (no scanning, no type lists), in entities and complex types, as keys, FKs and nullables. They are `ValueGenerated.Never`.
- **Inside JSON complex types an ID is its canonical string** (`host_...`), not BLOB or INTEGER. The document stays readable, and JSON members compare against a string parameter.
- ID lists (`List<RouteId>`) are primitive collections of canonical strings, as a column or inside JSON.
- Owned types mapped to JSON are rejected at model build. Use complex types.
- **Hard rule: a JSON ID (TEXT) never equals a column ID (BLOB or INTEGER) in SQL.** References inside JSON settings are resolved in memory by the compiler, never joined or compared in SQL.
- LINQ `==`, `!=`, `<`, `>`, `CompareTo`, `Contains` and `OrderBy` on IDs translate. `id.Value` does not, because EF cannot see through a converter.

### Secrets that look like IDs

- **API tokens** are `pdk_<tokenId base62>_<32 random bytes base62>_<crc32 base62>`. The prefix and checksum let secret scanners (GitHub, gitleaks) find leaked tokens. The database stores `tokenId` and `SHA-256(secret)`. A fast hash is fine for 256 bits of entropy, and lookup by ID makes verification constant-time.
- **Session cookies** are opaque random 256-bit values, and only the SHA-256 is stored. The session ID (`ses_`, UuidV4) is a handle for the session list, not the credential.
- **No ASP.NET Data Protection around the cookie value.** It is already unguessable and stored hashed, so a key ring would add rotation and no security. The Data Protection key ring in the vault serves antiforgery and Blazor only.

## Data model

Two database files, deliberately separate:

| File | Holds | Owned by |
|---|---|---|
| `data/paduraksa.db` | Config revisions, users, audit, metric rollups | `Paduraksa.Data` |
| `data/vault.db` | Encrypted key material and secrets | **Only** `Paduraksa.Vault`, through its own internal DbContext |

- Both share the typed-ID conventions, SQLite pragmas and timestamp storage from `Paduraksa.Storage`.
- Keeping the vault apart means backing up or exporting config never sweeps up keys.
- SQLite: WAL, `synchronous=NORMAL`, `foreign_keys=ON`, `busy_timeout`. One writer is enough because all writes go through the Application layer. Reads use `IDbContextFactory`, which Blazor Server also requires.

### The config document is the only copy

- The config entities below are the content of the config document (see [The config document and the compiler](#the-config-document-and-the-compiler)). They have no tables of their own: the migration `DropConfigProjection` removed tables that were written on every commit and read by nothing.
- The validator now enforces the one rule those tables enforced: a unique host name among enabled hosts, in any case (`host.name.duplicate`).
- Certificates, ACME accounts and orders, DNS providers, notification channels and the identity entities are tables, because they hold state outside the document.

### Config entities

| Entity | Key fields |
|---|---|
| `GlobalSettings` (singleton) | `Defaults: HostSettings` (JSON complex type), default-host behaviour, trusted proxies, admin settings, ACME defaults, `Server` (see [Server settings](#server-settings)) |
| `Profile` (`prof_`) | Named preset of `HostSettings` overrides, for example "Public app", "LAN only", "Media server" |
| `Host` (`host_`) | `Kind` (Proxy, Redirect, Respond, Admin), `Enabled`, `ProfileId?`, `Settings` (overrides), `CertificateBinding` (Auto, a specific `CertificateId`, or None), `Tags`, `Notes`, concurrency token |
| Host names | A host's `Names` list. They have no ID: names are natural keys, unique across hosts ignoring case, normalised on write (below). |
| `Route` (`rte_`) | Host FK, `Match` (path exact or segment-aware prefix), `Order` (tiebreaker), `UpstreamId`, per-route `Settings` overrides, transforms. Equivalent to NPM "custom locations". |
| `Upstream` (`ups_`) | Load-balancing policy, health checks, HTTP version, Host header mode, TLS-to-upstream trust, timeouts, affinity, connection and DNS refresh |
| `Destination` (`dst_`) | Upstream FK, address, weight, enabled |
| `AccessList` (`acl_`) | CIDR allow/deny, basic-auth users (a username and the ID of a password credential in the vault; no hash in the document) |
| `RuleSet` (`rs_`) / `Rule` (`rule_`) | Path, UA, header, method and country deny rules, ordered |
| `ErrorPageSet` (`eps_`) | Per-status templates (HTML), assets |
| `AuthProvider` (`authp_`) | ForwardAuth (authentik, Authelia and oauth2-proxy presets), OIDC (later) |
| `Certificate` (`crt_`) | Source (Acme, Uploaded, SelfSigned, LocalCa), SANs, NotBefore/NotAfter, issuer, public chain (PEM), fingerprint, renewal state. **No private key.** |
| `AcmeAccount` (`acct_`) | Directory URL (LE, LE staging, ZeroSSL, Google or custom), contacts, EAB key id and **secret ref**, status, account URL, key algorithm |
| `AcmeOrder` (`ord_`) | One row per certificate: status, challenge type, names, CA order URL, staged-key token and CSR (resume state), last error, attempts, retry time, ARI window, next renewal |
| `DnsProvider` (`dnsp_`) | Kind (Cloudflare, deSEC, ...), label, credential `SecretRef` (`sec_`) into the vault. The hosts the token may reach are stored with the secret. |
| `NotificationChannel` (`ntf_`) | Not part of the document; see below |

**Host names** are trimmed, lowercased and IDN-converted to punycode (`IdnMapping`) on write. They are at most 253 characters with labels at most 63, a wildcard only as the leftmost `*.` label, and no trailing dot.

**Notification channels** are an operational table like `DnsProvider`.
- Columns: label, kind (ntfy, Gotify, Discord, webhook, email), enabled, subscribed events, the kind's non-secret settings, vault `SecretId`s for its secret and a webhook's signing key, and delivery state (last sent, last error).
- Deliveries live in the outbox (`NotificationEvents`, `NotificationDeliveries`; see "Durable delivery and history" in [hot-path.md](hot-path.md)).
- Events: upstream down and up (also per destination), expiry warning for every certificate (default 30/14/7/1 days, the local CA root included), ACME failure and recovery, sign-in lockout and new address.

**Host kinds:**
- **Proxy** (YARP).
- **Redirect** (precomputed target).
- **Respond** (fixed status and body from the compiled snapshot, for example 404, 410 or "coming soon").
- **Admin** (see [hosting.md](hosting.md)).
- **Static** (serving files from disk) is **not built**: it needs path-traversal, symlink, MIME and caching semantics designed properly. The plan when it comes back: a chroot-like root under `data/www/<hostId>`, symlinks disabled, `PhysicalFileProvider` with `ExclusionFilters.Sensitive`, ETag and Last-Modified, and precompressed `.br`/`.gz` siblings.

### Upstream settings

- **Host header:** `Original` (default) sends the client's, `Destination` the destination's address, `Custom` a fixed name. See [Upstream Host header modes](#upstream-host-header-modes).
- **TLS to the upstream, trust:**
  - `System` (default) verifies against the system roots and the name sent.
  - `Pinned` accepts exactly the listed SHA-256 fingerprints, whatever their name, chain or dates. It is for self-signed homelab gear.
  - `Any` skips verification, with a compile warning.
  - Not built: SNI apart from Host (decided against, see below), a custom CA, and a client certificate for mTLS.
- **SNI with Pinned.** SNI is the Host sent. An upstream with several names may present a certificate per name, so the dashboard's fetch asks each destination with its own name and with every host name that routes to it, and offers to pin every distinct fingerprint. A refused handshake is logged with the fingerprint presented and the name asked for.
- **Connection and DNS refresh.**
  - `PooledConnectionLifetime` defaults to 60 s, so a restarted container with a new IP is picked up quickly.
  - The connect timeout (10 s) and idle timeout (1 hour, "never" allowed) are per upstream; see "Upstream timeouts" in [hot-path.md](hot-path.md). `PooledConnectionIdleTimeout` is not built.
  - If a destination fails to connect, it is passively marked unhealthy, which forces new connections (and a fresh DNS lookup) on retry.

## Upstream Host header modes

**The setting.** `UpstreamConfig.HostHeader` is `{mode, value}`.
- `original` is the default and the absence of the key. Canonical JSON writes none, so stored revisions keep their bytes.
- `destination`, or `custom` with a `value`.
- A custom value is a host name or IP literal normalised like host names elsewhere (trimmed, lowercased, IDN to punycode, no wildcard, IPv6 in brackets), with an optional `:port` from 1 to 65535. It is exactly the string sent.
- Validator codes: `upstream.hostHeader.value.required` (custom without a value), `.unused` (a value under another mode), `.invalid` (not a name, a bad port, or an IPv6 literal without brackets).
- The compiler checks a custom name again as a backstop (printable ASCII with no space, `/`, `\`, `?`, `#`, `@` or `%`), so a hand-built model cannot put anything but a name on the wire.
- The enum `UpstreamHostHeaderMode` is shared like the others (Core, both JSON contexts, `SharedEnumWireTests`). YAML and the admin API need nothing of their own.

**Where it applies.**
- The compiler gives each `CompiledRoute` its upstream's `UpstreamHost` (mode and name), one shared instance per upstream. `UpstreamHostTransform` reads a precomputed string off the route and writes the one header. Nothing is formatted per request.
- `Destination` sends no Host, so the HTTP client sends the destination's own authority: its host (punycode for an IDN) and its port unless it is the scheme's default. An upstream with several destinations sends each its own.
- The admin hop's route is always `Original`.
- A custom name is also put in the cluster's metadata (`paduraksa.upstream-host`, beside the pins), which the active health probe reads.
- **Pitfall.** When the builder has no host transform, YARP adds one at the end of the chain that drops a `Host` equal to the client's, so a custom name equal to the client's vanished and the destination's address went out. The builder therefore registers `AddOriginalHost(false)`: its transform clears the header, its presence stops YARP adding the default, and ours sets the header afterwards.
- **Cost.** `UpstreamHostAllocationTests` pins it. Choosing the Host allocates 0 B in every mode. Writing a `Host` into `HttpRequestHeaders` allocates inside the framework whatever the name, and a request now clears and writes once, which allocates no more than the single write `AddOriginalHost(true)` made.

**SNI follows the Host sent. There is no setting of its own.**
- The forwarder's `SocketsHttpHandler` takes the TLS server name, and the name its certificate is checked against, from the request's `Host`. `Original` sends the client's name, `Destination` the destination's host, `Custom` its name without the port. An IP literal sends no SNI.
- A second knob would need the handler's connect callback and a per-cluster client, for a case nobody has asked for. `Pinned` trust is already independent of the name.
- The dashboard's certificate fetch for pinning asks for the same names (`UpstreamEditModel.SniNames`): `Original` the address's own name plus every host name routing here, `Destination` and `Custom` one name each.

**Active health probes.**
- With `Custom`, `UpstreamHostProbes` (a wrapper of YARP's `IProbingRequestFactory`) sends the name on every probe. An upstream that answers only to its own name would otherwise fail them all and be taken out.
- With `Original` and `Destination` the probe goes with the destination's address, because a probe has no client Host to copy.

**Simulator and dashboard.**
- The Upstream step carries `hostHeaderMode` and `hostSent` facts: "forwarded to upstream X with Host: Y". For `Destination` it lists the distinct destination hosts. `UpstreamHostHeaderTests.TheSimulatorReportsTheHostTheUpstreamReceived` compares it with what a real upstream received, in every mode.
- The upstream drawer's Advanced panel has "Host header sent to the upstream", and for a fixed name, the name. One line of help says SNI and the certificate check follow it.

**Tests.** `UpstreamHostHeaderTests` (an upstream echoing Host and SNI in every mode, over HTTP/1.1 and h2c, plain and TLS; a republish switching mode), `UpstreamHostAllocationTests` and `SnapshotCompilerTests`. Unit tests cover the validator, canonical form, mapping and YAML.

**Left out.** A Host per route or per host: the upstream is the unit, so two routes that need different Hosts point at two upstreams. `ConfigGen`, the owner's property generator, does not generate the key yet.

## Settings layers

`HostSettings` is one record whose every field is nullable. Null (absent in JSON) means **inherit**. A layer is stored as an EF Core 10 JSON complex type, so a new setting needs no schema migration.

The layers, from the bottom: built-in defaults, global defaults, profile, host, and on top of the host a route's own subset. A route carries only the route-level subset of settings.

One merge owns the layers for every consumer: `HostSettingsLayers` in `Paduraksa.Application`.
- `Merge(lower, upper)` lays one layer over another and keeps the result a layer.
- `Resolve(layers)` puts `BuiltIn` underneath and yields the concrete values the compiler and the validator read.
- The dashboard's fields merge `BuiltIn` under what they inherit the same way, so a form cannot promise a value the proxy would not apply. It shows where each value comes from ("inherited from Global", "from profile *LAN only*") with "override here" and "reset to inherited".
- `BuiltIn` is the one place a built-in default is stated.

**Nested records** merge field by field: `Tls`, `Hsts`, `ServerHeader`, `ProofOfWork`, `RateLimit`, `Robots`, `Cloudflare`, `AccessList`, `RequestLimits`, `RuleSet`, `Headers` and `Compression`. `Headers` merges rule by rule, keyed on the header name (see "Custom headers" in [hot-path.md](hot-path.md)).
- A layer lifts an inherited rate limit, access list or rule set with `enabled: false`. Everything else is a plain override.
- If a nested record sets none of its fields, it is no record at all. Canonical JSON writes no key for it, so a stored revision keeps its bytes and hash. `ISettingsLayer.SetsNothing`, stated by each record, is the one rule.

**The fields:** force HTTPS, TLS (minimum version, TCP protocols, HTTP/3), HSTS, access log, request ID propagation, the `Server` header, proof of work, rate limit, `robots.txt`, Cloudflare (origin lockdown, Authenticated Origin Pulls), access list, request limits, rule set, auth provider, error pages, maintenance mode, custom request and response headers, and compression.

Not layered settings:
- Path rewriting is a route's own, `RouteConfig.Rewrite` (see "Path rewrite" in [hot-path.md](hot-path.md)).
- `X-Forwarded-*` is always written by the proxy (see [hot-path.md](hot-path.md#e-yarp)).

### Adding a host setting

1. **The property** on `HostSettings` (or on a nested `TlsSettings`, `HstsSettings` or `ServerHeaderSettings`), at the end, so existing documents keep their key order.
2. **Its merge line** in `HostSettingsLayers.Merge` (or, on a nested record, in that record's `Nested(...)` combine there, which drops the record when `SetsNothing` says it is empty), and its value in the `Concrete` mapping, where the resolved contract value becomes the proxy's.
3. **Its built-in default** as a `const` of `HostSettingsLayers`, from which `BuiltIn` is assembled. Nowhere else states one.
4. **A row in `SettingsInheritanceTests`**, the table that walks the contract: the field accessor, a layer that sets it, what the compiled host then carries, the built-in default and two sample values.
   - The table's completeness test fails until the row exists.
   - Once the row exists, a field missing from `Merge` fails it.
   - A field on a **nested** record must also be added to `ISettingsLayer.SetsNothing` (or `EveryNestedRecordKnowsWhenItSetsNothing` fails) and to the record's line in the nested-merge cases (or `EveryNestedRecordHasANestedMergeCase` fails).
5. **Its rules in `ConfigValidator`**: field rules at the layer, and resolved-value rules on the host's resolved settings with the layer named by `HostSettingsLayers.LayerOf`. Add the compiler's backstop for the same value.
6. **If it sends a header:** its operation in `CompiledHost.ResponseHeaders` (kind, stage and conditions in `ResponseHeaders`, and its line in `SnapshotCompiler.CompileResponseHeaders`).
   - Add its value or absence to every row of `ResponseHeaderMatrixTests`. The new header becomes a column each row must state.
   - Add one end-to-end test through `PaduraksaHost` if nothing else covers it on the wire.
7. **A new enum** shared by the document and the proxy goes in `Paduraksa.Core.Model` with `[JsonConverter(typeof(ConfigEnumConverter<T>))]`.
   - Register it in **both** `ConfigJsonContext` and `AdminJsonContext`, because the source generator cannot see a converter attribute in another assembly.
   - `SharedEnumWireTests` fails until its row in the shared list and both registrations exist.
8. **The dashboard field:** one row in `HostSettingsFields.razor` (title, help, `data-field`, error path), its phrase in `SettingsSummary`, and its row in `HostSettingsFieldsTests`, whose completeness check fails until the row exists.

For the settings UI step in detail:
- One `InheritSelect<T>` row in `HostSettingsFields.razor`, or the field's own small component (as `HstsMaxAgeField` is, because it keeps state). It gives the title and help, the label and `data-field`, the error path, the inherited value and the options.
- Its summary phrase goes in `SettingsSummary.Phrases`, in the contract's field order.
- Its `HostSettingsFieldsTests` row says how the component sets it, the record that leaves, how it clears, and the words of its inherit choice.
- Nothing else in the dashboard names a field. The form binds to the contract's record, and the merge and defaults are the backend's.

### HSTS rules

- Off by default. Once on, `max-age` is a year (31536000), with no subdomains and no preload.
- `MaxAge` is an integer in seconds. The validator checks it per layer that sets it, from 0 (tell a browser to forget the host) to 63072000 (two years).
- `preload` needs `includeSubDomains` and at least a year. It is judged on each host's resolved settings, like the certificate rule:
  - a profile may leave `includeSubDomains` to the hosts that use it;
  - a layer no host uses is never refused;
  - the error is reported at the host, naming the layer its `preload` came from, because the host sends the header.
- With HSTS off, the preload pair is inert.
- If a request is for a name no host serves, the response never carries HSTS, whatever the global defaults say. The proxy does not pin a name it does not serve.

### `Server` header rules

`ServerHeader` is the last field of `HostSettings`, after `AccessLog` and `RequestIdPropagation`, so existing key order and hashes stay put.

| Mode | Effect |
|---|---|
| `Paduraksa` (default) | Sends the name, never a version, replacing the upstream's header. |
| `Custom` | Sends its value, also replacing the upstream's. |
| `Upstream` | The upstream's own passes through. A gate answer has none. |
| `Remove` | Nothing at all, the upstream's included. HTTP/2 trailers are not touched. |

- A custom value is 1 to 128 visible ASCII characters or spaces, with no control characters and no leading or trailing space.
- It is required once the resolved mode is `Custom` (`serverHeader.value.required`) and checked as a value (`serverHeader.value.invalid`). The shape is checked where it is set and the resolved pair at the host, like HSTS's preload pair. An inherited bad value names the profile or global defaults it came from.
- Unknown hosts follow the global defaults' resolved mode. An admin host through the proxy uses its own like any host.

## Server settings

Values that tune the server itself (retention, the admin sign-in policy, notification timing, the integrations, the public connection limits) are in the config under `settings.server`. The dashboard edits them on Settings, Server. Some also still come from the environment.

### Where they live, and why the config document

- **`GlobalSettings.Server`** (`ServerSettings`) is a flat record of nullable fields; null is the default. Canonical JSON writes no `server` key while it sets nothing, so stored revisions keep their bytes and hashes.
- **Why the document, not a table.** It gets the review drawer (Apply now or Add to draft), the audit event, History and Undo by rollback, YAML export and import, and MCP's draft-first writes for free. A table would need each of those again.
- **One version with the global defaults.** A change is an `UpdateSettingsCommand`, so editing server settings and global defaults at once is a conflict, like two edits of a host. That is rare enough on one node to accept.

### One declaration per setting

`ServerSettingCatalog` (Contracts) declares each setting once: its property, its configuration key under `Paduraksa`, its group and heading on the page, live or restart, title and help, default and bounds, and optional review notes.

Everything else walks the catalogue:
- the validator: each value against its bounds (`server.setting.invalid`), then `ServerSettingCatalog.Rules` between settings over the values in force (`server.settings.conflict`): an idle timeout no longer than the session lifetime, and a data rate's grace over a second;
- the server's options validator, which holds the environment's values to the same bounds;
- environment detection, the API view, the page's fields, the review notes and the user guide's table, which `ServerSettingsGuideTests` regenerates and checks.

### Precedence

- **An explicit configuration value wins.** `ServerSettingEnvironment` asks the configuration providers whether a key has a value (appsettings, an environment variable, a test's in-memory source), never whether it differs from the default. A value equal to the default is still the environment's. The shipped `appsettings.json` sets no log retention keys, so they don't lock the dashboard out.
- **Then the config, then the default.** `ServerSettingsState` resolves the three into the values in force and the source of each.
- **A mix the validator can't see.** The validator sees only stored values. If an environment value breaks a rule with a stored one (an environment session lifetime shorter than a stored idle timeout), `ServerSettingsState.Settle` settles it:
  - every stored setting the rule involves (`ServerSettingRule.Involves`) falls back to its default, both sides if both are stored;
  - the rules are checked again, since a default can break another, until a pass drops nothing (at most one pass per setting);
  - a rule still broken with nothing stored in it is the environment against a default, which the options validator refuses at startup, so a running server never has one.
- **The command line follows it.** `admin reset-password` and `backup` resolve the sign-in policy through the same state, reading the stored values from the config database read-only (`CliSettings`).
- **An environment-locked value is still stored.** The API accepts it and the page shows it read-only, so removing the variable makes the stored value apply at the next start.

### Taking effect

- **Following the queue.** The compiler copies the server settings onto `ConfigCompilation.Server`. `ConfigPublishing` raises them when a snapshot is queued, not when YARP confirms it: the revision is committed by then, and startup values must be known before the public app binds, which is before the startup publish can complete.
- **Live consumers.** `ServerSettingsBindings` (Server) hands the values in force to each consumer's `LiveOptions<T>` (Core). A consumer reads `Current` when it acts, never a copy taken at construction.
  - Live: the rollup and analytics prunes, the notification dispatcher and health watcher, the identity services, the access-log drain and tail, the Cloudflare refresher and ACME issuance.
  - The log files take `Retain`, which also queues a trim at once.
  - The admin app reconfigures the sign-in throttle and switches MCP. The public app sets Kestrel's per-connection limits.
- **Restart only.** Kestrel reads the TLS handshake timeout and the connection caps when the listeners bind. `ConfigStartup` seals the values once the startup snapshot is queued (`ServerSettingsState.Startup`), and the public app's Kestrel callback runs after that. A stored cap therefore applies at the next start, and the page marks a stored value the process doesn't run with as *Restart pending*.
- **Sessions.** A session is checked against the lifetime in force from its creation, so a shortened lifetime ends existing sessions at their next check. A lengthened one never extends a session already issued. A completed session counts from the partial session's creation, so a shortening may end it up to the partial lifetime early, which is the safe side.
- **Touch interval** stays an environment setting. If a stored idle timeout is shorter than it, the bindings write the last-seen time at half the idle timeout, so live sessions don't idle out.

### Who may change them

- **`server:write`** (`Permission.ServerWrite`) belongs to the Owner and Admin roles, never the Operator.
- **Checked on what the plan does to the document,** not on the kind of command. `ConfigPipeline` requires it whenever `settings.server` differs between the head and the planned document. That covers `PUT /server-settings`, `PUT /settings`, an import, a rollback and a draft's entry alike.
- **Simulations aren't checked**, since they never commit.
- **Staging in a draft isn't checked either.** Adding an entry checks the entry and commits nothing, so an Operator can stage a server-settings change. The draft's apply runs the pipeline as the person applying it, and that is where it is refused.

### Pointing at a setting

- **Anchors.** Each setting's row has the id `ServerSettingDefinition.Anchor` (the key in kebab-case), so `/settings/server#mcp-enabled` scrolls to it, focuses it and highlights it (see "Linking to a field" in [dashboard.md](dashboard.md)). A locked setting keeps its anchor: the link lands on the note that names the variable.
- **Hints link, they don't name the variable.** A dashboard hint for a catalogue setting links with `SettingsRoutes.SettingLink`. The API tokens page's "MCP is off" says to turn on *Serve /mcp* in the server settings.
  - If someone lacks `server:write`, the hint says an Owner or Admin can change it, and still links, since the page shows the value read-only.
  - When the environment keeps it off, the hint says so, names the variable and links to the row.
- **Kept as they are:** the server's own error and warning texts name settings outside the catalogue (`Paduraksa:Listen:AdminAllowedSources` in the forwarded-headers refusal, `Paduraksa:Listen:AdminInternal` in the compiler's warning). The OpenAPI description of the notification history names `Paduraksa:Notifications:HistoryRetention`, left alone to keep the API's description stable.

### Adding a server setting

1. The property at the end of `ServerSettings`.
2. Its declaration in `ServerSettingCatalog` (key, configuration key, group and heading, live or restart, words, default, bounds, notes), and its place in `All`. `ServerSettingCatalogTests` fails for a property without one.
3. Where its consumer takes it: a line in `ServerSettingsBindings.Apply` (or the admin or public app's follower) onto the consumer's `LiveOptions<T>`. If a consumer reads it once, the setting is declared `Restart`.
4. The options validator's bound for the environment's value, from the declaration (`Bound(ServerSettingCatalog.X, ...)`). The option's default must be the declaration's: `ServerSettingOptionsTests` walks the configuration keys.
5. Regenerate `openapi.v1.json` and the guide's table (`PADURAKSA_UPDATE_DOCS=1` on `ServerSettingsGuideTests`).

The page, the API view, the review notes and the precedence need nothing.

### Left out

- **Still in the environment:** the data directory, the listeners, the development switches, the trust files, the log directory, the timeouts of startup and shutdown, PBKDF2's iterations, the write intervals, the ring and tail sizes, the ACME scheduler and the test hooks.
- **DNS-01's resolvers and timings.** Only the check's switch and the fixed wait moved.
- **The storage-impact estimate and table sizes** on the page.
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
  5. BEGIN TX: write ConfigRevision(doc, diff), AuditEvent; check concurrency token; COMMIT
  6. PUBLISH (single writer, coalescing to the latest candidate):
     a. swap the YARP provider config, cancel the old change token
     b. await YARP ConfigurationApplied / ConfigurationApplyingFailed (matched by RevisionId, timeout -> alert)
     c. capture YARP's new Endpoint objects into the candidate's slot -> Endpoint table
     d. SnapshotStore swap                       <- atomic reference swap; requests see it from here on
  7. notify: change feed (dashboard live update)
```

- **Optimistic concurrency.** Each top-level object has a version, and commands carry the version they edited (or `baseRevision` for whole-document commands). Two admins editing the same host get a conflict dialog with a diff instead of a silent overwrite.
- **Dry run.** Every command supports `dryRun: true`, which runs steps 1 to 4 and returns the diff plus validation results. The dashboard's "Review changes" sheet, MCP's `execute` (a write without `confirm` is a dry run, see [admin-surfaces.md](admin-surfaces.md)) and `import --dry-run` all use it.
- **Rollback.** Load an old revision's document, run it through the *import* path (validate, compile) and commit it as a **new** revision ("Rollback to #37"). History is never rewritten.
- **Diff.** A structural diff over the canonical document (stable ordering by typed ID), rendered as an object-level change list plus a YAML side-by-side view.
- **YARP validation.** YARP's own `IConfigValidator` runs in step 4, so an invalid YARP config fails before commit. YARP otherwise swallows invalid configs: it logs and keeps the old one.
- **Startup recompiles the latest revision.** The latest committed revision is the source of truth, and the process compiles it on every start.
  - If the process crashes between commit and publish, the change goes live at the next start.
  - A failed publish after a commit is reported to the command, logged as an error and audited, with no retry. The commit stands and the previous snapshot stays live. The next commit's publish or the next start makes the change live, since every snapshot is the whole config.
- **Last-known-good.** After each successful publish the compiled snapshot's source document is also written to `data/last-good.json`, a fallback for an unreadable database. See [Startup, last-known-good and degraded starts](#startup-last-known-good-and-degraded-starts).

## The runtime snapshot

A `RuntimeSnapshot` is immutable, built once and read lock-free. Its shape:

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

`SnapshotStore` is `volatile RuntimeSnapshot _current` plus `Publish` (`Interlocked.Exchange`, then fire the change token). Each request reads the reference **once** and uses only that snapshot, so it never sees half of one config and half of another.

Code: `src/Paduraksa.Proxy` (`Snapshot/`, `Gate/`, `Yarp/`, `Config/`).

### How YARP is integrated

Decided by spike S2 (`docs/spikes/S2-yarp-reload.md`): **the gate picks the YARP endpoint itself, and ASP.NET routing never matches hosts.**

- **IDs.** YARP route, cluster and destination IDs are stable typed-ID strings with no generation in them. The YARP config is a **pure function of content**, so an edit that touches only policy is a YARP no-op.
- **Routes carry no Host match.** Each YARP route is path-only (or a catch-all per cluster) and exists only to give YARP an endpoint with the right cluster and transforms. Host matching was about 90% of the reload cost (500 hosts: 15 ms down to 1.9 ms; 2000 hosts: 64 ms down to 11 ms).
- **The gate resolves host and route** from its snapshot `S`, using a per-host route list ordered most specific first. It takes the policies **and** the YARP `Endpoint` from the same `S`, then calls `HttpContext.SetEndpoint(...)` *before* `UseRouting`, so endpoint routing skips matching.
  - A request cannot mix generations. There is no version check, no snapshot ring and no 503 path.
  - The gate costs 0 B. S2 measured gate entry to proxy pipeline at 152 B per request, against 256 B with Host matching.
- **Publish order** (step 6): YARP applies first, then the endpoint table is captured from `EndpointDataSource` (keyed by route ID), then the snapshot is swapped. Until the swap, requests keep the old `S` and its old `Endpoint` objects, which S2 showed keep proxying correctly even after YARP removed them, following YARP's live cluster state.
- **Custom pipeline.** `MapReverseProxy(Action<IReverseProxyApplicationBuilder>)` replaces YARP's default pipeline, so **session affinity, load balancing and passive health must be added back explicitly** (`UseSessionAffinity()`, `UseLoadBalancing()`, `UsePassiveHealthChecks()`). Without them, passive health silently does nothing.
- **Client reuse.** A custom `IForwarderHttpClientFactory` must reuse the existing client when cluster config is unchanged. A new `HttpClient` counts as a cluster change and rebuilds every route on it.
- `ConfigureEndpoints` metadata is **not** used: YARP re-runs it only for new or changed routes, so unchanged routes would keep stale metadata.

### Compiled types

- The config model and the snapshot are keyed by typed IDs. `Revision` is a `ConfigRevisionId` (`Empty` with no committed revision: the bootstrap snapshot, the empty startup config). `CompiledHost.Id` and `CompiledRoute.Id` are `HostId` and `RouteId`. Upstreams, destinations and certificate bindings use `UpstreamId`, `DestinationId` and `CertificateId`.
- YARP's route, cluster and destination IDs are their canonical strings (`rte_...`, `ups_...`, `dst_...`), formatted by the compiler. Nothing on the request path formats an ID.
- YARP compares IDs ignoring case, so the compiler rejects route, upstream and destination IDs that differ only in case.
- `RuntimeSnapshot.Yarp` holds the YARP *content* (`YarpConfig`). The `IProxyConfig` with its change token and a unique `RevisionId` is created per publish by the provider.
- `Certificates` (`CertificateTable`) and `TrustedProxies` (`TrustedProxyPolicy`) are typed slots the compiler fills. `SnapshotStore` is the TLS selector's `ICertificateTableSource`, so a publish hot-swaps certificates for new handshakes.
- **Unbound, then bound.** The compiler produces a candidate with generation 0 and no endpoints. The publisher `Bind`s it to YARP's endpoints and the next generation. The store starts at a bootstrap snapshot (generation 0, 503 for everything), so `Current` is never null.

### Route matching and order

The rule is **match leniently, forward unchanged.**
- Matching, and path rules after it, use a canonical *matching view* of the decoded path:
  - `\`, `%2F` and `%5C` separate segments, and repeated `/` collapse;
  - `;...` segment parameters are dropped;
  - trailing `.` and spaces are trimmed per segment;
  - dot segments resolve, and case is ignored.
- Many upstreams read those spellings as the same path, so `//admin`, `/admin;x`, `/admin.` and `/ADMIN` must hit the policies of `/admin`. Over-matching only makes a policy stricter.
- The view costs 0 B: the path as is when already canonical, else on the stack, or a pooled buffer past 256 characters. Kestrel's request-line limit bounds it.
- The compiler rejects route paths that are percent-encoded or not canonical.
- Routes carry **only their own** policies.
- YARP routes are `/.paduraksa/r/{routeId}/{**rest}`. The proxy pipeline answers 404 to any request whose endpoint the gate did not set, so they cannot be reached directly.

**Order.** A host tries exact routes before any prefix, then the longest prefix, then `Order`, then the listed order. The first match wins.
- The compiled snapshot fixes this order once, so request matching stays a scan.
- The listed order decides only between routes of equal kind, path length and `Order`. A catch-all listed first therefore cannot shadow the routes after it.
- The canonical document and the dashboard list routes in this evaluation order. YARP's internal routes carry no order, because the gate sets their endpoint.

### Forwarding and publishing

- **Forwarding.** The upstream gets the path exactly as received. A YARP request transform builds the URI from the raw request target (path part) plus the query, without re-escaping. YARP's default would turn `%2F` into `%252F`.
- **Publisher** (a single writer):
  - A queued candidate completes as `Superseded` when a newer one arrives.
  - YARP's `IConfigValidator` plus a route-to-cluster check run first (`Invalid`).
  - Content equal to what YARP runs skips the YARP round trip (policy-only edits).
  - `ConfigurationApplyingFailed` gives `Rejected`, and the previous YARP config is re-applied so provider and YARP agree. No outcome within `ApplyTimeout` (10 s) gives `TimedOut`. On any failure the old snapshot stays live.
  - **Before YARP exists** (the core host publishes first), an empty-YARP candidate publishes at once and any other waits for `MapPaduraksaProxy`, which validates it and hands it to YARP's initial load.
  - Startup code therefore **never awaits** a publish. The core host's start returns before the public app starts, so it queues the publish and `/readyz` (`HasPublished`) reports when it is live.
  - Disposing the publisher stops the writer and fails anything queued or in flight.
- **Fixed answers** (Respond hosts, policy responses) with status 204, 205 or 304 must have an empty body (a compile error otherwise) and are sent without `Content-Length`.
- **Default host:** 404 (default), 421, or close (H1 closes the connection, H2 and H3 reset the stream). The HTTP-to-HTTPS redirect is 301 for GET/HEAD and 308 otherwise. Its `Location` (request host + raw target + public port) is the one string a gate-answered response allocates, built after the decision.

## State that survives a publish

Snapshots are immutable and thrown away on every publish, so **mutable runtime state must never live inside one**. Otherwise every config edit would silently reset rate limits and break metric series.

Long-lived state sits in process-lifetime singletons (`RuntimeState`). Snapshots hold only *keys* into it, and those keys derive from **stable identities**, never from array positions.

| State | Lives in | Keyed by | On publish |
|---|---|---|---|
| Rate-limit buckets | One global fixed-size bucket table (see [hot-path.md](hot-path.md)) | A long: a **16-bit policy slot** (stable, from an append-only `RateLimitPolicyId` to slot registry, like metric slots) plus a **48-bit hash** of the partition key (IP/64, IP+path, header) | Carried over untouched. If a policy's rate or burst changed, the new parameters apply on each bucket's next touch (tokens clamped to the new burst). Deleted policies' buckets are cleared by the sweeper (an unknown slot means clear). |
| Per-IP connection counts | `ConnectionRuntime` (one `ConnectionTable`), a process singleton | Client address (IPv4 whole, IPv6 /64) | Not snapshot-related. Only the *limit* and the deny list come from the snapshot (`RuntimeSnapshot.Connections`). A lowered limit applies to the next connection, and what is already open stays counted (see "Connection stage" in [hot-path.md](hot-path.md)). |
| Forward-auth decision cache | `ForwardAuthRuntime`, a process singleton | `AuthProviderId` plus the provider's **config hash** (everything but the name) | Kept if the provider's effective config is unchanged, otherwise dropped, so a changed auth config never reuses old decisions. A snapshot's states become the live ones when it is published, not when it is compiled. |
| Basic-auth verify cache | The vault (`IPasswordVerifier`) | Per-process key, the credential's stamp (changes with its salt), masked client IP, and the user name and password as sent | Nothing to carry over. A changed or deleted password makes every earlier entry miss, and a publish touches nothing. |
| Metrics counters and histograms | `MetricsRegistry` | **`MetricSlot`**, allocated per typed ID (`HostId`, `RouteId`, `UpstreamId`, `DestinationId`) by an append-only slot registry | The compiler asks the registry for each ID's slot, so the same host keeps the same slot forever. Slots of deleted objects are freed after a grace period (one rollup interval). See [hot-path.md](hot-path.md). |
| Persisted metric rollups | SQLite (`MetricRollups`) | **Typed ID** plus minute, never slot or index | Not applicable: a deleted object's rows stay until retention removes them. The sampler flushes a slot's last minute under the old owner's ID before the slot is reused. |
| YARP health state | YARP (per destination) | ClusterId + DestinationId (typed-ID strings) | See below. |
| ACME HTTP-01 tokens | `IAcmeChallengeStore` | Token | Independent of config snapshots. |
| Cloudflare edge ranges | `CloudflareEdgeRangeSource` (process) and `<data dir>/cloudflare-ranges.json` | - | The compiler reads the current list at every compile, and the snapshot keeps the one it was compiled with. A refresh that changes the list has the live config compiled and published again, with no revision (see [hot-path.md](hot-path.md)). |

**YARP health state** (verified in S2) survives every reload that keeps the IDs, including changes to the cluster's own policies. Pitfalls:
- Removing and re-adding a destination resets it to *Unknown*, which YARP treats as routable.
- Changing a destination's address under the same ID keeps the old verdict.
- So rollback and import must keep destination IDs.
- Optionally, key destinations as `{DestinationId}~{hash8(Address, Host, Health)}` so an address change gets fresh state. This fixes only a stuck passive-unhealthy verdict (the new key starts as Unknown) and resets affinity for that destination.

**Continuity tests.** Spike S6 turns this table into tests: N generations are published under load, and rate-limit decisions, counter totals and YARP destination health must stay continuous. `StateContinuityTests` covers YARP health and request errors, `RateLimitContinuityTests` the buckets (and a lowered burst applied on the next touch), and `MetricsEndToEndTests` counters staying monotonic and on the same slots across a publish every 100 ms under load.

## The config document and the compiler

- **The document is the source of truth.**
  - `ConfigDocument` (in `Paduraksa.Contracts`, `apiVersion: paduraksa.dev/v1`) holds the global settings, profiles, upstreams and hosts with their routes. It has references only, never secrets.
  - Each revision stores it as canonical JSON (deterministic bytes), Brotli-compressed, with the SHA-256 of the uncompressed bytes.
  - Startup, rollback, diff and export all read documents.
- **No table mirrors the document.** Queries (dashboard lists, "which hosts use this upstream") read the latest revision's document.
- **Host names are natural keys,** stored normalised, with no ID.
- **Concurrency.**
  - Every top-level object (global settings, profile, upstream, host) carries a `Version` that the pipeline increments on change. A command names the version it edited, so two admins editing different hosts never conflict.
  - Whole-document commands (import, rollback) name the `baseRevision`, which must be the latest.
  - One writer at a time (`SemaphoreSlim`) in the Application layer.
- **Settings layering.** The compiler resolves the layers into the concrete `ProxyConfigModel`. `AccessLog` (`true` built-in) and `RequestIdPropagation` (`upstreamAndResponse` built-in) are scalar switches on the same path. Both are absent from canonical JSON when unset, so a stored document that never mentions them keeps its bytes and revision hash.
- **Errors carry a path.** Validation and compile errors carry a JSON-pointer path into the document (`/hosts/3/routes/0/path`), a stable code and a message, so every surface can point at the field.
- **Audit hash chain.** `hash = SHA-256(prevHash || canonical bytes of the event)`. The first event's `prevHash` is 32 zero bytes. A verifier walks the chain in ID order and reports the first break.

### The compiler (`src/Paduraksa.Application/Compilation`)

- **Mapping.** `ProxyConfigMapping.Map` is a pure function from the document's canonical form to `ProxyConfigModel`, so two spellings of one config compile alike.
  - `ConfigCompiler` (the `IConfigCompiler`) runs it and `SnapshotCompiler` with the process's `SnapshotCompilerOptions`.
  - `SnapshotCompiler` is split by concern: `Compile` orchestrates, the per-host step (admin-host rules included) is `SnapshotCompiler.Hosts.cs`, and upstreams, TLS/certificates and response headers have their own partials, each filling a small `CompileContext`.
- **Layering.** Per host: built-in defaults, global defaults, its profile (a missing profile adds no layer; the validator rejects it with `host.profile.unknown`), then the host. Per route, its own subset goes on top of the host's result. A null field inherits, TLS merges field by field, and `ForceHttps` is judged after layering.
- **Certificate binding** (resolved against the inventory, see [vault-and-certificates.md](vault-and-certificates.md)):
  - Auto means no certificate of its own (the default one). Specific uses its `CertificateId`.
  - None sets `HostSpec.Https = false`: no certificate-table entries and no `Alt-Svc`, and the gate answers HTTPS requests for the host 421 (a connection on the default or another host's certificate cannot reach it).
  - None with an effective `ForceHttps` is an error (validator: `host.certificate.noneWithForceHttps`; the compiler at the host).
  - Disabled destinations are left out. An upstream may have none left until an enabled host routes to it.
- **Compile errors carry paths.** `SnapshotCompiler` attributes each error to a host, route, upstream, destination (by typed ID) or the defaults and trusted proxies.
  - `ConfigCompiler` maps that to a pointer into the document *as given* (`/hosts/{i}`, `/hosts/{i}/routes/{j}`, `/upstreams/{k}/destinations/{m}`, `/settings`, `/settings/trustedProxies`) with code `compile.failed`, each (path, message) once.
  - An inherited value is reported where it is used (each host or route whose chain carries it). A duplicated ID points at its last occurrence.
  - The mapping reports the few invalid values the model cannot carry: an undefined certificate binding mode or load-balancing policy, or a respond host without a content type.
  - A test runs sample, edge and generated documents through the validator and the compiler. Anything that validates must compile, except certificate references, which only the compiler can resolve against the inventory.

### Policy chains: where access lists, rule sets and auth providers go

**Access lists: host chain and route chains.**
- The gate runs the host chain before the HTTPS redirect, fixed answers and route matching, so a route override cannot *replace* a list that sits there alone.
- When every route ends up with the same list (none overrides the host's, or all override it alike) and it has no users, or the host has no routes, that list goes in the host chain only. It guards everything the host answers.
- Otherwise each route carries its own effective list, and the host chain carries the **union** of the routes' address rules. It drops a request only if every route's list drops:
  - a client no route admits is blocked on every path, over plain HTTP too (no redirect, no 403-versus-404 to map the host by, and a dropping host still looks dead);
  - a client that some route admits is judged again by the route it hits.
- If some route's list cannot refuse by address (or a route has none), the host chain carries no union, since anyone may reach that route.
- A list with users puts only its address rules in the host chain and the whole list (passwords too) in each route's chain. A password is never asked for before the HTTPS redirect.
- A test drives the real gate over generated documents (scheme x path x client IP, lists with and without users) against a reference that reads the document directly.

**Rule sets: host chain and route chains.** The same shape as the access list's rule, without the union.
- When every route resolves to one set, or the host has no routes, the set is in the host chain. It goes after the host chain's address-only check or union, and for a host with no routes before a full list that has users.
- Otherwise each route's own chain carries its set, in front of its list when that has users (the list's address checks included). A route's set replaces the host's, and a deny cannot be lifted by a union.

**Auth providers: route chains only.**
- The provider is not a chain entry the mapping builds. It is a route policy the compiler adds after the rate limit and before proof of work. Reasons: a forward-auth call is the one expensive thing a request can cost outside the upstream, so a 429 should come first; and a cookie must never be sent to an endpoint over plain HTTP (the route chain runs after the HTTPS redirect).
- The mapping only resolves which provider each route has (`RouteSpec.AuthProvider`, with the route's own override marked).
- An admin, redirect or respond host ignores the setting with a warning (`authProvider.ignored`).

### Commands (`Paduraksa.Application.Commands.ConfigService`; commands and results in `Paduraksa.Contracts.Commands`)

- **Versions are revision numbers.** A revision gives every top-level object it changes (a route or destination change counts for its parent) its own `Seq` as the new version. Untouched objects keep theirs, whatever a command sends.
  - A version is issued once, ever, and stays at most the latest `Seq`.
  - A rollback to #37 committed as #52 gives what it changes version 52. An editor holding any older version of those objects (including one from #37, or of an object deleted and restored) gets a conflict. Editors of objects the rollback leaves alone do not.
- **The empty config** is the default document with no revision. Startup writes nothing. The first command creates revision #1, and a whole-document command on an empty database names no base revision.
- **Errors.**
  - Errors of whole-document commands point into the document as sent (a rollback: as stored).
  - Object commands report the sent object's errors relative to it (`/names/1`), and errors the change causes elsewhere (a deleted upstream still in use) as related errors pointing into the canonical document after the change, like the change list.
  - The sent object is validated last in its list, so a clash with another object is reported on it.
- **Commands that change nothing** (equal canonical config) commit nothing and return `unchanged`.
- **Audit.**
  - Commits are audited in the commit's transaction.
  - Denied, invalid, conflict and not-found commands are audited in a transaction of their own, written without the caller's cancellation once decided. A failed write is logged rather than replacing the result.
  - A publish failure after a commit is audited as `config.publish` (failed), also when it happens after the command stopped waiting.
  - Dry runs and no-ops are not audited.
- **Publishing.** Queued with the publisher under the writer lock, so snapshots go live in commit order. The command waits for the outcome outside the lock (bounded, then `pending`). A superseded publish reports the outcome of the newer publish that carries it. A failed publish is reported, logged and audited by the command whose publish it was. The commit stands, with no retry.
- **One writer.** This process is the only writer of the config database. The CLI writes identity and the vault, never config. A second server process on the same data directory is refused at startup (the file lock in [vault-and-certificates.md](vault-and-certificates.md)). If one got past it (a file system that cannot lock), its commit would fail on the unique `Seq` and parent indexes of `ConfigRevisions` rather than fork history.

## Startup, last-known-good and degraded starts

Code: `ConfigService.StartAsync` and `Paduraksa.Application.LastKnownGood`; composition in `Server/Hosting/ConfigStore` and `ConfigStartup`.

**Composition.**
- The config database (`<data dir>/paduraksa.db`), its context factory, the compiler (`ConfigCompiler` on `PublicListenerSetup.CompilerOptions`), the `LastKnownGoodFile` and the one `ConfigService` are shared instances, registered into all three hosts.
- The factory and the service come from a small private container (EF's factory and the service are meant to be resolved), which `SharedServices` owns and which alone disposes them.
- Disposal runs in reverse creation order after every host has stopped:
  1. the container (the service refuses new commands);
  2. the publisher (stops its writer and fails whatever is queued, so nothing goes live after it);
  3. the last-known-good file, created before the publisher (waits for its pending write), so the file never trails the last revision that went live.
- Certificate bindings resolve against the certificate inventory. The placeholder certificate is its fallback default (see [hosting.md](hosting.md)).

**Startup recompile.** The core host's `ConfigStartup` creates the data directory and runs `StartAsync` under the writer lock:
1. migrate (`ConfigDatabase.InitializeAsync`);
2. read the latest revision (none means the empty config);
3. compile;
4. queue the publish **without awaiting it** (YARP attaches only when the public app starts).

The outcome is logged when it arrives. A superseded startup publish (a commit's newer publish carries the config) is not a failure.

**File modes** (threat model G7).
- The config database is created 0600, and so are its `-wal` and `-shm`.
- `PrivateSqliteFile` (in Storage, shared with the vault's database) creates it. At every `ConfigDatabase.InitializeAsync` it takes group and other access away from an existing file and its WAL files and reports which it changed (a warning, logged once).
- The database holds password hashes, session and token hashes and the audit log, so it must not depend on the data directory's mode or the process umask.
- The data directory itself keeps the mode its creator gave it (the image's 0750, a bind mount's).

**Last-known-good file (`last-good.json`).**
- Shape: `{format, revision, seq, sha256, document}`. The document is the revision's canonical bytes verbatim, so the hash is the stored `DocumentHash`. References only, no secrets.
- It is offered after every successful publish (the startup one and each commit's, before the command returns) and written in the background, never under the writer lock. The newest offer wins; older ones are dropped.
- Each write goes to a new temp file with a random name (`CreateNew`, mode 0600 on Unix; leftovers of a crash are removed first, a symlink as a link), is flushed to disk and renamed over the target, which replaces a symlink there instead of following it.
- The directory is not fsynced (.NET cannot open a directory). That is safe but not durable: a power loss right after a write may leave the previous, still valid, file.
- A failed write is logged and never faults the writer.
- Reading uses one bounded read, refuses a symlink, checks format, IDs and hash, and never throws. A damaged file is logged as a warning (also on a healthy start) and replaced at the next publish.
- The empty config is not written: it is no revision.
- One data directory serves one server process at a time, enforced by a file lock (`run/server.lock`, see [vault-and-certificates.md](vault-and-certificates.md)).

**Restore.** A database without revisions next to a usable last-known-good file has lost its config (a deleted or replaced `paduraksa.db`).
- The file's document is committed as revision #1 through the normal replace path (actor System, message "Restored from last-known-good #N (rev_...)", audited), logged at Critical, and startup goes on normally.
- If that commit is rejected or fails, the start is degraded on the last-known-good config (on the empty config if that does not compile either), accepting commands.
- To start over with an empty config, delete both files.
- If the file is newer than a database that has revisions (a restored backup), it only warns: the database wins.

**Degraded starts.** The last-known-good document is compiled and published instead, logged at Critical, and `ConfigService.Status` says so (`Source = LastKnownGood`, the problem, the revision served). This happens when:
- the database cannot be opened or migrated;
- its latest revision cannot be read (corrupt document, unreadable by this build, or columns a converter rejects: a damaged ID or timestamp);
- the latest revision does not compile.

If the store itself is unusable, commands get `Unavailable` (`config.store.unavailable`, unaudited, since the audit log lives in that store) until the database is fixed or restored and the process restarted.

If only the compile failed (say, an upgrade made the compiler stricter), the database is fine. Commands are accepted and planned on the latest revision as always, and the first one that goes live ends the degraded state. Refusing them would leave no way to fix the config short of editing the database.

**A failed startup publish** (YARP rejects it or times out) leaves no live config.
- If the last-known-good file holds another revision and no command has committed since, that is published instead, degraded and accepting commands.
- Otherwise the process stays up without a snapshot, and `/readyz` says not ready.
- This is tested with a publish the publisher refuses. Every YARP setting the compiler emits is checked before YARP sees it, so no config makes YARP itself reject one.

**Readiness while degraded.** `/readyz` stays 200, with the body `degraded` instead of `ready`. The proxy serves traffic, and taking it out of rotation would turn a config-store problem into an outage. Keyword monitors can alert on the body. The cause is in the Critical log, and the admin UI shows the banner.

**Nothing to serve.** With neither a usable database revision nor a usable last-known-good document, `StartAsync` throws `ConfigStartupException` and the process exits 2 (startup failed), logged once at Critical by the host. An empty proxy that refuses changes would only look alive.

**Change to live.** Through the whole process (real SQLite, YARP and Kestrel; one upstream and one host, two commits, until the first request for the new host comes back): median 11 to 15 ms on a dev machine with the rest of the suite running alongside.

## How the Application layer is organised

An external review of the Application layer led to these shapes. Each finding was checked before it was acted on.

- **One writer, one process.**
  - `ConfigService` has no protocol for a second writer, because the CLI never writes config. There is no re-plan path for a commit that finds the head moved.
  - What stays is what one process needs: the writer lock, the unique revision indexes as the backstop, and the whole recovery set (last-known-good, restore of an empty database, degraded starts, the fallback after a failed startup publish). Each of those happens with one process: a deleted database, a corrupt revision, a stricter compiler, a YARP refusal.
- **`ConfigService` is split by responsibility, not by partial.**
  - `ConfigService` is the public surface and the writer lock.
  - `ConfigPipeline` runs a command's steps: authorize, plan, validate, compile, guard, commit.
  - `ConfigPublishing` carries a commit to live: queue, wait, last-known-good, warnings, validity timer, store status.
  - `ConfigRecovery` starts the store and republishes the live config.
  - `ConfigDrafts` serves the request simulator.
  - `ConfigCommandPlanner`, `AdminLockoutGuard` and `CertificateBindingGuard` are pure functions of documents, tested on documents alone.
  - `ConfigAudit` is how the writer records audit events.
- **No config projection.** The `DropConfigProjection` migration removed the seven tables (`GlobalSettings`, `Profiles`, `Upstreams`, `Destinations`, `Hosts`, `HostNames`, `Routes`) that mirrored the document. A test migrates a database created at the previous migration and keeps its revisions.
- **One authorization check.** `IPermissionEvaluator` is the one check and is required everywhere. There are no typed authorizers with allow-all defaults.
  - Admin keeps its minimal-API filters (`RequirePermission`), with the existing conventions that every caller requirement has its filter and every route its expected permission. The dashboard's permission sets come from the same evaluator.
  - A host-level test pins that the issuance services get the evaluator: they were once built in a container that never registered it, which silently turned their own permission checks off in production.
- **No optional constructor parameters in Application.** Production dependencies are required. A nullable type is used only where null is a state of the world: no built-in certificates, no external account binding, no DNS provider. `ApplicationConstructorConventionTests` fails when an optional parameter comes back. Tests get defaults from shared builders (`TestConfigServices`, `TestPermissions`, the issuance harness).
- **Kept as it is.** The recovery cases, the commit-time unique-index backstop, and Admin's attribute-free filters (a decision of the owner). The dashboard hides what the user may not do from its own permission set, the evaluator's answer at the last re-check. The services refuse regardless.
