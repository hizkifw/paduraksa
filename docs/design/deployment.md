# Deployment

Paduraksa ships as one container image (or a self-contained binary with a systemd unit) and keeps all state in one volume. This page covers the image and compose file, bare metal, SQLite and runtime tuning, backups, and the planned fleet mode (a control plane and data planes, after v0.1).

Contents:
- [Overview](#overview)
- [The container and the install path (M5)](#the-container-and-the-install-path-m5)
- [Fleet mode](#fleet-mode-control-plane-and-data-plane-after-v01)

## Overview

- **Container:** `ghcr.io/<org>/paduraksa:<ver>`, multi-arch (amd64 and arm64), Ubuntu Noble base plus `libmsquic` (pinned), `ASPNETCORE_HTTP_PORTS` cleared, non-root.
  - It listens on **80, 443 (tcp and udp) and 9443 inside the container**, mapped 1:1 in compose, so `Alt-Svc` and redirects never see a translated port.
  - Non-root binding below 1024 works because Docker (20.10+) sets `net.ipv4.ip_unprivileged_port_start=0` in the container's network namespace. For `network_mode: host` or other runtimes, the image also gives the apphost `cap_net_bind_service` (`setcap`).
  - If someone still remaps ports, `Paduraksa:Listen:PublicHttpsPort` drives `Alt-Svc` and redirects.
  - One volume: `/data`.
- **Health:** `/healthz` and `/readyz` (see [admin-surfaces.md](admin-surfaces.md)).
  - The image's own Docker `HEALTHCHECK` uses `paduraksa doctor --liveness` (which calls `/healthz` over loopback) at a 30 s interval, because the image ships no curl or wget. That costs a short-lived process per probe, which is acceptable at that interval.
  - External monitors should hit `/healthz` directly.
- **Bare metal:** a self-contained single-file publish plus a systemd unit (`AmbientCapabilities=CAP_NET_BIND_SERVICE`, `LoadCredential=master.key`).
- **SQLite:** `Microsoft.Data.Sqlite` ships its own bundled SQLite (SQLitePCLRaw `e_sqlite3`), so the host's SQLite version does not matter. The required floor is **3.35** (`RETURNING`, used by EF Core 10; WAL long predates it). `doctor` prints the runtime version.
- **Runtime tuning:** Server GC with DATAS (adapts heap to load, good for small homelab boxes), `TieredPGO`, ReadyToRun.
- **Backups:** `paduraksa backup` takes an online SQLite backup of both databases plus KEK handling instructions. Built in M10: one passphrase-encrypted archive of both databases and the last-known-good file, made online, that restores on a fresh machine under its own master key (see [vault-and-certificates.md](vault-and-certificates.md)). The `docker exec` and `docker compose run` forms and the homelab routine are in [Backups and restore](../operating/backups.md). The volume also holds `/data/run/server.lock`, the claim of the one running server.

## The container and the install path (M5)

The `Dockerfile` is at the repository root, with `.dockerignore`, so `docker build .` is the whole recipe.

### Build

- `dotnet/sdk:10.0-noble` publishes the server **framework-dependent but RID-specific** (`-r linux-$TARGETARCH`, `--self-contained false`, `PublishReadyToRun=true`, apphost kept) into `/app`.
  - The app is one web-server binary with no native dependencies of its own. A self-contained publish would only duplicate the roughly 80 MB runtime the runtime image already carries at the exact patch level.
  - ReadyToRun crossgens the app's own assemblies (not the framework's). `--build-arg PADURAKSA_READYTORUN=false` turns it off.
- `TARGETARCH` (amd64 or arm64) is mapped to the .NET RID explicitly, so a cross build fails loudly instead of producing the wrong architecture.
- Restore runs as its own step with a BuildKit cache mount, before `publish --no-restore`.

### Runtime

- `mcr.microsoft.com/dotnet/aspnet:10.0-noble` (Ubuntu Noble) with **libmsquic 2.5.10** pinned from `packages.microsoft.com/ubuntu/24.04/prod`.
  - The feed's signing key is checked against Microsoft's fingerprint (`BC528686B50D79E339D3721CEB3E94ADBE1229CF`) after dearmoring.
  - `ldconfig` is checked during the build.
  - curl and gnupg are purged in the same layer that installed them.
- The server logs **`HTTP/3 is on: QUIC on UDP 443 ...`** when libmsquic loaded (and the existing line when it did not), which is what the smoke test asserts.
- Userspace is uid and gid **10001**, with `/data` owned by it.
- The apphost gets `cap_net_bind_service` via `setcap` before `libcap2-bin` is purged in that same layer, for runtimes without Docker's `ip_unprivileged_port_start=0`. A runtime that drops capabilities (`cap_drop: [ALL]`, Kubernetes "restricted") must add `NET_BIND_SERVICE` back, or exec fails with EPERM.

### Environment

- `Paduraksa__DataDirectory=/data`.
- `ASPNETCORE_HTTP_PORTS` and `ASPNETCORE_URLS` blanked.
- `PATH` including `/app`, so `docker compose exec paduraksa paduraksa setup-token` works.
- Server GC with DATAS (`DOTNET_gcServer=1`, `DOTNET_GCDynamicAdaptationMode=1`), `DOTNET_TieredPGO=1`, diagnostics off.
- `EXPOSE 80/tcp 443/tcp 443/udp 9443/tcp`, `STOPSIGNAL SIGTERM`.
- `HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD paduraksa doctor --liveness`.

### Compose and systemd

**`deploy/compose.yaml`:**
- It builds the local image from the checkout by default (`image: paduraksa:local`; M11's published image replaces the `build:` block).
- It maps the public ports 1:1 (including `443:443/udp`).
- It publishes the admin as `127.0.0.1:9443:9443` (threat model G4). Docker's published ports sit ahead of the host firewall, so a bare mapping would offer the sign-in to every network. The LAN-address form is in a comment, and install.md "Exposing the admin" explains the choices.
- It uses one named volume for `/data`, sets a stable `hostname`, `init: true`, `stop_grace_period: 35s` and `restart: unless-stopped`, and rotates its json-file log.
- It documents the `cap_drop: [ALL]` and `NET_BIND_SERVICE` rule and the bind-mount `install -d -o 10001 -g 10001`.
- It uses the `PADURAKSA_MASTER_KEY_FILE` Docker secret by default (`./master.key`, created before the first start, host owner 10001: non-swarm Compose bind-mounts keep the host owner and mode).

**`deploy/paduraksa.service`** is the bare-metal unit for a self-contained publish:
- `AmbientCapabilities=CAP_NET_BIND_SERVICE`;
- `LoadCredential=master.key` with `PADURAKSA_MASTER_KEY_FILE=%d/master.key`;
- `StateDirectoryMode=0700`, `UMask=0077`;
- `RestartPreventExitStatus=2 3` (startup failed, invalid configuration);
- a hardened sandbox and `TimeoutStopSec=40s`.

### List settings

`ConfigurationListOptions` normalises every `IList<string>` option (`AdminAllowedSources`, both PROXY protocol `TrustedSources`, `Admin:CertificateNames`, ACME `DnsPropagation:Resolvers`) from either indexed keys or one comma-separated scalar. Values are trimmed and empty entries dropped. A scalar beside indexed keys wins deterministically, with a startup warning that the indexed entries were ignored.

### Docs

`README.md` has the short install section. The site's [Getting started](../guide/getting-started.md) and [Operating it](../operating/docker.md) pages have the details (`docs/install.md` is only the map from its old sections to them):
- compose up, reading or reprinting the setup token;
- the master key as a Docker secret with the uid-10001 chown;
- trusting the local CA root from `root.pem` (PEM for `update-ca-certificates`; DER `root.crt` for Android and iOS);
- both list forms, opening `https://<host>:9443/`;
- the 1:1 ports, HTTP/3 and `443/udp`;
- bind-mount ownership and capabilities;
- where data lives and how to back it up;
- upgrade (`git pull && docker compose up -d --build`) and `vault rotate-kek` (stop, rotate, restart);
- the health endpoints;
- the bare-metal path including libmsquic.

### The documentation site

Built on `docs-site`. The site is MkDocs with the Material theme over `docs/` (`mkdocs.yml` at the root, pins in `docs/requirements.txt`).

- **Pages.** The user guide (`docs/guide/`), operating it (`docs/operating/`) and automation (`docs/automation/`) are written for someone running Paduraksa. The threat model, the changelog, the design docs and `development.md` are the same files the repository has, shown as reference.
- **One home per fact.** The old `docs/install.md` was split into those pages, and what is left of it is a map from its old sections. The Overview's "How to move it" link and a test pin `install.md#the-master-key`, so the page keeps that heading.
- **Written against the code.** Each page's defaults, bounds, paths and commands were read from the code, and the CLI and API examples were run against a real instance. Where the code and the older text disagreed, the code won.
- **Hooks (`scripts/docs/hooks.py`).**
  - A link that leaves `docs/` (a source file, `deploy/`, `bench/`) becomes a link to the repository on GitHub, so the Markdown stays valid on GitHub and on the site.
  - `CHANGELOG.md` is added as the page `changelog.md`.
  - The API reference is written after the build: the shipped `openapi.v1.json`, byte for byte, and a pinned `@scalar/api-reference` bundle (version and integrity hash in the hook, downloaded once into `~/.cache/paduraksa-docs`). The page loads nothing from outside the site, with Scalar's fonts, telemetry, agent and MCP features off, as on the server's own `/api/docs`.
- **Anchors** are GitHub's (`pymdownx.slugs`), because the existing docs link to headings by their GitHub slug and the files are read on GitHub too.
- **Offline.** The theme uses system fonts. The build fetches nothing but that one cached bundle, and the check never fetches an external link.
- **Checks.** `mkdocs build --strict` (every internal link and anchor between pages warns, and a warning fails the build), then `scripts/docs/check-site.py`, which walks the built HTML for the links Markdown never sees (raw HTML, the theme, the generated reference).
- **CI.** The `Docs` workflow (`.github/workflows/docs.yml`) is its own file, so that it builds on every push to any branch and every pull request (`ci.yml` runs for pushes to `main` only). Its `build` job builds with `--strict`, runs the check and uploads the site (as the `docs-site` artifact and as the Pages artifact). Its `deploy` job publishes to GitHub Pages and runs only on a manual run (`workflow_dispatch`) **and** only when the repository variable `DOCS_PUBLISH` is `true`. Nothing publishes by itself, and Pages is not enabled: [development.md](../development.md#the-documentation-site) says how the owner turns it on.
- **Material's end of life.** Material for MkDocs is in maintenance until 2027-05-05, and its team points to Zensical as the successor, which reads the same `mkdocs.yml`. The build sets `NO_MKDOCS_2_WARNING=1` to keep that notice out of the log. Moving is a later job.

### Test

`ContainerSmokeTests` (trait `Category=Container`, off unless `PADURAKSA_CONTAINER_TESTS=1`) builds the image, runs it on an empty volume with random loopback ports, and asserts:
- the healthcheck turns healthy;
- the setup banner and the HTTP/3 line are in `docker logs`;
- `docker exec paduraksa setup-token` works;
- `/api/v1/setup` answers 200 over HTTPS;
- ports 80 and 443 accept a connection;
- `docker stop` exits 0 inside the 35-second grace period.

A second run with `--cap-drop ALL --cap-add NET_BIND_SERVICE` covers the setcap hardening path. It removes its containers, volumes and image.

## Fleet mode: control plane and data plane (after v0.1)

**Goal.** Run many proxy replicas (Kubernetes, or several hosts) that share certificates, ACME challenges, config and limits, without giving up what the single node is good at: a hot path with no I/O, keys that never leave a vault, and a proxy that keeps serving when everything behind it is down. Single-node (`--role all`) stays the default and gets none of the extra machinery.

### Roles

One binary: `paduraksa serve --role all|control|data`.

- **Control plane (`control`):** admin app, REST, MCP and dashboard, config store and revisions, the authoritative vault, ACME orchestration and the renewal scheduler, audit, the rollout controller and fleet statistics. It serves no proxied traffic. One replica on SQLite is enough: data nodes never touch its database, so **Postgres is only needed for a highly available control plane** (phase F4).
- **Data plane (`data`):** the public app and the hot path only. It has no config database, no admin listener and no ACME client. Its state is a local **bundle cache** (an `emptyDir` or a small volume), so it scales horizontally as a plain Deployment.
- **`all`** is both roles in one process, wired in-process exactly as today. The sync below is the in-process `ConfigChangeFeed` and `SnapshotStore` path, so single-node users never run a sync protocol.

### Sync: data nodes keep a fast local copy

Data nodes **connect out** to the control plane over one long-lived mTLS HTTP/2 stream (ASP.NET Core gRPC, first-party). Nothing connects into a data node except proxied traffic, so data nodes need no admin port and work behind NAT.

**What is synced**, content-addressed by SHA-256:
- **Config:** the canonical revision document (already canonical JSON with a hash). Each node **compiles it locally** with the same compiler. A snapshot is never shipped as an opaque blob, and the node's own certificate table and YARP binding are built the normal way.
- **Certificates and keys:** chains in plain form. Private keys only sealed (see the trust boundary below).
- **Secrets the hot path needs:** the PoW HMAC key (forward-auth holds none), sealed the same way.
- **Artifacts:** Cloudflare IP ranges, the Geo DB and error pages. Each is fetched or built once at the control plane and distributed.
- **ACME HTTP-01 tokens:** a priority channel (below).

**Delta and resume.** A node reports what it holds (the applied revision, certificate versions, artifact hashes). The control plane sends only what is missing. Every message is idempotent, so a reconnect resumes rather than restarts.

**Applying.** A node verifies hashes, compiles, publishes through its own `SnapshotStore`, then **acks** with the revision it is serving, or with the compile errors it hit. The control plane always knows which revision each node serves.

**Static stability.**
- A node that loses the control plane keeps serving its last applied bundle, which survives a restart through the bundle cache (the same idea as the last-known-good file).
- It reports staleness (`paduraksa_fleet_bundle_age_seconds`), and `/readyz` stays ready.
- Only certificate expiry limits how long a fleet can run headless, and the control plane alerts long before that.

### Trust boundary across nodes

Keys still never exist in plaintext outside a vault.

- **Enrolment.** At first start a data node's vault generates a node key pair (X25519 or P-256) and joins with a **join token** (`pdk_join_...`, single-use, or reusable with expiry and a node-group scope for autoscaled pods). The control plane's internal CA issues the node's mTLS client certificate. Nodes get a typed ID, `node_` (UuidV7), and a label set.
- **Vault-to-vault transfer.** The control vault re-wraps a certificate's key for each node's public key (ECDH + HKDF + AES-GCM, AAD = node ID, certificate ID, version). Only the node's vault can unwrap it, and it re-seals locally under its own KEK. This is a new vault capability, `IVaultTransfer`, restricted to enrolled node keys, so the key-material ban list still holds everywhere else.
- **Least privilege.** A node receives only the keys of the certificates its node group serves, so a compromised edge node leaks only its own hosts. Removing a node revokes its certificate, stops its stream and rotates nothing else. Nodes not seen for a configurable time are expired automatically (autoscaled pods come and go).

### ACME in a fleet

- Ordering, renewal and DNS-01 run only at the control plane. With an HA control plane, they run behind a leader lease (`ILeaderLease`, always "leader" on a single node).
- **HTTP-01 fan-out with acks.** The orchestrator publishes the token on the priority channel and tells the CA to validate **only after every healthy node that serves the name has acked it** (a bounded wait). Nodes that do not ack in time are listed in the order's step log. The CA's validation request can then land on any pod. Withdrawing the token fans out the same way.
- Renewed certificates are synced like any other certificate change. Certificates bypass rollouts (below): a renewal goes everywhere at once.

### Rollouts: test config changes on part of the fleet

A commit no longer means "every node, now". It sets a **target revision per node group**.

- **Node groups and labels:** `zone=eu`, `group=edge`, `canary=true`. A group can also **shard hosts**, serving only the hosts assigned to it, for example an EU edge serving EU hosts. That is also what scopes the keys above.
- **Rollout plans.** Each plan is an ordered list of stages, each a selector or a percentage of nodes plus a bake time. For example: canary node, 10 %, 50 %, 100 %, with **gates** between stages:
  - **Manual:** "advance".
  - **Automatic (phase F3):** compare the stage's nodes with the rest of the fleet on gate errors, upstream 5xx rate, p99 latency, compile failures and health-check failures. Any node that fails to compile stops the rollout.
- **Outcomes.** A failed gate pauses the rollout, or rolls it back automatically (the target returns to the previous revision) if the plan says so. An operator can also pause, resume, abort or **apply now** for emergencies.
- **Defaults.** No plan means every node gets the revision immediately, which is today's behaviour. Each rollout start, stage advance, pause and rollback is audited.
- **What a rollout changes.** Only *config revisions* are staged. Certificate changes, ACME tokens, secret rotations and artifact updates go fleet-wide immediately, because holding them back would break something, not test it.
- **What users see.** While a rollout runs, different nodes serve different revisions. The dashboard shows the fleet split per revision. The simulator can target "the revision node X is serving", and access-log entries carry the revision that answered them.

### Statistics: push the cheap things, pull the heavy ones

- **Push.** Each node sends aggregated **deltas** every 10 s on its sync stream: counters and histogram buckets from the fixed-size M9 metric slots, keyed by host, route and upstream ID, plus its upstream-health view.
  - The payload is small, works through NAT, and gives the rollout gates their data even when nobody has the dashboard open.
  - The control plane merges the deltas into the M9 rollups and exposes fleet-level `/metrics`.
- **Backpressure.** While the control plane is unreachable, a node keeps a bounded ring of deltas (for example 15 min). After that it drops the oldest deltas and counts the drops, so a reconnect backfills recent history and the hot path never blocks on statistics.
- **Pull on demand,** over the same stream, for things too heavy or too rare to push: live access-log tail from selected nodes (opened when a dashboard view asks), per-node destination health detail, connection and TLS debug, and a node's current snapshot summary.
- **Prometheus-native too.** Each data node keeps its own `/metrics` on an internal port, so a cluster that already scrapes pods can ignore the fleet aggregate.

### Rate limits across replicas

- Local token buckets on each node, with each node's share of a limit (for example the limit divided by the number of healthy nodes in the group) pushed by the control plane as part of the bundle, so the hot path stays I/O-free. This is approximate by design.
- Exact shared limits are an option for later: local buckets reconciled asynchronously, gossiped between nodes or batched to Valkey or Redis every ~100 ms, so overshoot is bounded by the sync interval.
- Never a network round trip per request.

### Upgrades and version skew

- **Upgrade order:** the control plane is upgraded first and serves data nodes of the same and the previous minor version.
- **Protocol and bundle versions:** the protocol is versioned, and bundles carry the document's `apiVersion` and a minimum compiler version. A node that cannot compile a revision acks the error and keeps serving the old one, which also stops any rollout.
- **Migrations:** with an HA control plane they become expand/contract and run once, from the leader or a Job.

### Kubernetes shape

- **Control plane:** a StatefulSet with 1 replica and a PVC (SQLite), and a Service for the admin UI and for the sync endpoint.
- **Data plane:** a Deployment with an HPA. It gets the join token from a Secret, an `emptyDir` bundle cache, a LoadBalancer Service for 80 and 443 tcp+udp (`externalTrafficPolicy: Local`, or PROXY protocol from the LB), and PodDisruptionBudgets.
- **Packaging:** a Helm chart with both roles, plus the declarative mode (see [admin-surfaces.md](admin-surfaces.md)) feeding the control plane.

### Phasing

| Phase | Scope |
|---|---|
| **F1** | Roles, enrolment, config, certificate and artifact sync with static stability, vault-to-vault key transfer, HTTP-01 fan-out with acks, pushed statistics and the fleet view in the dashboard. |
| **F2** | Node groups and labels, host sharding with scoped keys, rollout plans with manual gates. |
| **F3** | Automatic gates and auto-rollback. |
| **F4** | An HA control plane (Postgres, leader lease, LISTEN/NOTIFY between control replicas) and exact shared rate limits. |

### Seams worth keeping in the single-node code

These are cheap now and expensive later. Fold them into the milestones that touch the code anyway.

1. **Config source.** The public side gets its snapshots through a config-source abstraction (today the in-process `ConfigService` publish), so a remote bundle source can replace it.
2. **Leader lease.** The ACME scheduler and any other singleton job run through `ILeaderLease`.
3. **Challenge publishing** goes through `IAcmeChallengeStore` publish and withdraw (already a seam) and can grow acks.
4. **Int64Ordered node ID.** The layout reserves bits for a node ID before any data exists, because changing it later is a data migration.
5. **Statistics.** M9 metric slots are designed as mergeable deltas (counters and fixed histogram buckets, no per-node-only aggregates).
6. **Database portability.** SQLite-specific behaviour (`BEGIN IMMEDIATE` serialization, `NOCASE`, rowid keys) stays inside Storage and Data behind intent-named helpers, so the control plane could run on Postgres.
