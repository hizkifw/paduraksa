# Paduraksa

**Paduraksa**: the gate into the inner sanctum, flanked by guardian figures meant to keep evil out.

An open-source, homelab-grade reverse proxy and lightweight WAF built on Kestrel + YARP.

Deploy it once, then manage everything from the dashboard, REST API, MCP or CLI.

> Status: pre-alpha. See [docs/PLAN.md](docs/PLAN.md) for the design and roadmap.

## Build

```sh
dotnet build
dotnet test
dotnet run --project src/Paduraksa.Server --launch-profile dev
```

## License

[Apache-2.0](LICENSE). See [NOTICE](NOTICE).
