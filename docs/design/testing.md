# Testing and quality

Paduraksa tests at five levels: unit tests, integration tests against real servers and databases, architecture rules, performance benchmarks, and security checks (threat model, scanning, property tests and fuzzing). This page describes what each level covers and how the suites are shaped. For the commands, see `docs/development.md`.

Contents:
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

Both are table-driven, since the settings refactor.

- **`SettingsInheritanceTests`** holds one row per `HostSettings` field (nested records included). It checks the field at every layer, overridden at each level and at its built-in default, through the real merge, validator and compiler. Its completeness test walks the contract, so a new field fails until it has its row.
- **`ResponseHeaderMatrixTests`** is one table of answer kind x scheme x connection x name x each header's mode. It asserts the exact header set a response carries, plus the compiled list, YARP's transform after a copy, the refusals the compiler makes and the 0 B assertions.
- One end-to-end test per header stays in `Tests.Integration` (`PublicHstsTests`, `PublicServerHeaderTests`, `PublicHttp3Tests`), with the special paths (502, WebSocket) beside them.

## Integration tests

**The shape (v0.20 refactor).** A host is expensive to build and to stop: vault, config store, three listeners, the local CA and the admin leaf. So a test class whose tests only read, or whose tests make their own uniquely named resources, starts **one host for the whole class** through an xUnit class fixture. The fixtures are in `tests/Paduraksa.Tests.Integration/Hosting/SharedFixtures.cs`: `SharedHost`, `SharedOwnerSession`, `SharedAdminProxy`, `SharedAdminApi`, `SharedSmallBodyHost`, `SharedLoopbackHost`.

When a test keeps a host of its own:
- it changes global settings, users or the vault;
- it restarts, fails or stops the host;
- it counts something global.

A test that only needs an account of its own (a wrong password that counts toward a lockout, a session it ends, a second factor it enrols) creates a user instead of a host. IDs, names and paths are per test, so a shared host leaves no order dependence.

**Two test-only seams** keep start and stop cheap without weakening a claim:
- A test host hashes passwords at the validator's floor (100 000 iterations).
- `SqlitePragmas.BusyTimeout` is settable once, before the first connection opens. The Data suite runs the two busy-timeout tests at one second, with Microsoft.Data.Sqlite's own retry window pinned at its 30-second default, so only the busy handler can be what fails the write.

Production never sets either. A host's stop no longer waits for the log writer's tick either: `RotatingLogFile.Flush` wakes the writer at once (see [observability.md](observability.md)).

