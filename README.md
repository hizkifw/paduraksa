# Paduraksa

**Paduraksa**: the gate into the inner sanctum, flanked by guardian figures meant to keep evil out.

An open-source, homelab-grade reverse proxy and lightweight WAF built on Kestrel + YARP.

Deploy it once, then manage everything from the dashboard, REST API, MCP or CLI.

> Status: pre-alpha. See [docs/PLAN.md](docs/PLAN.md) for the design and roadmap.

## Build

```sh
dotnet build
dotnet test
dotnet format --verify-no-changes
dotnet run --project src/Paduraksa.Server --launch-profile dev
```

Warnings are errors, and CI runs all three checks.

The server keeps its state in its data directory (`Paduraksa:DataDirectory`, default `./data` next to the shipped
`appsettings.json`, so `src/Paduraksa.Server/bin/Debug/net10.0/data/` for the `dev` profile; set
`Paduraksa__DataDirectory` to move it): the config database `paduraksa.db` and `last-good.json`, the config that was
last live. The server falls back to that file (degraded) when the database can't be used at startup, and restores the
database from it when the database has no revisions (deleted or replaced). So to start over with an empty config, stop
the server and delete both. One data directory serves one server at a time.

## Install

Docker is the supported deployment: the [`Dockerfile`](Dockerfile) builds a non-root, Ubuntu Noble image with
libmsquic for HTTP/3, and [`deploy/compose.yaml`](deploy/compose.yaml) is a one-node example that builds it.

```sh
cd deploy
docker compose up -d --build
docker logs paduraksa 2>&1 | grep -A8 "Paduraksa first-run setup"   # the one-time setup token
```

Map **80/tcp, 443/tcp, 443/udp and 9443/tcp 1:1** (the UDP port is HTTP/3; without it clients use HTTP/2). Then trust the
local CA root from `https://<host>:9443/api/v1/ca/root.crt` and open `https://<host>:9443/`.

The full guide - the master key and Docker secrets, reading or reprinting the setup token, trusting the CA root,
backups and where data lives, upgrades, UDP notes, external health checks, and a systemd unit for a bare-metal
self-contained publish ([`deploy/paduraksa.service`](deploy/paduraksa.service)) - is in [docs/install.md](docs/install.md).

## Database migrations

The config database schema (`ConfigDbContext` in `src/Paduraksa.Data`) comes from EF Core migrations in
`src/Paduraksa.Data/Migrations`, applied at startup by `ConfigDatabase.InitializeAsync` (the server's startup recompile
calls it first). After changing an entity or its mapping, add a migration with the pinned `dotnet-ef` from the local tool manifest:

```sh
dotnet tool restore
dotnet restore
dotnet ef migrations add <Name> --project src/Paduraksa.Data --output-dir Migrations
```

Commit the tool's output as is: `.editorconfig` marks the Migrations folder as generated code, so the style rules
and `dotnet format` skip it. Review it, though, like any other change. A test (`HasPendingModelChanges`) fails when
the model and the migrations disagree. Commands that open a database (`database update`, `migrations remove`) use
a scratch file in the temp directory unless you name one after `--`, e.g.
`dotnet ef database update --project src/Paduraksa.Data -- data/paduraksa.db`. Keep `dotnet-ef` in `.config/dotnet-tools.json` on the same version as the EF Core packages in
`Directory.Packages.props`.

The vault database (`VaultDbContext` in `src/Paduraksa.Vault`, `data/vault.db`) has migrations of its own in
`src/Paduraksa.Vault/Migrations`, applied at startup by the vault (`IVaultLifecycle.InitializeAsync`). Add one the same way:
`dotnet ef migrations add <Name> --project src/Paduraksa.Vault --output-dir Migrations`.

## Repository layout

- `src/`: the product projects (layering in [docs/PLAN.md](docs/PLAN.md) §1).
- `tests/`: unit (`Paduraksa.Tests.Unit`), data (`Paduraksa.Tests.Data`), proxy hot path and end-to-end
  (`Paduraksa.Tests.Proxy`), integration (`Paduraksa.Tests.Integration`) and architecture
  (`Paduraksa.Tests.Architecture`) tests, and `Paduraksa.Tests.Vault` for the vault (a key-material boundary project like the vault itself).
- `bench/`: BenchmarkDotNet micro-benchmarks (`Paduraksa.Benchmarks`), the end-to-end runner
  (`Paduraksa.Bench.Runner`) and the nginx/Caddy baseline (`run-baseline.sh`, results in `bench/results/`).
- `spikes/`: throwaway experiments, excluded from the solution. Findings live in [docs/spikes/](docs/spikes/).
- `docs/`: the design plan and spike write-ups.

## License

[Apache-2.0](LICENSE). See [NOTICE](NOTICE).
