# Testing and quality

For contributors: what each kind of test covers, how the suites are shaped, and what the property tests and fuzzers found. For the commands, see [development.md](../development.md).

## In short

- Five levels: unit, integration (real Kestrel, real SQLite, a Pebble ACME server), architecture rules, performance, and security checks.
- Architecture tests (ArchUnitNET) enforce the project dependency graph, the typed-ID and DTO rules, internal-only vault types and the hot-path namespace bans.
- Every gate and response path has a 0 B allocation assertion. Benchmarks and a one-hour soak guard the rest of the budget.
- Property tests (CsCheck) check the parsers, matchers and the config pipeline against slow, obvious models. Seeds are derived, so a run is the same every time.
- Sixteen coverage-guided fuzz targets (SharpFuzz and libFuzzer) run nightly. Committed corpora replay on every commit.
- CI requires the capabilities a test needs (Chromium, libmsquic, Docker for Pebble) instead of skipping without them.
- A sleep in a test must be one of a few allowed kinds. The rule is in [development.md](../development.md).
- Code: `tests/`, `fuzz/`, `bench/`, `tests/Shared/Property.cs`, `tests/Shared/Requirements.cs`.

## Contents

- [Test levels](#test-levels)
- [Settings and response-header tables](#settings-and-response-header-tables)
- [Integration tests](#integration-tests)
- [What CI runs, and sleeping in tests](#what-ci-runs-and-sleeping-in-tests)
- [Architecture tests](#architecture-tests)
- [Performance](#performance)
- [Soak test](#soak-test)
- [Security](#security)
- [Property tests](#property-tests)
- [Fuzzing](#fuzzing)

## Test levels

- **Unit:** policies (table-driven), the compiler (config to snapshot golden files), typed IDs (fuzz parse and format round-trip), YAML round-trip, diff.
- **Integration:** real Kestrel on ephemeral ports, real SQLite in a temp dir, a validating **Pebble** container for ACME (HTTP-01 through the gate, plus **pebble-challtestsrv** with the DNS providers), and a fake DNS provider. `HttpClient` with H2 and H3 verifies SNI and ALPN.
- **Architecture:** dependency rules, DTO rules and typed-ID rules.
- **Performance:** BenchmarkDotNet and a load harness.
- **Security:** the threat model, dependency scanning, CodeQL, fuzzing and an audit-chain verifier.

## Settings and response-header tables

Both are table-driven.

- **`SettingsInheritanceTests`** has one row per `HostSettings` field (nested records included). It checks the field at every layer, overridden at each level and at its built-in default, through the real merge, validator and compiler. Its completeness test walks the contract, so a new field fails until it has its row.
- **`ResponseHeaderMatrixTests`** is one table of answer kind x scheme x connection x name x each header's mode. It asserts the exact header set a response carries, the compiled list, YARP's transform after a copy, the compiler's refusals and the 0 B assertions.
- One end-to-end test per header stays in `Tests.Integration` (`PublicHstsTests`, `PublicServerHeaderTests`, `PublicHttp3Tests`), with the special paths (502, WebSocket) beside them.

## Integration tests

**One host per class.** A host is expensive to build and to stop: vault, config store, three listeners, the local CA and the admin leaf. A test class whose tests only read, or make their own uniquely named resources, starts **one host for the whole class** through an xUnit class fixture. The fixtures are in `tests/Paduraksa.Tests.Integration/Hosting/SharedFixtures.cs`: `SharedHost`, `SharedOwnerSession`, `SharedAdminProxy`, `SharedAdminApi`, `SharedSmallBodyHost`, `SharedLoopbackHost`.

A test keeps a host of its own when it:
- changes global settings, users or the vault;
- restarts, fails or stops the host;
- counts something global.

A test that only needs an account of its own (a wrong password that counts toward a lockout, a session it ends, a second factor it enrols) creates a user instead. IDs, names and paths are per test, so a shared host leaves no order dependence.

**Two test-only seams** keep start and stop cheap without weakening a claim. Production never sets either.
- A test host hashes passwords at the validator's floor (100 000 iterations).
- `SqlitePragmas.BusyTimeout` is settable once, before the first connection opens. The Data suite runs the two busy-timeout tests at one second, with Microsoft.Data.Sqlite's own retry window pinned at its 30-second default, so only the busy handler can be what fails the write.

A host's stop does not wait for the log writer's tick: `RotatingLogFile.Flush` wakes the writer at once ([observability.md](observability.md)).

**Browser tests** share one console collector (`PlaywrightBrowser.CollectProblems`).
- A Content Security Policy violation or a console error fails the test. Exceptions: a failure the test asks for, and Blazor's own "Connection disconnected" report, which is a circuit reconnect under load and not an app error.
- Pages are waited on by signal (the shell's `data-circuit=live`, a finite animation finishing), not by delay.

**The two 0 B assertions** (`MacKeyTests`, `AllocationTests`) measure after warm-up on a thread of their own and take the smallest of three rounds. A one-off allocation on that thread can't fail them, and a real allocation still does.

## What CI runs, and sleeping in tests

**Skips must not hide a gap.** The Playwright tests skip when no Chromium is found, the QUIC tests when libmsquic is missing, and the Pebble tests when Docker can't pull the image. CI once had neither of the first two, so 19 browser tests and 19 QUIC tests skipped on every run.

- `.github/workflows/ci.yml` installs Playwright's own Chromium for the pinned package version (`playwright.ps1 install --with-deps chromium`, cached by version, because Ubuntu's `chromium` is a snap) and libmsquic at the version the Dockerfile pins. The Test step sets `PADURAKSA_REQUIRE=playwright,quic,pebble`.
- `tests/Shared/Requirements.cs` reads it. `Requirements.SkipOrFail` is what the three skip sites call (`PlaywrightBrowser.LaunchOrSkipAsync`, the two QUIC `Require` helpers, the Pebble fixtures' `SkipReason`). If a named capability is missing, the test fails with the reason. If it isn't named, the test skips.
- `run-quic-tests.sh` stays for machines without libmsquic (it sets `PADURAKSA_REQUIRE=quic`). CI covers the QUIC tests in the normal run.
- The build-test job's timeout is 30 minutes.

**Sleeping in tests.** Where a sleep stands in for a signal or a fake clock, use the signal. Each remaining `Task.Delay` or `Thread.Sleep` carries a one-line comment naming which of the allowed kinds it is. The kinds, and what to do instead, are in [development.md](../development.md#writing-tests-that-dont-flake).

## Architecture tests

`tests/Paduraksa.Tests.Architecture`, built on **ArchUnitNET** (maintained; NetArchTest is effectively unmaintained), plus plain reflection tests for the ID and DTO rules.

They check:
- the dependency graph (the layer rules in [the plan](../PLAN.md));
- no `Data` types in `Contracts`, `Admin` or `Dashboard`;
- no untyped `*Id` properties;
- Vault types internal;
- hot-path namespace bans: `System.Linq`, `Regex` with backtracking, `DbContext`.

## Performance

- BenchmarkDotNet for each policy and host lookup, with `[MemoryDiagnoser]` asserting 0 B.
- A nightly `oha` load test comparing p50, p99 and RPS against the previous run and nginx.
- The S7 harness covers nginx, Caddy, HAProxy, Traefik, Envoy and bare YARP, with a recorded history (`bench/RESULTS.md`).
- The soak test (below) shows that a running server stays healthy over time.

## Soak test

`bench/soak/run-soak.sh` runs one real Paduraksa for an hour (`--quick`: five minutes) and judges it. Method, thresholds and their reasons are in `bench/soak/README.md`.

**Shape.**
- **The server** is the Release build of `src/Paduraksa.Server` started as `dotnet paduraksa.dll`, with settings in environment variables, pinned to four CPUs. Not the container image: it adds Docker and libmsquic and changes no finding. A startup hook (`bench/Paduraksa.Bench.Soak.Probe`, `DOTNET_STARTUP_HOOKS`) reports the process's GC and thread-pool figures, which neither `/proc` nor `/metrics` has.
- **The driver** (`bench/Paduraksa.Bench.Soak`) does first-run setup and a YAML import of a 51-host config, then runs the load and the changes and samples every 10 s. It writes CSV and facts, and `bench/soak/analyze.py` judges them, with every threshold in one table.
- **The config** has seven upstreams of two or three destinations, two profiles, the access log, rate limits, an access list, a rule set, custom error pages, forward auth, proof of work, HSTS, custom headers, compression and a strip-prefix route.
- **The load** is open-loop and fixed-rate: HTTP/1.1, HTTP/2 (HTTP/3 with libmsquic), downloads and uploads, a 429 client, unknown hosts, 300 WebSockets that chat slowly, five silent ones, slowloris and slow-body clients.
- **The changes:** a publish every 10 s (a host, an upstream, a rate-limit policy, an access list), a burst of one every 100 ms for 60 s, a destination that stops for 60 s, admin API polling, two dashboard tabs in headless Chromium, an MCP call a minute and a graceful restart halfway.
- **The checks:** no unexpected error, no leak (slopes over the post-warm-up stretches), no latency drift (the last 10 minutes against minutes 5 to 15), publishes all succeed and stay fast, S6 (429s and counters through the burst, a down destination stays down), the flap and the restart.

**Decisions.**
- **Fixed rate, not saturation.** The default is 4 000 requests a second, what the .NET generator holds, about 40 % of the server's four CPUs. The bench's `oha` pushes ten times that, and at that rate the generator is the limit, not the server.
- **Errors are tolerated only where the test causes them:** the restart (stop signal to ready, plus 15 s) and, on the flap hosts, the flap's window.
- **The leak fit is a Theil-Sen slope** over each process's post-warm-up stretch, so one GC or hiccup does not move it. Stretches under 10 minutes are skipped, so `--quick` reports none.
- **One client IP.** Everything is loopback, so the per-IP limits see one client, and the connection cap and the deny list are set high.

**What it found** (two 60-minute runs, `bench/soak/results/2026-11-14-full*`):
- A rate-limit bucket was evicted by the same client's buckets on other hosts, and an evicted bucket starts full: past eight rate-limited hosts a 20/s limit let 88/s through. **Fixed** in `RateLimitTable` (the probe start mixes in the slot).
- A restart with WebSockets open waited out the whole 30 s `ShutdownTimeout` and lost the analytics and metrics in memory. **Fixed:** upgraded connections get a 1001 close as the drain starts, and core has a budget of its own ([hosting.md](hosting.md#shutdown)).
- A destination that came back after a flap stayed out for a minute: YARP's passive window holds the outage's failures, and its 60 s reactivation ignores the upstream's configured period. **Fixed** by `PassiveFailureRatePolicy` ("Passive health" in [observability.md](observability.md)), with tests that fail on YARP's policy.
- A slow request body in flight at the stop held the drain for its whole budget, because Kestrel disposes the heartbeat that enforces the data rates. **Fixed** by `KestrelDrainHeartbeat` ([hosting.md](hosting.md#shutdown)).
- About 15 HTTP/2 requests an hour (of 14.9 million) got no response, while the proxy logged them as completed. A Kestrel bug: a pooled HTTP/2 stream can be left unable to send ([dotnet/aspnetcore#69690](https://github.com/dotnet/aspnetcore/pull/69690), not yet in 10.0). **Worked around** in `KestrelHttp2StreamReuse`, with a test that reproduces it every time ([hot-path.md](hot-path.md#kestrels-lost-http2-responses)).
- RSS rose in steps while the managed heap stayed flat, and one hour could not tell a plateau from a leak. The two-hour runs before the tag show it levelling off after a 30-minute climb (`bench/soak/README.md`).

## Security

Threat model doc, dependency scanning, CodeQL, fuzzing of the ACME, JSON, YAML and multipart parsers, and an audit-chain verifier.

### CodeQL and the NuGet audit (threat model G9)

- `.github/workflows/codeql.yml` analyses the C# product code on pushes to main, pull requests and weekly. It is a manual build of `Paduraksa.Server`, which builds every product project and no test, benchmark or fuzz project. It uses the `security-extended` queries.
- `Directory.Build.props` sets `NuGetAudit` on with `NuGetAuditMode=all` (transitive packages too) and `NuGetAuditLevel=moderate`.
- **Decision.** The repository builds with warnings as errors, which would make every advisory fail restore, so the choice is explicit:
  - high and critical (NU1903, NU1904) are errors;
  - a **moderate** one (NU1902) stays a warning in the restore log. A moderate advisory that has no fixed release yet would otherwise stop every build, the Dependabot pull request that updates the package included;
  - a low one (NU1901) is not reported.

## Property tests

The per-commit part is cheap, and the deep part is manual or nightly.

**Tooling.** **CsCheck 5.0.0**, run in `dotnet test`. `tests/Shared/Property.cs` is the one entry point.
- CsCheck seeds only a run's first case and draws the rest at random. So each case's seed is derived from a fixed base, the property's name and the case's number. A run is the same every time, and adding a test moves no other test's cases.
- A failing case is run on from its seed with 5,000 more iterations to shrink (CsCheck keeps the smallest failure it meets).
- A failure prints the shrunk input and the seed that reproduces it, and becomes a plain regression test.
- Case counts are set per test. `PADURAKSA_PROPERTY_SCALE` multiplies all of them, and `PADURAKSA_PROPERTY_SEED` moves the base. Example: `PADURAKSA_PROPERTY_SCALE=50 dotnet test --project tests/Paduraksa.Tests.Proxy --filter-class "*PropertyTests"`.
- The defaults add about 40 s of CPU to the suite (about 25 s of wall time on four cores). The nightly workflow runs them 50 times deeper with a seed of the day.

Each property is checked against a slow, obvious model.

**`Paduraksa.Tests.Proxy`:**
- `RoutePathPropertyTests`: the canonical matching path against a plain split-and-resolve model, its idempotence, the stack and the pooled buffer alike, and 150 raw request targets over a socket to a bare Kestrel.
- `GlobPatternPropertyTests`: the automaton against a backtracking regex of the same rules, on any path and on paths built from the pattern.
- `CompiledRuleSetPropertyTests`: indexed exact and prefix paths, scanned globs and regexes against "the first rule whose conditions all hold", with non-ASCII case folding in the user agent.
- `AccessListPropertyTests`: allow, deny, countries, `satisfy`, drop and passwords through the real gate in Cloudflare mode against the table written out.
- `SimulatorParityTests`: the fixed tables, widened to 2,100 random requests across the unknown-host behaviours and trusted-proxy modes.
- `ForwardAuthCachePropertyTests`: the decision cache against a model that remembers an allow under everything the provider is sent, at the grain the answer says.
- `ForwardAuthHeaderPropertyTests`: the owned names and the stripping against a model with `_` and `-` as one character.
- `AccessLogFormatterPropertyTests`: every line read back by a plain reader of the grammar finds exactly 13 fields, quoted ones decode to what was sent, nothing a reader may take for a line end survives, and a sensitive query value never appears.

**`Paduraksa.Tests.Unit`:**
- `ConfigGen` generates whole config documents: every list, every settings layer, references that mostly resolve, values that are mostly valid, error pages at every layer in both modes, maintenance with a bypass access list, and `replaceUpstream`. The pipeline property fails if too few valid documents reach them.
- `ConfigPipelinePropertyTests`: the validator never throws, a document that validates never fails to compile, its canonical JSON is idempotent and round-trips, and the same config written differently has the same canonical bytes.
- `SimulatedBehaviourTests`: for any generated valid document and request, the simulator says what the real gate answers, including that `errorPage` is the operator's page exactly when the gate serves bytes equal to the preview compiler's rendering.

### What the property tests found

1. The simulator decoded `%5C` in a path to `%5c`, but Kestrel hands over a backslash (found by the differential against real Kestrel). The trace's decoded path was wrong, not the matching.
2. The forward-auth decision cache key left out the scheme and the standard hint headers the provider is sent (`User-Agent`, `Accept`, `Accept-Language`, `X-Requested-With`), so an allow was served to a request that differs in them.
3. The access-log formatter left NEL, the other C1 controls and U+2028 and U+2029 unescaped, and a reader that splits on a Unicode line end takes them for a line end.
4. A custom `Server` header that the global defaults leave without a value validated but could not compile (a document with no host).

One property was wrong, not the code: `RoutePath.IsCanonical` answers false for any path with a `%`, canonical or not, because it is only a fast path.

Each property and harness was shown to fail by breaking the code it checks.

### Gaps

- The ACME response parsing has no harness yet.
- The fuzzers are not shown a real network: HTTP framing is Kestrel's, and the response body's streaming is not driven.
- The simulator parity property leaves out hosts Kestrel would answer 400 itself (the socket parity tests pin that rule), and feeds the real gate already-decoded paths (the Kestrel differential covers the decoding).
- The rate-limit table, the proof-of-work page and the gate's allocation have no property test.
- Coverage is the proxy's and Core's assemblies only. YARP and the framework are not instrumented.
- The harnesses build and run on Linux x64.

## Fuzzing

**Tooling.** **SharpFuzz 2.3.0** (the pinned `sharpfuzz` tool instruments the proxy's assemblies in a published copy) with a pinned release of **libfuzzer-dotnet** (v2025.05.02.0904, checked against its digest).
- `fuzz/Paduraksa.Fuzz.Targets` holds the targets, and `fuzz/Paduraksa.Fuzz` the libFuzzer driver and the `list`, `replay` and `seed` modes. Both are in the solution, so CI builds them.
- Every target also checks the same verdict for the same bytes, and 0 B (an allocation counts when two runs of the same input both make it).
- Instrumented code may only run once SharpFuzz has set up its shared memory, so the driver finds the target inside the first run.

### Targets

| Target | What it checks |
|---|---|
| `proxy-protocol` | v1 and v2, in one segment and two. A header that parses is its `consumed` bytes, and no shorter prefix of it is anything but incomplete. |
| `pow-cookie` | The cookie scan and the token check. A token that verifies is the one canonical encoding of what it says. |
| `pow-form` | The posted form, the challenge, the solution against an independent SHA-256 count, and the return target, which can never leave the site. |
| `basic-auth` | Against an independent base64 decode. |
| `client-address` | `CF-Connecting-IP`, `X-Forwarded-For`, `True-Client-IP` and `CF-IPCountry` through the resolver in every trusted-proxy mode. An untrusted peer is never moved by a header and gets no country. An address taken from a header is the one `IPAddress` reads. |
| `forward-auth-answer` | The provider is a handler that answers with the input, so the whole gate runs with no socket. The answer is compared with a plain reading of the rules. A log line that names an exception is the crash, since the policy turns one into a 502. |
| `error-page` | Pages that use every variable, as a message and as a document, with hostile `Host`, `X-Request-Id`, `CF-Ray` and `Accept`. The page is the template with each variable HTML-encoded and cut as the rules say (host at 253 characters without its port, ray at 64 and empty unless trusted, request ID the client's only from a trusted peer and 1 to 128 visible ASCII characters). No raw `<`, `>`, `"`, `'`, stray `&` or non-ASCII byte. `HEAD` has no body, and HTML is chosen exactly when an independent reading of `Accept` says so. |
| `upstream-error-replacement` | `ReplaceUpstreamErrorTransform.Replaces(mode, status, mediaType, upgrade, started)`, the pure decision the transform calls, against a plain model: never for an upgrade, an event stream, `application/grpc*` or a started response, `html` only for `text/html` in any case and with parameters, only for a status that has a page. |
| `access-log-tail` | A hostile request through `AccessLogFormatter` and the tail entry built as the drain builds it. A plain reader finds thirteen fields and no line end, every tail field equals the file line's, a sentinel in a sensitive query parameter appears in neither, and the line costs 0 B. |
| `mcp-sandbox` | The parent of the MCP sandbox against a child that is not to be trusted (below). |
| `yaml-import` | The YAML reader and importer (below). |
| `ip-network` | `IpNetwork.TryParse` against an independent reading of its three rules. A parsed block spans exactly 2^(128-prefix) addresses, contains its ends and not the addresses beside them, and its canonical text parses back to the same block. |
| `glob-pattern` | A pattern compiles exactly when it is ASCII, starts with a slash and fits the token limit. A match equals a memoised recursion written from the rules, the case of the path never matters, and a 64 KiB path takes well under a second. |
| `cloudflare-ranges` | The real `CloudflareRangeFetcher` over a handler that answers with the input. A list is accepted exactly when an independent reading of the list rules accepts the first source that gives one. A refusal is one short printable line. |
| `backup-archive` | The header and chunk reader up to the passphrase check, under a fixed key (the Argon2 derivation is not run, because a header may ask for up to a gigabyte). Only `BackupException` escapes, with the reason a second reading of the rules gives. |
| `certificate-import` | The vault's `ICertificateImport` over a scratch directory. Only `CertificateImportException` escapes, the same upload gives the same verdict, an import takes under 30 s and 512 MiB, a refusal never repeats the password, and the public chain holds no key. |

`Paduraksa.Vault` is instrumented by `scripts/fuzz.sh` and lets `Paduraksa.Fuzz.Targets` see its internals (`InternalsVisibleTo`, like `Paduraksa.Tests.Vault`). The targets project is a key-material boundary project (`PaduraksaKeyMaterialBoundary`), because the backup target decrypts with `AesGcm` and the import target builds seed certificates. No other target uses either.

### Corpora and crashes

- `fuzz/corpus/<target>/` is committed. The `seed-*` files are well-formed starting points written by `Paduraksa.Fuzz seed`. The rest is what runs found, minimised by `scripts/fuzz.sh --merge`.
- `fuzz/regressions/<target>/` holds inputs that once crashed a target.
- `.gitattributes` marks both binary (a PROXY header ends in CR LF).
- `tests/Paduraksa.Tests.Fuzz` replays every committed input through its target on every commit, with no libFuzzer and no instrumented assembly, so a corpus regression fails CI.
- A crash is copied into `regressions/`, fixed, and gets a unit test beside the code.
- `scripts/fuzz.sh [--time S] [--target T] [--merge] [--replay] [--list]` publishes the harness, instruments, runs libFuzzer per target for the time box, and fails on a crash or an error.
- `.github/workflows/fuzz.yml` (`workflow_dispatch` and a nightly schedule, never push or pull request) runs every target (15 minutes each by default) and the property tests 50 times deeper. It uploads crash inputs, logs and the grown corpus. It runs the 16 targets one after another, so its timeout is 300 minutes, and `FuzzWorkflowTests` fails when a target is added without raising it.
- The release run is `scripts/fuzz.sh --time 3600 --merge`. The one before v0.1 was two parallel hours on each target.

### The `mcp-sandbox` target

**Decision.** The harness does not fuzz the scripts in process: a stock engine in the same process as the fuzzer crashes and hangs on the very scripts the boundary exists to absorb, which is noise, not a finding. A child per input is too slow (a start is about 180 ms). So the first input byte picks one of two modes.

**Frames** (three inputs in four: in process, instrumented, fast). The rest of the input is what a hostile child writes to its stdout, fed to the parent's run protocol (`RunProtocol.ExchangeAsync`) over a stub dispatcher and the real request policy ([admin-surfaces.md](admin-surfaces.md)). It carries a known run id that the seeds carry, so the fuzzer can mutate it into a wrong, a missing or an old one: no frame that is not an answer to the run in progress may be served. The call may return an outcome, or end as a breach or a protocol violation, and nothing else may happen:
- no other exception;
- a result within the output cap, an error of at most 700 characters without control characters, a bounded note;
- at most the request limit dispatched;
- every dispatched request one the policy allows (a plain path under `/api/v1`, a bounded query, body and entity tag, a write only with the bytes saying `confirm` or as a dry run);
- everything the parent wrote back a `response` frame.

**Scripts** (one in four, when the harness can start a child). `scripts/fuzz.sh` publishes an uninstrumented copy and sets `PADURAKSA_FUZZ_CHILD`. The rest of the input is a script, run on a real child with 1 s of wall and CPU time. The properties are the parent's: it never throws, the call ends within its wall limit plus a margin, the answer is bounded, and the pool holds at most one child.

Seeds: 23 script seeds (the scripts that were once dangerous in process, plus ordinary ones) and 21 frame seeds (every shape of violation). Without a child, a script input is read as frames, so the corpus replays in CI without one.

### The `yaml-import` target

`fuzz/Paduraksa.Fuzz.Targets/YamlImportTarget.cs`. The input is YAML bytes, read through the guarded reader, mapped onto the document and merged, with no database, twice: replacing the empty config, and merging into a small stored one (so identity matching and names that resolve against stored objects are reached). `scripts/fuzz.sh` instruments `Paduraksa.Application`, `Paduraksa.Contracts` and `YamlDotNet` as well as Proxy and Core.

**Properties.**
- No exception. Only a result with errors, each a JSON-pointer path, a code and a message that names no CLR or parser type, under 800 characters.
- The same bytes give the same result, the new IDs included (a counter supplies them).
- **The caps hold.** The reader under a tight set of limits (6 levels, 400 nodes, 64-character scalars, 16-character keys, 4 anchors, 8 aliases) returns a tree that, counted again, is no deeper or larger and has no longer scalar or repeated key, whatever aliases and anchors the input uses. Under the real limits an import of up to 4 KiB allocates under 192 MiB and runs under 8 s.
- An accepted document that validates is exported, read back and is the same canonical bytes (versions aside), with no `password` key in the export. A document with a route ID used by two hosts maps but cannot be read back, because the import refuses repeated IDs, so only that it can be written is checked.

There are 24 seeds: well-formed configs, a flow-style one, an export, a billion-laughs document, deep nesting, tags, two documents, a merge key, a BOM, invalid UTF-8.

### What fuzzing found

Each find has its input in `fuzz/regressions/<target>/` and a unit test beside the code, unless noted.
- **`mcp-sandbox`:** a frame with invalid UTF-8 inside a string reached `JsonElement.GetString`, which throws `InvalidOperationException`. The production loop would have treated it as a broken child, but by accident. Frames are checked for UTF-8 first now, and text with a lone surrogate is a violation.
- **`access-log-tail`:** `AccessLogFormatOptions.IsSensitive` enumerated its key list through `IReadOnlyList<string>`, which boxes an enumerator, so every query parameter of every access-log line allocated 32 B on the drain thread. It indexes the list now (`AccessLogFormatterTests.ALineWithAQueryAllocatesNothing`).
- **`yaml-import`:** the reader's error for an unknown alias quoted the alias's name, which the input makes as long as it likes. It is cut at 40 characters now. The unit tests found two more: the emitter misjudges some mixes of YAML indicators and quotes, so the writer asks for a plain scalar only for text of letters, digits and a few joiners and quotes everything else itself, and the scanner throws `InvalidOperationException` for an unterminated flow collection followed by more lines. The property over `ConfigGen` found that an empty `passiveHealth` is not the same as none, and that a valid document can name an object that is gone in a layer nothing uses (both in [admin-surfaces.md](admin-surfaces.md)).
- **`cloudflare-ranges`:** a refused range list put the response's text in its reason as it came, so an escape sequence or a forged log line reached the log and the dashboard. Control, format and line-separating characters are `?` now. Invalid UTF-8 in a string of the JSON answer threw `InvalidOperationException` out of `CloudflareRangeFetcher`, so the text endpoints were never asked. It is a failed source like any other now.
- **`certificate-import`:** a PKCS#8 key on a curve the platform doesn't have escaped as `PlatformNotSupportedException`, a 500 from the admin API. It is `Unreadable` now (`CertificateImportHardeningTests`).

The other targets found no product bug. Harness bugs were fixed in the harness: a header name that is not a token (which a transport never delivers) and repeated `Location` lines compared as one joined string in `forward-auth-answer`, and `HttpClient`'s forgiving reading of media types in `upstream-error-replacement`.

Each target is also shown to fail when the code it checks is broken on purpose.

**Left out.** There is no target for the dashboard's own parsing, the live tail's SSE framing or the notification senders' payloads (a sender builds its request from the operator's own text, not from a client's).
