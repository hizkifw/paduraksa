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

### Database migrations

The config database schema (`ConfigDbContext` in `src/Paduraksa.Data`) comes from EF Core migrations in
`src/Paduraksa.Data/Migrations`, applied at startup by `ConfigDatabase.InitializeAsync` (the server calls it from a
later M2 wave on). After changing an entity or its mapping, add a
migration with the pinned `dotnet-ef` from the local tool manifest:

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

## Repository layout

- `src/`: the product projects (layering in [docs/PLAN.md](docs/PLAN.md) §1).
- `tests/`: unit (`Paduraksa.Tests.Unit`), data (`Paduraksa.Tests.Data`), proxy hot path and end-to-end
  (`Paduraksa.Tests.Proxy`), integration (`Paduraksa.Tests.Integration`) and architecture
  (`Paduraksa.Tests.Architecture`) tests.
- `bench/`: BenchmarkDotNet micro-benchmarks (`Paduraksa.Benchmarks`), the end-to-end runner
  (`Paduraksa.Bench.Runner`) and the nginx/Caddy baseline (`run-baseline.sh`, results in `bench/results/`).
- `spikes/`: throwaway experiments, excluded from the solution. Findings live in [docs/spikes/](docs/spikes/).
- `docs/`: the design plan and spike write-ups.

## License

[Apache-2.0](LICENSE). See [NOTICE](NOTICE).
