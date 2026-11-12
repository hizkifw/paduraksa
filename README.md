# Paduraksa

A self-hosted reverse proxy for homelabs, built on Kestrel and YARP. Deploy it once, then manage hosts, certificates and
users from a web dashboard or a REST API, with every change versioned and reversible.

> [!WARNING]
> Pre-alpha. It works end to end, but expect breaking changes until v0.1, and don't put it in front of anything that
> matters yet.

*Paduraksa*: the gate into the inner sanctum, flanked by guardian figures meant to keep evil out.

<img width="960" height="560" alt="192 168 0 254_9443_ (1)" src="https://github.com/user-attachments/assets/3c7bda95-7af7-4b29-8e34-111ed743dd67" />

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
- **Access control and WAF:** named access lists (addresses, countries by `CF-IPCountry`, and basic-auth users whose
  passwords live in the vault), rule sets (path, method and user-agent rules, with presets), rate limits, request size
  limits, a proof-of-work browser check, a managed `robots.txt`, and Cloudflare origin lockdown with Authenticated
  Origin Pulls.
- **Sign-in with your identity provider:** forward-auth to authentik, Authelia, oauth2-proxy or any endpoint that
  answers allow or refuse, per host or route, with the provider's identity headers passed upstream.
- **Security overview:** what protects each host and how much work each protection has done.
- **Request simulator:** ask why a request was routed, redirected or refused, against the live config or a draft.
- **Logs:** access and error logs, rotated and compressed, browsable from the dashboard.
- **AI assistants (MCP):** an MCP endpoint with two tools, `search` and `execute`, that let an assistant read the API description and call the
  API by writing code, as the API token it connects with. A read-only token can change nothing; a change is a dry run and then a confirmed request,
  recorded as MCP in the audit log. See [docs/install.md](docs/install.md#mcp-ai-assistants).
- **Secrets stay encrypted:** certificate keys, DNS credentials and TOTP secrets live in an encrypted vault.

**Planned:** custom error pages, metrics, a CLI, YAML import/export and backups. See the [roadmap](docs/PLAN.md#14-milestones).

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
[docs/development.md](docs/development.md). The design is in [docs/PLAN.md](docs/PLAN.md). Performance against nginx,
Caddy, HAProxy, Traefik, Envoy and a bare YARP reference is in [bench/RESULTS.md](bench/RESULTS.md).

## License

[Apache-2.0](LICENSE). See [NOTICE](NOTICE).
