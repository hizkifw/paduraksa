# Developing Paduraksa

You need the .NET 10 SDK. Docker, a Chromium and libmsquic are optional locally: the tests that need them skip without them (see [Tests that need something installed](#tests-that-need-something-installed)).

## Build and test

```sh
dotnet build
dotnet test
dotnet format --verify-no-changes
dotnet run --project src/Paduraksa.Server --launch-profile dev
```

Warnings are errors, and CI runs all three checks.

Write ASCII. `NoConfusableCharactersTests` (architecture tests) fails on an ambiguous or invisible Unicode character
anywhere in a tracked file (an en dash, a smart quote, a non-breaking or zero-width space, a Greek or Cyrillic homoglyph),
because editors flag them and every reviewer has to look twice. The test's remarks list what stays allowed (box drawing,
arrows, `§`). `./scripts/check-confusables.sh` is the same check as one grep.

## Tests that need something installed

Three kinds of test need more than the SDK, and skip when it is missing:

| Needs | Tests | Locally |
|---|---|---|
| Chromium | the Playwright browser tests of the dashboard (`Paduraksa.Tests.Dashboard/EndToEnd`) | `PADURAKSA_E2E_CHROMIUM=/usr/bin/chromium`, or `playwright.ps1 install chromium` (see `PlaywrightBrowser`) |
| libmsquic | the QUIC and HTTP/3 tests (trait `Transport=Quic`) | install it, or `tests/Paduraksa.Tests.Proxy/Tls/run-quic-tests.sh` runs them in Docker |
| Docker and the Pebble image | the ACME tests against Pebble (`Paduraksa.Tests.Issuance`, `Paduraksa.Tests.Integration/Acme`); the container smoke test is separate and needs `PADURAKSA_CONTAINER_TESTS=1` | `docker pull` works; `PADURAKSA_PEBBLE=off` skips them on purpose |

A skip hides a gap, and it once hid one for good: CI had no Chromium and no libmsquic, so 19 browser tests and 19 QUIC tests skipped on every run. **`PADURAKSA_REQUIRE`** turns a skip into a failure for what it names, a comma-separated list of `playwright`, `quic` and `pebble` (`tests/Shared/Requirements.cs`). CI installs all three and sets `PADURAKSA_REQUIRE=playwright,quic,pebble`, so a runner that loses one goes red. Nothing changes locally unless you set it, for example `PADURAKSA_REQUIRE=quic` to check that a QUIC run is not all skips (`run-quic-tests.sh` sets it).

## Property tests and fuzzing

Two kinds of test go beyond examples ([testing.md, Property tests](design/testing.md#property-tests)).

**Property tests** (CsCheck) sit in `Paduraksa.Tests.Proxy` and `Paduraksa.Tests.Unit` next to what they test (`*PropertyTests`) and run in `dotnet test`. A property
checks the code against a slow, obvious model on generated input. Every case's seed is derived from a fixed base, so a run is the same every time, and a failure
prints the shrunk input and the seed that reproduces it. Case counts are set per test; `PADURAKSA_PROPERTY_SCALE` multiplies them and `PADURAKSA_PROPERTY_SEED`
moves the base, so a deep run is one line:

```sh
PADURAKSA_PROPERTY_SCALE=50 PADURAKSA_PROPERTY_SEED=7 dotnet test --project tests/Paduraksa.Tests.Proxy --filter-class "*PropertyTests"
```

A counterexample becomes a plain test beside the code it found a bug in, and the fix is its own commit.

**Fuzzing** (SharpFuzz and libFuzzer) covers the parsers that read raw attacker input: the harnesses are in `fuzz/`, the committed inputs of each target in
`fuzz/corpus/<target>/` and the inputs that once crashed one in `fuzz/regressions/<target>/`. Every commit replays all of them through the targets without libFuzzer
(`Paduraksa.Tests.Fuzz`, or `scripts/fuzz.sh --replay`). A real fuzzing run is manual or nightly (`.github/workflows/fuzz.yml`):

```sh
scripts/fuzz.sh --list                                   # the targets
scripts/fuzz.sh --time 600                               # every target, ten minutes each
scripts/fuzz.sh --target proxy-protocol --time 3600 --merge   # one target, an hour, then keep what it found
```

It publishes the harness to `artifacts/fuzz/bin`, instruments the proxy's assemblies (the parser's among them) there with the pinned `sharpfuzz` tool and runs the pinned release of
`libfuzzer-dotnet` (downloaded once into `~/.cache/paduraksa`). A crash is a file in `artifacts/fuzz/crashes/<target>/`. To keep it: copy it to
`fuzz/regressions/<target>/`, fix the bug and add a unit test next to the code. The targets are `scripts/fuzz.sh --list`; each states its properties in the remarks of its class (`fuzz/Paduraksa.Fuzz.Targets`), and the table is in [fuzz/README.md](../fuzz/README.md). Adding one means registering it in `FuzzTargets.All`, writing its seeds (`Paduraksa.Fuzz seed`) and running it for a while with `--merge`; the nightly job's timeout has to grow with the count (`FuzzWorkflowTests` checks it). `Paduraksa.Fuzz seed` writes a target's well-formed starting inputs (`fuzz/corpus/<target>/seed-*`).

## Publishing the NuGet packages

`Paduraksa.ProxyProtocol` (the PROXY protocol parser, BCL only) and `Paduraksa.ProxyProtocol.AspNetCore` (a Kestrel `UseProxyProtocol` over it) are the solution's only packable projects.
`src/Packaging.props` holds what they share, and each has its own README, which is the package readme.

- **Every CI run** packs both (`pack` job in `.github/workflows/ci.yml`), with warnings as errors and package validation on, and uploads the `.nupkg` and `.snupkg` files as the `nuget-packages` artifact. Locally:
  `dotnet pack src/Paduraksa.ProxyProtocol -c Release -o artifacts/nupkg`, then the same for `src/Paduraksa.ProxyProtocol.AspNetCore`.
- **The version** is the `Version` property in `Packaging.props` (0.1.0). A `proxyprotocol-v*` tag packs at the tag's version instead (`proxyprotocol-v0.1.0` gives `0.1.0`, `proxyprotocol-v0.2.0-rc.1` gives `0.2.0-rc.1`), so the tag decides.
- **The packages have their own tag.** They ship when they change, not with every release of the app: a `proxyprotocol-v*` tag publishes them and builds no image, and the app's `v*` tags publish no package.
  For a `proxyprotocol-v*` tag, once `build-test` and `pack` pass, the `publish-nuget` job pushes the `.nupkg` files to nuget.org with one `dotnet nuget push --skip-duplicate`, which also pushes the `.snupkg` symbol package beside each. A version already there is skipped.
- **No API key is stored.** The job uses nuget.org's [trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing): it asks GitHub for an OIDC token (`id-token: write`, on that job only), `NuGet/login` exchanges it for an API key that lives one hour, and nuget.org grants it only to the repository and workflow file a policy names.
  It does nothing until the repository has a variable named `NUGET_USER`: without it the push is skipped and the job says so.
- **To enable it,** on nuget.org open your username, Trusted Publishing, and add a policy: Repository Owner `hizkifw`, Repository `paduraksa`, Workflow File `ci.yml` (the file name only, no `.github/workflows/`), Environment empty.
  Then set the variable to your nuget.org profile name (not the email address): `gh variable set NUGET_USER --body <profile name>`. The next `proxyprotocol-v*` tag then publishes.
  Renaming `ci.yml` or moving the job to another workflow file breaks the policy until it is edited to match.
- **After the first release** move the lines of each `PublicAPI.Unshipped.txt` to `PublicAPI.Shipped.txt`. From then on a change to the public API shows as an added line in `Unshipped`, and a removed or changed member is a build error until the shipped file says so; set `PackageValidationBaselineVersion` in `Packaging.props` to the last version so a breaking change fails the pack.
- **The icon** is `src/icon.png`, 128x128 and 762 bytes, made from the dashboard's `src/Paduraksa.Dashboard/wwwroot/favicon.svg` (NuGet takes no SVG). The PNG is committed; nothing in the build makes it.
  To regenerate it after the favicon changes, render the SVG, reduce it to a 48-colour palette (it can't be told from the full render: at most 3/255 per channel on white or black) and recompress it with zopfli.
  The snippet needs `rsvg-convert`, and Pillow built with libimagequant (the PyPI wheels aren't; a distro's usually is) and the `zopfli` package:

  ```sh
  rsvg-convert -w 128 -h 128 src/Paduraksa.Dashboard/wwwroot/favicon.svg -o /tmp/icon-full.png
  python3 - <<'EOF'
  import io
  from PIL import Image
  from zopfli.png import optimize

  full = Image.open("/tmp/icon-full.png").convert("RGBA")
  palette = full.quantize(colors=48, method=Image.Quantize.LIBIMAGEQUANT, dither=Image.Dither.NONE)
  raw = io.BytesIO()
  palette.save(raw, "PNG")
  open("src/icon.png", "wb").write(optimize(raw.getvalue(), filter_strategies="0", num_iterations=200, num_iterations_large=200))
  EOF
  ```
- **Targets** are `net10.0` and `net8.0`. Only `net10.0` runs the tests; the `net8.0` build is compiled (with the API analyzer and package validation) but not executed.

## Writing tests that don't flake

CI runs on GitHub's 4-core runners with the test classes in parallel, and a test that passes alone can fail there: a blocked image publish blocks every deploy.
`scripts/test-ci-like.sh` (official SDK, Release, 4 cores) is the local check; to reproduce a load failure, pin a class to the cores and load them,
for example `taskset -c 0-3 dotnet test --no-build -c Release --project tests/<project> --filter-class "*Name"` in a loop, beside a few busy loops
(`taskset -c 0-3 bash -c 'while :; do :; done' &`; `stress-ng --cpu 8` does the same). Fix a flake at its cause: a retry or a longer timeout hides the race, and a deleted check
leaves the thing unprotected. What has caused them so far:

- **A clock moved before the code armed its wait.** A fake clock advanced before the code under test has started its timer never fires it. Advance until the effect is seen,
  bounded (`AdvanceUntilAsync` in `NotificationDispatcherTests`), or wait for an internal signal that the wait is armed (the dispatcher's `Routed` counter).
- **A signal counted from the wrong moment.** A test that takes a baseline count and then waits for it to grow is satisfied by the next unrelated tick. Count from the
  state the test cares about (`LogFilesPageTests`: scrolls are counted from the render that shows the new line). A fixed-interval poll in the code under test makes the same mistake in
  `VerifyInvoke(id)`, which means exactly once.
- **Parallel classes sharing process state.** Environment variables set process-wide, every child process of the test process, a static. Scope to the test's own instance
  (`McpShutdownTests` asks its own server) or use a harmless sentinel.
- **A wall-clock assertion, or a rate the test keeps.** "Under 150 us a call", "20 000 entries a second": a starved runner fails them without anything being wrong. Assert the cause
  (the number of rule comparisons, that every entry arrived, that nothing allocated) and keep the timing in the benchmark project (`bench/`). A writer that must not overflow a
  ring waits for room instead of keeping a rate.
- **Asserting an exact batching or timing outcome.** Assert the invariant instead (`LiveTailPageTests` burst: every line is there, in order, whatever the batch sizes).
- **bUnit's 1 s default wait** and a thread pool of 4: `TestAssemblySetup` raises the wait to 10 s and the pool's minimum. A wait ends as soon as its condition holds, so a passing test is no slower.
- **Zero-allocation checks.** The per-thread counter jumps once, by 2-8 KB, at a random iteration, under load. `AllocAssert.Zero` re-measures and forgives one allocating call; a check
  that sums bytes by hand (`CompletionProbe`) allows one request that allocated, like `GateAllocationTests`. A real allocation happens on every call and still fails.
- **A server that answers and closes while the client is still writing.** The client sees a reset before the 413. Send `Expect: 100-continue` (`RequestLimitsEndToEndTests`;
  `AdminApiHarness.SendAsync(..., expectContinue: true)`, whose client waits 30 s for the answer), so the body goes only after it.
- **Waiting a stretch of wall time for something to happen.** A child that must burn CPU, a timer that must fire: a loaded runner gives it a fraction of the time. Wait for the
  measured thing itself (`SandboxHostileChildTests` polls the child's CPU time), bounded.
- **Playwright.** Each browser test starts its own Chromium and server; run in parallel on 4 cores they starve one another (page loads over 30 s). Every one is in the
  `[Collection(PlaywrightBrowser.CollectionName)]`, which runs alone, one class after another, and `Expect` waits 15 s. Never read a response body after the page has navigated away: use a plain
  API request.
- **A cold first call.** The first request of a boot runs cold code, and a probe or client with a contract timeout of its own (the 3 s of `doctor --liveness`) takes a longer
  one in tests (`LivenessAsync`'s `timeout`), as the timeout is not what the test checks.
- **A local port prober.** On the dev machine a Node tool sends `GET /` to every new localhost port within seconds. A fake or a log that counts requests must count only what
  the proxy sent (`X-Forwarded-Proto`: `FakeAuthServer`, `BodyCountingUpstream`) or only the names the test sent (`*.test` in `AccessLogPipelineTests`). CI doesn't have it, so such a failure shows locally only.
- **Ports.** Bind port 0 and read the real one back (`TestHost.Endpoint`). If a number must be known before the bind, `TestPorts.StartOnFreePortAsync` retries the whole start with a new port; never probe, release and rebind by hand.
- **Sleeping for time.** A `Task.Delay` or `Thread.Sleep` in a test waits for a signal or it is one of a few things that really are time, and says which in a one-line comment beside it:
  a short poll step inside a bounded wait for a condition; a step of the fake clock's pump (real time for loops to run between steps, `AdvanceUntilAsync`); the pacing of a load or soak test whose subject is running for that long; a deliberately slow or hung peer
  in a test about slowness; an absence window, where nothing can signal that something did not happen (keep it short and name what would show in it); a real-time gap that cannot be faked (a real host's timers). Anything else becomes a signal (a `TaskCompletionSource` the code completes, a counter, the armed timer of a fake clock,
  a failing fake's own count) or a fake clock: a quiet period after the fact is replaced by waiting for the cause.
- **Waiting for something that arrives by an event.** Wait on the event (a `TaskCompletionSource` completed by the code, `WaitForState` in bUnit), with a bound for the failure
  case that a loaded runner can't reach: not a poll of a fixed number of 50 ms steps.

## The API description

`/api/v1/openapi.json` is served from a copy of the generated document, `src/Paduraksa.Admin/OpenApi/openapi.v1.json`, embedded in the assembly. ASP.NET builds the schema of
every request and response type again for every operation, so generating it takes seconds of CPU and gigabytes of allocation, which the first `/api/docs` visit and the
first MCP `search` of every boot would otherwise wait for. `OpenApiSnapshotTests` fails when the copy no longer equals a fresh generation (after you add or change an
endpoint, a DTO, an enum, a description or an XML comment on one). Regenerate it, commit it with the change, and rebuild:

```sh
PADURAKSA_UPDATE_OPENAPI=1 dotnet test --project tests/Paduraksa.Tests.Integration --filter-class "*OpenApiSnapshotTests"
```

The file is the API's changelog as much as a build input: read its diff before committing it. A server run from a build made before you regenerated serves the old description.

## The data directory

The server keeps its state in `Paduraksa:DataDirectory` (default `./data` next to the shipped `appsettings.json`, so
`src/Paduraksa.Server/bin/Debug/net10.0/data/` for the `dev` profile; set `Paduraksa__DataDirectory` to move it). It
holds the config database `paduraksa.db` and `last-good.json`, the config that was last live.

The server falls back to `last-good.json` (degraded) when the database can't be used at startup, and restores the
database from it when the database has no revisions. So to start over with an empty config, stop the server and delete
both. One data directory serves one server at a time: the server holds `run/server.lock` for its life, a second one fails to start, and `paduraksa restore` refuses while it is held.

## Database migrations

The config database schema (`ConfigDbContext` in `src/Paduraksa.Data`) comes from EF Core migrations in
`src/Paduraksa.Data/Migrations`, applied at startup by `ConfigDatabase.InitializeAsync`. After changing an entity or its
mapping, add a migration with the pinned `dotnet-ef` from the local tool manifest:

```sh
dotnet tool restore
dotnet restore
dotnet ef migrations add <Name> --project src/Paduraksa.Data --output-dir Migrations
```

Commit the tool's output as is: `.editorconfig` marks the Migrations folder as generated code, so the style rules and
`dotnet format` skip it. Review it like any other change, though. A test (`HasPendingModelChanges`) fails when the model
and the migrations disagree.

Commands that open a database (`database update`, `migrations remove`) use a scratch file in the temp directory unless
you name one after `--`, e.g. `dotnet ef database update --project src/Paduraksa.Data -- data/paduraksa.db`. Keep
`dotnet-ef` in `.config/dotnet-tools.json` on the same version as the EF Core packages in `Directory.Packages.props`.

The vault database (`VaultDbContext` in `src/Paduraksa.Vault`, `data/vault.db`) has its own migrations in
`src/Paduraksa.Vault/Migrations`, applied at startup by `IVaultLifecycle.InitializeAsync`. Add one the same way:
`dotnet ef migrations add <Name> --project src/Paduraksa.Vault --output-dir Migrations`.

A new vault table that holds a sealed value must also be added to `KekRotation.RewrapAllAsync`: a KEK rotation, a backup and a restore
all re-wrap through it (`RewrapCoverageTests` fails until it is). A backup restores an older schema by migrating it, so a migration needs
no backup-specific step.

## Repository layout

- `src/`: the product projects (layering in [PLAN section 1](PLAN.md#1-solution-layout)), among them the two NuGet packages `Paduraksa.ProxyProtocol` and `Paduraksa.ProxyProtocol.AspNetCore`.
- `tests/`: unit (`Paduraksa.Tests.Unit`), data (`Paduraksa.Tests.Data`), proxy hot path and end-to-end
  (`Paduraksa.Tests.Proxy`), the NuGet packages' parser and Kestrel middleware (`Paduraksa.ProxyProtocol.Tests`), integration (`Paduraksa.Tests.Integration`), architecture (`Paduraksa.Tests.Architecture`)
  and vault (`Paduraksa.Tests.Vault`, a key-material boundary project like the vault itself) tests.
- `bench/`: BenchmarkDotNet micro-benchmarks, the end-to-end runner and the nginx/Caddy baseline
  ([bench/README.md](../bench/README.md)).
- `spikes/`: throwaway experiments, excluded from the solution. Their findings are in [spikes/](spikes/).
- `docs/`: the design plan, the install guide, this page and the spike write-ups.