**Browser tests** share one console collector (`PlaywrightBrowser.CollectProblems`).
- A Content Security Policy violation or a console error fails the test. Exceptions: a failure the test asks for, and Blazor's own "Connection disconnected" report, which is a circuit reconnect under load and not an app error.
- Pages are waited on by signal (the shell's `data-circuit=live`, a finite animation finishing) rather than by delay.

**The two 0 B assertions** (`MacKeyTests`, `AllocationTests`) measure after warm-up on a thread of their own and take the smallest of three rounds. A one-off on that thread cannot fail them, while a real allocation still does.

## What CI runs, and sleeping in tests

**CI ran less than it claimed.** The Playwright tests skip when no Chromium is found, the QUIC tests when libmsquic is missing, and the Pebble tests when Docker can't pull the image. CI had none of the first two, so 19 browser tests and 19 QUIC tests skipped on every run (the last green run before the change: 8 069 passed, 45 not executed).

**As built.**
- `.github/workflows/ci.yml` installs Playwright's own Chromium for the pinned package version (`playwright.ps1 install --with-deps chromium`, browser download cached by version; Ubuntu's `chromium` is a snap) and libmsquic at the version the Dockerfile pins, from Microsoft's package feed. The Test step sets `PADURAKSA_REQUIRE=playwright,quic,pebble`.
- `tests/Shared/Requirements.cs` reads it: `Requirements.SkipOrFail` is what the three skip sites call (`PlaywrightBrowser.LaunchOrSkipAsync`, the two QUIC `Require` helpers, the Pebble fixtures' `SkipReason`). A named capability that is missing fails the test with the reason; unnamed, it skips as before. The older `PADURAKSA_REQUIRE_QUIC=1` is gone; `run-quic-tests.sh` sets `PADURAKSA_REQUIRE=quic`.
- `run-quic-tests.sh` stays: it is the way to run the QUIC tests on a machine without libmsquic (the dev box). CI covers them in the normal run now.
- The build-test job's timeout is 30 minutes (it was 20 for a 10-minute run).
- Verified locally: the helper's list parsing, and a run with `PADURAKSA_REQUIRE=playwright` and no Chromium fails with the reason. Only GitHub can show that the installs work on the runner.

**The sleeps.** There were 110 `Task.Delay` and `Thread.Sleep` lines in the tests. Nine calls became signals or a fake clock: the verification queue's two deadlines (a manual clock, no 1 s sleep), the sandbox caller-gone test (the dispatcher's first call), the notification outbox tests (the upkeep timer being armed, and the failing factory's own count of refusals), a flusher baseline that needed no wait, and the security page's hidden tab (the renderer's own queue). The 100 calls left each carry a one-line comment saying which of the allowed kinds they are (the rule is in `docs/development.md`, "Writing tests that don't flake"): 52 poll steps of bounded waits, 16 slow or hung peers in tests about slowness, 14 absence windows (nothing can signal that something did not happen; each says what would show in it), 7 pacings of a concurrent loop, 3 soak durations, 4 steps of the fake-clock pump, 2 real-time gaps of a real host's timers (`UpstreamHealthNotificationTests`: the health hold) and 2 bounds on a signal wait.

## Architecture tests

`tests/Paduraksa.Tests.Architecture`, built on **ArchUnitNET** (maintained; NetArchTest is effectively unmaintained), plus plain reflection tests for the ID and DTO rules.

They check:
- the dependency graph (see the layer rules in [../PLAN.md](../PLAN.md));
- no `Data` types in `Contracts`, `Admin` or `Dashboard`;
- no untyped `*Id` properties;
- Vault types internal;
- hot-path namespace bans: `System.Linq`, `Regex` with backtracking, `DbContext`.

## Performance

- BenchmarkDotNet for each policy and host lookup, with `[MemoryDiagnoser]` asserting 0 B.
- A nightly `oha` load test comparing p50 and p99 and RPS against the previous run and nginx.
- The S7 harness now covers nginx, Caddy, HAProxy, Traefik, Envoy and bare YARP, with a recorded history (`bench/RESULTS.md`).
- The soak test (below) shows that a running server stays healthy over time.

## Soak test

Built on `soak`: `bench/soak/run-soak.sh` runs one real Paduraksa for an hour (`--quick`: five minutes) and judges it. Method, thresholds and their reasons are in `bench/soak/README.md`; this is the summary.

**As built.**
- **The server** is the Release build of `src/Paduraksa.Server` started as `dotnet paduraksa.dll`, with settings in environment variables, pinned to four CPUs. Not the container image: it adds Docker and libmsquic and changes no finding. A startup hook (`bench/Paduraksa.Bench.Soak.Probe`, `DOTNET_STARTUP_HOOKS`) reports the process's GC and thread-pool figures, which neither `/proc` nor `/metrics` has.
- **The driver** (`bench/Paduraksa.Bench.Soak`) does first-run setup and a YAML import of a 51-host config, then runs the load and the changes and samples every 10 s. It writes CSV and facts; `bench/soak/analyze.py` judges them, with every threshold in one table.
- **The config** has seven upstreams of two or three destinations, two profiles, the access log, rate limits, an access list, a rule set, custom error pages, forward auth, proof of work, HSTS, custom headers, compression and a strip-prefix route.
- **The load** is open-loop and fixed-rate: HTTP/1.1, HTTP/2 (HTTP/3 with libmsquic), downloads and uploads, a 429 client, unknown hosts, 300 WebSockets that chat slowly, five silent ones, slowloris and slow-body clients.
- **The changes** are a publish every 10 s (a host, an upstream, a rate-limit policy, an access list), a burst of one every 100 ms for 60 s, a destination that stops for 60 s, admin API polling, two dashboard tabs in headless Chromium, an MCP call a minute and a graceful restart halfway.
- **The checks:** no unexpected error, no leak (slopes over the post-warm-up stretches), no latency drift (the last 10 minutes against minutes 5 to 15), publishes all succeed and stay fast, S6 (429s and counters through the burst, a down destination stays down), the flap and the restart.

**Decisions.**
- **Fixed rate, not saturation.** The default is 4 000 requests a second, what the .NET generator holds, about 40 % of the server's four CPUs. The bench's `oha` pushes ten times that; at that rate this generator is the limit, not the server.
- **Errors are tolerated only where the test causes them:** the restart (stop signal to ready, plus 15 s) and, on the flap hosts, the flap's window.
- **The leak fit is a Theil-Sen slope** over each process's post-warm-up stretch, so one GC or hiccup does not move it; stretches under 10 minutes are skipped, so `--quick` reports none.
- **One client IP.** Everything is loopback, so the per-IP limits see one client; the connection cap and the deny list are set high.

**What it found** (two 60-minute runs on 2026-11-14, `bench/soak/results/2026-11-14-full*`; details and data in `bench/soak/README.md`):
- A rate-limit bucket was evicted by the same client's buckets on other hosts, and an evicted bucket starts full: past eight rate-limited hosts a 20/s limit let 88/s through. **Fixed** in `RateLimitTable` (the probe start mixes in the slot), with a regression test.
- A restart with WebSockets open waits out the whole 30 s `ShutdownTimeout` and loses the analytics and metrics still in memory: core stops on a budget the public drain has used up. **Open.**
- A destination that comes back after a flap stays out for a minute: YARP's passive window holds the outage's failures, and its 60 s reactivation ignores the upstream's configured period (reproduced in a unit test). **Open.**
- About 15 HTTP/2 requests an hour (of 14.9 million) get no response; the proxy logged them as completed. A Kestrel bug: a pooled HTTP/2 stream can be left unable to send ([dotnet/aspnetcore#69690](https://github.com/dotnet/aspnetcore/pull/69690), not yet in 10.0). **Worked around** in `KestrelHttp2StreamReuse`, with a test that reproduces it every time; see "Kestrel's lost HTTP/2 responses" in [hot-path.md](hot-path.md#kestrels-lost-http2-responses).
- RSS rose in steps while the managed heap stayed flat; one hour could not tell a plateau from a leak. **Open.**

## Security

- Threat model doc, dependency scanning, CodeQL, fuzzing of ACME, JSON, YAML and multipart parsers, and an audit-chain verifier.

### CodeQL and the NuGet audit (threat model G9)

- `.github/workflows/codeql.yml` analyses the C# product code on pushes to main, pull requests and weekly. It is a manual build of `Paduraksa.Server`, which builds every product project and no test, benchmark or fuzz project. It uses the `security-extended` queries.
- `Directory.Build.props` sets `NuGetAudit` on with `NuGetAuditMode=all` (transitive packages too) and `NuGetAuditLevel=moderate`.
- **Decision.** The repository builds with warnings as errors, which would make every advisory fail restore, so the choice is explicit:
  - high and critical (NU1903, NU1904) are errors;
  - a **moderate** one (NU1902) stays a warning in the restore log. A moderate advisory that has no fixed release yet would otherwise stop every build, the Dependabot pull request that updates the package included;
  - a low one (NU1901) is not reported.
- Checked: a restore of the whole solution reports no advisory today. A probe project with a package that has high advisories fails with NU1903, while one with a moderate advisory only warns.

## Property tests

Built in v0.25, after M9 part 1 (planned for right after M7). Both property tests and fuzzing exist. The per-commit part is cheap, and the deep part is manual or nightly.

**Tooling.** **CsCheck 5.0.0**, run in `dotnet test`. `tests/Shared/Property.cs` is the one entry point.
- CsCheck seeds only a run's first case and draws the rest at random. So each case's seed is derived from a fixed base, the property's name and the case's number. A run is the same every time, and adding a test moves no other test's cases.
- A failing case is run on from its seed with 5,000 more iterations to shrink (CsCheck shrinks by keeping the smallest failure it meets).
- A failure prints the shrunk input and the seed that reproduces it, and becomes a plain regression test.
- Case counts are set per test. `PADURAKSA_PROPERTY_SCALE` multiplies all of them, and `PADURAKSA_PROPERTY_SEED` moves the base. Example: `PADURAKSA_PROPERTY_SCALE=50 dotnet test --project tests/Paduraksa.Tests.Proxy --filter-class "*PropertyTests"`.
- The defaults add about 40 s of CPU to the suite (about 25 s of wall time on four cores, the two projects overlapping). The nightly workflow runs them 50 times deeper with a seed of the day.

Each property is checked against a slow, obvious model.

**`Paduraksa.Tests.Proxy`:**
- `RoutePathPropertyTests`: the canonical matching path against a plain split-and-resolve model, its own idempotence, the stack and the pooled buffer alike, and 150 raw request targets over a socket to a bare Kestrel. Its `Request.Path` is canonicalised like the model and decoded like the simulator's model, and sent through the gate to a guarded `/admin` route.
- `GlobPatternPropertyTests`: the automaton against a backtracking regex of the same rules, on any path and on paths built from the pattern, `/**/**/x` runs, and which patterns compile.
- `CompiledRuleSetPropertyTests`: indexed exact and prefix paths, scanned globs and regexes against "the first rule whose conditions all hold", with non-ASCII case folding in the user agent.
- `AccessListPropertyTests`: allow, deny, countries, `satisfy`, drop and passwords through the real gate in Cloudflare mode against the table written out.
- `SimulatorParityTests`: the fixed tables, widened: 2,100 random requests across the unknown-host behaviours and trusted-proxy modes.
- `ForwardAuthCachePropertyTests`: the decision cache against a model that remembers an allow under everything the provider is sent, at the grain the answer says, over sequences of one-field mutations.
- `ForwardAuthHeaderPropertyTests`: the owned names and the stripping against a model with `_` and `-` as one character, and a name `ForwardAuthHeaderNames.Problem` accepts never owning a header the proxy owns.
- `AccessLogFormatterPropertyTests`: every line read back by a plain reader of the grammar finds exactly 13 fields, the quoted ones decode to what was sent, nothing a reader may take for a line end survives, and a sensitive query value never appears.

**`Paduraksa.Tests.Unit`:**
- `ConfigGen` generates whole config documents: every list, every settings layer, references that mostly resolve, values that are mostly valid. Since the M8/M9 fuzz round the layers also carry error pages at every layer in both modes with valid and refused templates, maintenance with a bypass access list, and `replaceUpstream`. The pipeline property fails if too few valid documents reach them.
- `ConfigPipelinePropertyTests`:
  - the validator never throws;
  - a document that validates never fails to compile;
  - its canonical JSON is idempotent, round-trips and still validates and compiles;
  - the same config written differently has the same canonical bytes.
- `SimulatedBehaviourTests`: for any generated valid document and request, the simulator says what the real gate answers. For the page, `errorPage` is the operator's page exactly when the gate serves bytes equal to the preview compiler's rendering of the entry a plain walk over the document finds (host, then profile, then global defaults), for refusals and for maintenance.

### What the property tests found

1. The simulator decoded `%5C` in a path to `%5c`, but Kestrel hands over a backslash. Found by the differential against real Kestrel with `/%20adm%61in;x%zz%252F%5c.%C3%A9`. The trace's decoded path was wrong; matching was not.
2. The forward-auth decision cache key left out the scheme and the standard hint headers the provider is sent (`User-Agent`, `Accept`, `Accept-Language`, `X-Requested-With`). An allow was therefore served to a request that differs in them. Found by the cache model.
3. The access-log formatter left NEL, the other C1 controls and U+2028 and U+2029 unescaped. A reader that splits on a Unicode line end takes them for a line end.
4. A custom `Server` header that the global defaults leave without a value validated but could not compile (a document with no host). Found by validate-implies-compile.

One property was wrong, not the code: `RoutePath.IsCanonical` answers false for any path with a `%`, canonical or not, because it is only a fast path.

Each property and harness was shown to fail by breaking the code it checks: the `/**/**/x` fix undone, a prefix candidate dropped, a satisfy rule changed, a redirect status, the owned-name folding, the cache grain, a canonical ordering, an expiry check, a return-target rule, an empty user name accepted, an untrusted peer believed, an identity-value check dropped, an allocation added, a verdict made random.

### Gaps

- The ACME response parsing gets a harness when it lands.
- The fuzzers are not shown a real network: HTTP framing is Kestrel's, and the response body's streaming is not driven.
- The simulator parity property leaves out hosts Kestrel would answer 400 itself (the socket parity tests pin that rule), and feeds the real gate already-decoded paths (the Kestrel differential covers the decoding).
- The rate-limit table, the proof-of-work page and the gate's allocation have no property test.
- Coverage is the proxy's and Core's assemblies only. YARP and the framework are not instrumented.
- The harnesses build and run on Linux x64.
- The generators reach M8's settings since the fuzz round for M8 and M9. M9 added nothing to the config document: notification channels are an operational table, not config.

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
| `client-address` | `CF-Connecting-IP`, `X-Forwarded-For`, `True-Client-IP` and `CF-IPCountry` through the resolver in every trusted-proxy mode. An untrusted peer is never moved by a header and gets no country. An address taken from a header is the one `IPAddress` reads. The parser's text and UTF-8 forms agree. |
| `forward-auth-answer` | The provider is a handler that answers with the input, so the whole gate runs with no socket. The answer is compared with a plain reading of the rules. A log line that names an exception is the crash, since the policy turns one into a 502. |
| `error-page`, `upstream-error-replacement`, `access-log-tail` | The M8 and M9 surfaces (see below). |
| `mcp-sandbox` | The parent of the MCP sandbox against a child that is not to be trusted (see below). |
| `yaml-import` | The YAML reader and importer (see below). |
| `ip-network`, `glob-pattern`, `cloudflare-ranges`, `backup-archive`, `certificate-import` | The `ci-tests` round (see below). |

### Corpora and crashes

- `fuzz/corpus/<target>/` is committed. The `seed-*` files are well-formed starting points written by `Paduraksa.Fuzz seed`. The rest is what runs found, minimised by `scripts/fuzz.sh --merge`.
- `fuzz/regressions/<target>/` holds inputs that once crashed a target.
- `.gitattributes` marks both binary (a PROXY header ends in CR LF).
- `tests/Paduraksa.Tests.Fuzz` replays every committed input through its target on every commit, with no libFuzzer and no instrumented assembly, so a corpus regression fails CI.
- A crash is copied into `regressions/`, fixed, and gets a unit test beside the code.
- `scripts/fuzz.sh [--time S] [--target T] [--merge] [--replay] [--list]` publishes the harness, instruments, runs libFuzzer per target for the time box, and fails on a crash or an error.
- `.github/workflows/fuzz.yml` (`workflow_dispatch` and a nightly schedule, never push or pull request) runs it (15 minutes a target by default) and the property tests 50 times deeper. It uploads crash inputs, logs and the grown corpus.
- `docs/development.md` has the commands.

### What the first fuzz round found

The fuzzers found no product bug. Two harness bugs came up in the first forward-auth runs, both fixed in the harness:
- a header name that is not a token, which a transport never delivers (within 243 runs);
- repeated `Location` lines compared as one joined string.

libFuzzer itself found the empty-name break from one valid seed in under a minute, when the code was broken on purpose.

### Sessions run (v0.25)

On a four-core box, one target per process. None of the final runs crashed.

| Target | Time | Runs |
|---|---|---|
| `proxy-protocol` | 5 min | 5.3 M |
| `pow-cookie` | 5 min | 3.8 M |
| `pow-form` | 5 min | 4.7 M |
| `basic-auth` | 5 min | 4.0 M |
| `client-address` | 10 min | 5.2 M |
| `forward-auth-answer` | 10 min | 3.2 M |

A run of every target for the release checklist (M11) is `scripts/fuzz.sh --time 3600 --merge`.

### The `mcp-sandbox` target (M10, `m10-mcp`)

It tests the parent of the MCP sandbox (see [admin-surfaces.md](admin-surfaces.md)) against a child that is not to be trusted.

**Decision.** The harness no longer fuzzes the scripts in process: a stock engine in the same process as the fuzzer crashes and hangs on the very scripts the boundary exists to absorb, which is noise, not a finding. A child per input is too slow (a start is about 180 ms). So the target has two modes, picked by the first byte.

**Frames** (three inputs in four, in process, instrumented, fast). The rest of the input is what a hostile child writes to its stdout, fed to the parent's run protocol (`RunProtocol.ExchangeAsync`) over a stub dispatcher and the real request policy. It carries a known run id that the seeds carry, so the fuzzer can mutate it into a wrong, a missing or an old one: no frame that is not an answer to the run in progress may be served.

The call may return an outcome, or end as a breach or a protocol violation, and nothing else may happen:
- no other exception;
- a result within the output cap, an error of at most 700 characters without control characters, a bounded note;
- at most the request limit dispatched;
- every dispatched request one the policy allows (a plain path under `/api/v1`, a bounded query, body and entity tag, a write only with the bytes saying `confirm` or as a dry run);
- everything the parent wrote back a `response` frame.

**Scripts** (one in four, when the harness can start a child). `scripts/fuzz.sh` publishes an uninstrumented copy and sets `PADURAKSA_FUZZ_CHILD`, and a replay from the driver finds the driver's own assembly. The rest of the input is a script, run on a real child with 1 s of wall and CPU time. The properties are the parent's: it never throws, the call ends within its wall limit plus a margin, the answer is bounded, and the pool holds at most one child.

Seeds: 23 script seeds (the scripts that were once dangerous in process, plus ordinary ones) and 21 frame seeds (every shape of violation). Without a child a script input is read as frames, so the corpus replays in CI (`Paduraksa.Tests.Fuzz`) without one.

**Found.** The first minute found a real bug in the parent: a frame with invalid UTF-8 inside a string reached `JsonElement.GetString`, which throws `InvalidOperationException`. The production loop would have treated it as a broken child, but by accident. Frames are checked for UTF-8 first, text with a lone surrogate is a violation, and the input is in `fuzz/regressions/mcp-sandbox/`.

**Sessions.**
- 25 minutes (`--out /tmp/fz-mcp-r3`): 70 562 executions (47 a second: a quarter of the inputs run a script on a real child, and the breaching ones wait out a limit). No crash, no abandoned or orphaned child. The corpus was merged on edges (`-use_counters=0`) to 397 inputs.
- After the review that bound every frame to its run, a fresh corpus from the new seeds and 18 minutes (`--out /tmp/fz-mcp-r4`): 163 833 executions (151 a second), no crash, no orphaned child, merged on edges to 475 inputs.

### Fuzz round for M8 and M9

**Targets.**

*`error-page`.* It compiles a host whose pages use every variable, as a message and as a document (the global defaults' page for an unknown host, a wildcard host's own for a path no route takes). It sends the real gate a `Host`, an `X-Request-Id` from a trusted or an untrusted peer, a `CF-Ray` with Cloudflare's headers trusted or not, and an `Accept`, decoded as UTF-8, Latin-1 or UTF-16 (which can hold a lone surrogate). Checks:
- The page must be the page's template with each variable replaced by a plain HTML encoder's reading of the value as the rules cut it: the host without its port at 253 characters, the ray at 64 and empty unless trusted, and the request ID the client's only from a trusted peer and only if it is 1 to 128 visible ASCII characters.
- No variable adds a raw `<`, `>`, `"` or `'`, an `&` that starts no entity, or a non-ASCII byte.
- `HEAD` has no body.
- HTML is chosen exactly when an independent reading of `Accept` says so.
- The same input gives the same bytes.
- The writer and the `Accept` decision allocate nothing.

*`upstream-error-replacement`.* It asks `ReplaceUpstreamErrorTransform.Replaces(mode, status, mediaType, upgrade, started)`, the pure decision the transform and the target now both call (the transform's success path is unchanged, one integer comparison, its allocation tests green). It checks each mode against a plain model of the rules: never for an upgrade, an event stream, `application/grpc*` or a started response; `html` only for `text/html` in any case and with parameters; only for a status that has a page. The media type is read from the `Content-Type` lines by `HttpResponseMessage` as the transport would, and the target is also fed the raw text of each line.

*`access-log-tail`.* It sends one request's hostile host, method, path, query, protocol, upstream, request ID, `CF-Ray`, referer and user agent through `AccessLogFormatter` and through the tail entry built as the drain builds it. Checks:
- a plain reader of the line's grammar finds thirteen fields and no line end;
- every tail field equals the file line's field (numbers by value, quoted text exactly unless a cap cut it, and then a prefix of the full escape closed by the marker);
- the line's request target is what a plain model builds from the method, path, redacted query and protocol;
- a sentinel in a sensitive parameter at the front of the query appears in neither output, and with the query string off neither does a sentinel in a plain parameter;
- the line costs 0 B.

**Generators and the simulator.** `ConfigGen` sets the M8 settings at every layer. `Respelled` adds empty error-page and maintenance layers that canonical JSON must drop. A property holds the simulator's page selection (`ErrorPageSelection`, which the writer and the simulator both call) to what the gate serves for refusals and maintenance, by comparing the gate's bytes with the preview compiler's rendering of the entry a plain walk over the document finds.

**Found.** One product bug: `AccessLogFormatOptions.IsSensitive` enumerated its key list through `IReadOnlyList<string>`, which boxes an enumerator. Every query parameter of every access-log line therefore allocated 32 B on the drain thread (the formatter documents that a line costs nothing; nothing measured it). It indexes the list now, with a test beside it (`AccessLogFormatterTests.ALineWithAQueryAllocatesNothing`) and the input kept in `fuzz/regressions/access-log-tail/`.

Harness bugs, fixed before the final runs:
- `HttpClient` forgives blanks around the `/` of a media type (`applicatio /grpc` reads `applicatio/grpc`) and reads only the first of two `Content-Type` lines.
- The tail's query can be cut twice (the redaction's marker, then the budget's), which leaves up to five dots.

Mutation checks: each target failed when the code it checks was broken (a slot written unencoded, a referer left unescaped, redaction switched off, the maintenance page's selection taken out of the writer).

**Sessions run.** On a 20-core box, one target per process, 25 min each, run in parallel. None of the final runs crashed. Each corpus was merged (`--merge`) into `fuzz/corpus/<target>/`. The runs before them stopped on the harness bugs above and restarted from the fixed harness.

| Target | Runs | Rate | Corpus files |
|---|---|---|---|
| `error-page` | 30.6 M | 20 k/s | 144 |
| `upstream-error-replacement` | 95.7 M | 64 k/s | 101 |
| `access-log-tail` | 15.9 M | 11 k/s | 62 |

**Left out.** `Paduraksa.Fuzz` still has no target for the dashboard's own parsing, the live tail's SSE framing or the notification senders' payloads (a sender builds its request from the operator's own text, not from a client's). The tail's query keeps the file's 1 KiB cap, so a query cut twice can end in `.....`: cosmetic, left as it is.

### The `yaml-import` target (M10)

`fuzz/Paduraksa.Fuzz.Targets/YamlImportTarget.cs`. The input is YAML bytes, read through the guarded reader, mapped onto the document and merged, with no database, twice: replacing the empty config, and merging into a small stored one (so identity matching and names that resolve against stored objects are reached).

**Properties.**
- No exception. Only a result with errors, each a JSON-pointer path, a code and a message that names no CLR or parser type, under 800 characters.
- The same bytes give the same result, the new IDs included (a counter supplies them).
- **The caps hold.** The reader under a tight set of limits (6 levels, 400 nodes, 64-character scalars, 16-character keys, 4 anchors, 8 aliases) returns a tree that, counted again, is no deeper or larger and has no longer scalar or repeated key, whatever aliases and anchors the input uses. Under the real limits an import of up to 4 KiB allocates under 192 MiB and runs under 8 s.
- An accepted document that validates is exported, read back and is the same canonical bytes (versions aside), with no `password` key in the export. A document the validator refuses can still be written, but the import refuses its repeated IDs, so only that it can be written is checked.

`scripts/fuzz.sh` instruments `Paduraksa.Application`, `Paduraksa.Contracts` and `YamlDotNet` as well as Proxy and Core.

There are 24 seeds: well-formed configs, a flow-style one, an export, a billion-laughs document, deep nesting, tags, two documents, a merge key, a BOM, invalid UTF-8.

**Sessions run (M10).** On a 20-core box, one target per process: two 25-minute runs (7.9 M and 6.3 M runs, about 5 k/s each), then two 22-minute runs of the final code seeded from their corpora (6.4 M runs each), about 27 M runs in all. The first pair stopped one run early on a real finding, and the second pair did not crash. The corpora were merged into `fuzz/corpus/yaml-import/` (3 052 files, 1.3 MB: every input that reached a feature the others did not).

**Found.**
- The reader's error for an unknown alias quoted the alias's name, which the input makes as long as it likes (a 3 000-character message). The name is cut at 40 characters now. The input is `fuzz/regressions/yaml-import/alias-unknown-long-name`, with a unit test beside the reader.
- The unit tests found two more: the emitter's own analysis misjudges some mixes of YAML indicators and quotes (`0 *'` followed by a backtick as a key), so the writer asks for a plain scalar only for text of letters, digits and a few joiners and quotes everything else itself. And the scanner throws `InvalidOperationException` for an unterminated flow collection followed by more lines.
- The property over `ConfigGen` found that an empty `passiveHealth` is not the same as none, and that a valid document can name an object that is gone in a layer nothing uses. Both are in [admin-surfaces.md](admin-surfaces.md).
- Harness bug, fixed before the final runs: the round trip was first asserted for every accepted document. But a document with a route ID used by two hosts (a merge of an export whose host ID was changed) maps and cannot be read back, because the import refuses repeated IDs.

### The `ci-tests` round: five more targets

The gaps list named parsers of attacker or admin input that had no harness. Each target states its properties in its class's remarks.
- **`ip-network`.** `IpNetwork.TryParse` on text. The verdict is compared with an independent reading of its three rules (the address `IpParser` reads, a decimal prefix without a leading zero within the family's width, no host bit below the prefix, judged on the bytes `IPAddress` reads). A parsed block spans exactly 2^(128-prefix) addresses, contains its ends and not the addresses beside them, and its canonical text parses back to the same block. 0 B on both paths, the error included.
- **`glob-pattern`.** A pattern, then a path. A pattern compiles exactly when it is ASCII, starts with a slash and fits the token limit. A match equals a memoised recursion written from the rules (no automaton in it), the case of the path never matters, 0 B, and a 64 KiB path against any pattern takes well under a second.
- **`cloudflare-ranges`.** The real `CloudflareRangeFetcher` over a handler that answers with the input (the JSON answer, the two text ones, or both, under a 1 KiB cap). A list is accepted exactly when an independent reading of the list rules accepts the first source that gives one, and then holds the canonical, sorted, unique blocks that source says, three of each family at least, none too wide, with every end in the set. A refusal is one short printable line.
- **`backup-archive`.** The header and chunk reader up to the passphrase check, under a fixed key (the Argon2 derivation is the cost a header may ask for, up to a gigabyte, so it is not run). Only `BackupException` escapes, with the reason a second reading of the header table and the chunk rules gives (the chunks are decrypted again with `AesGcm` in the target). An accepted header is the bytes `ToBytes` writes and asks for a derivation within the caps. Allocation stays bounded whatever the header says. The first input byte can pick the manifest instead: it parses or is `Damaged`, and one that parses writes and reads back to itself.
- **`certificate-import`.** The vault's `ICertificateImport` over a scratch directory, a fixed clock and one fixed certificate ID (a success replaces the row). Input byte 0 picks the password. Only `CertificateImportException` escapes; the same upload gives the same verdict; an import takes under 30 s and 512 MiB; a refusal never repeats the password; the leaf of a success is one of the PEM's certificates by SHA-256, has a name, and the public chain holds no key.

`Paduraksa.Vault` is instrumented by `scripts/fuzz.sh` and lets `Paduraksa.Fuzz.Targets` see its internals (`InternalsVisibleTo`, like `Paduraksa.Tests.Vault`); the targets project is a key-material boundary project (`PaduraksaKeyMaterialBoundary`) because the backup target decrypts with `AesGcm` and the import target builds seed certificates. No other target uses either.

**Found.** Three real bugs, each with its input in `fuzz/regressions/<target>/` and a unit test beside the code.
- A refused Cloudflare range list put the response's text in its reason as it came, so an escape sequence or a forged log line in a block reached the log and the dashboard. Control, format and line-separating characters are `?` now (`CloudflareEdgeRangesTests`).
- Invalid UTF-8 in a string of the JSON answer threw `InvalidOperationException` out of `CloudflareRangeFetcher` (the refresh loop survived it), so the text endpoints were never asked. It is a failed source like any other now (`CloudflareRangeRefresherTests`).
- A PKCS#8 key on a curve the platform doesn't have (a made-up curve OID) escaped the certificate import as `PlatformNotSupportedException`, a 500 from the admin API. It is `Unreadable` now (`CertificateImportHardeningTests`).

`backup-archive`, `ip-network` and `glob-pattern` found nothing.

**Sessions run.** On a 20-core box, 10 minutes a target, one process each (three other lanes of an hour-long run were going at the time), then `--merge` into `fuzz/corpus/<target>/`; a target that crashed was fixed and run again for the full time.

| Target | Runs | Per second | Corpus files |
|---|---|---|---|
| `ip-network` | 34.6 M | 60 k | 114 |
| `glob-pattern` | 7.4 M | 12 k | 87 |
| `cloudflare-ranges` | 8.2 M | 14 k | 279 |
| `backup-archive` | 14.3 M | 24 k | 46 |
| `certificate-import` | 1.0 M | 1.7 k | 124 |

`.github/workflows/fuzz.yml` runs all 16 targets one after another, so its timeout is 300 minutes (16 targets at 15 minutes and the build); `FuzzWorkflowTests` fails when a target is added without raising it.
