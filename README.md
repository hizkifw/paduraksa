# Paduraksa

A self-hosted reverse proxy for homelabs, built on Kestrel and YARP. Deploy it once, then manage hosts, certificates and
users from a web dashboard or a REST API, with every change versioned and reversible.

> [!WARNING]
> Pre-alpha. It works end to end, but expect breaking changes until v0.1, and don't put it in front of anything that
> matters yet.

*Paduraksa*: the gate into the inner sanctum, flanked by guardian figures meant to keep evil out.

## Features

- **Proxying:** HTTP/1.1, HTTP/2 and HTTP/3, a certificate per name (SNI), HTTPS redirects, WebSockets, HSTS. Real
  client IPs from behind Cloudflare, a trusted proxy or the PROXY protocol.
- **Certificates:** ACME issuance and renewal (HTTP-01, DNS-01 via Cloudflare or deSEC, ARI renewal windows), uploaded,
  self-signed, or issued from a built-in local CA.
- **Safe config changes:** review every change as a diff before it goes live, roll back any revision, and see who did
  what in a tamper-evident audit log. Changes go live in milliseconds without dropping requests.
- **Dashboard and API:** a web dashboard with a first-run wizard, and a REST API with OpenAPI docs over the same
  operations.
- **Multiple users:** roles, two-step sign-in (TOTP) and scoped API tokens.
- **Request simulator:** ask why a request was routed, redirected or refused, against the live config or a draft.
- **Logs:** access and error logs, rotated and compressed, browsable from the dashboard.
- **Secrets stay encrypted:** certificate keys, DNS credentials and TOTP secrets live in an encrypted vault.

**Planned:** access lists and forward auth, WAF policies (rate limits, geo, proof-of-work), custom error pages, metrics,
a CLI, an MCP server, YAML import/export and backups. See the [roadmap](docs/PLAN.md#14-milestones).

## Quick start

With Docker and Compose v2:

```sh
mkdir paduraksa && cd paduraksa
curl -fsSLO https://raw.githubusercontent.com/hizkifw/paduraksa/main/deploy/compose.yaml
docker compose up -d
docker logs paduraksa 2>&1 | grep -A8 "Paduraksa first-run setup"   # the one-time setup token
```

Then open `https://<host>:9443/` and follow the setup wizard. The admin port uses Paduraksa's local CA; to stop the
browser warning, trust its root from `https://<host>:9443/api/v1/ca/root.crt`.

Map ports **80/tcp, 443/tcp, 443/udp and 9443/tcp** one to one (443/udp is HTTP/3).

The image is `ghcr.io/hizkifw/paduraksa`, for amd64 and arm64. The [install guide](docs/install.md) covers the master
key, backups, upgrades, building the image yourself and running without Docker.

## Development

You need the .NET 10 SDK.

```sh
dotnet build
dotnet test
```

The checks CI runs, the data directory, database migrations and the repository layout are in
[docs/development.md](docs/development.md). The design is in [docs/PLAN.md](docs/PLAN.md).

## License

[Apache-2.0](LICENSE). See [NOTICE](NOTICE).
