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
