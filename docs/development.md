# Developing Paduraksa

You need the .NET 10 SDK. Docker is optional; the tests that need it (Pebble, the container smoke test) skip without it.

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

## The data directory

The server keeps its state in `Paduraksa:DataDirectory` (default `./data` next to the shipped `appsettings.json`, so
`src/Paduraksa.Server/bin/Debug/net10.0/data/` for the `dev` profile; set `Paduraksa__DataDirectory` to move it). It
holds the config database `paduraksa.db` and `last-good.json`, the config that was last live.

The server falls back to `last-good.json` (degraded) when the database can't be used at startup, and restores the
database from it when the database has no revisions. So to start over with an empty config, stop the server and delete
both. One data directory serves one server at a time.

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

## Repository layout

- `src/`: the product projects (layering in [PLAN.md](PLAN.md) §1).
- `tests/`: unit (`Paduraksa.Tests.Unit`), data (`Paduraksa.Tests.Data`), proxy hot path and end-to-end
  (`Paduraksa.Tests.Proxy`), integration (`Paduraksa.Tests.Integration`), architecture (`Paduraksa.Tests.Architecture`)
  and vault (`Paduraksa.Tests.Vault`, a key-material boundary project like the vault itself) tests.
- `bench/`: BenchmarkDotNet micro-benchmarks, the end-to-end runner and the nginx/Caddy baseline
  ([bench/README.md](../bench/README.md)).
- `spikes/`: throwaway experiments, excluded from the solution. Their findings are in [spikes/](spikes/).
- `docs/`: the design plan, the install guide, this page and the spike write-ups.
