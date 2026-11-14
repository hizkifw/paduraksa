# Introduction

A self-hosted reverse proxy for homelabs, built on Kestrel and YARP. Deploy it once, then manage hosts, certificates and users from a web dashboard or a REST API, with every change reviewed first, versioned and reversible.

!!! warning "Pre-alpha"
    It works end to end, and v0.1, the first release, is coming. Until it is out, expect breaking changes, and do not put it in front of anything that matters yet.

*Paduraksa*: the gate into the inner sanctum, flanked by guardian figures meant to keep evil out.

## What it does

- **Proxying:** HTTP/1.1, HTTP/2 and HTTP/3, a certificate per name (SNI), HTTPS redirects, WebSockets, HSTS. Real client addresses from behind Cloudflare, a trusted proxy or the PROXY protocol.
- **Certificates:** ACME issuance and renewal (HTTP-01, DNS-01 with Cloudflare or deSEC), uploaded and self-signed certificates, and a built-in local CA for LAN names.
- **Safe changes:** review every change as a diff before it goes live, collect changes in a draft, roll back any revision, and see who did what in a tamper-evident audit log.
- **Access control and WAF:** access lists with addresses, countries and basic-auth users, rule sets, rate limits, request limits, a proof-of-work browser check, a managed `robots.txt`, and forward-auth to authentik, Authelia or oauth2-proxy.
- **Insight:** access and error logs, a live tail, metrics (Prometheus too), traffic analytics, a request simulator and notifications.
- **Automation:** a REST API with an OpenAPI description, YAML import and export, a command line, and an MCP endpoint for AI assistants.
- **Secrets stay encrypted** in a vault: certificate keys, DNS credentials, TOTP secrets and access-list passwords.

## Where to start

| If you want to | Read |
|---|---|
| Run it and put your first service behind it | [Getting started](guide/getting-started.md) |
| Understand hosts, routes, profiles and drafts | [Hosts and routes](guide/hosts-and-routes.md) |
| Get HTTPS for a public name or a LAN name | [TLS and certificates](guide/tls.md) |
| Lock a service down | [Access control and WAF](guide/security.md) |
| Install it properly, back it up, upgrade it | [Operating it](operating/docker.md) |
| Script it | [The REST API](automation/api.md), [The command line](automation/cli.md) |
| Find out how it works, or what it defends against | [Threat model](threat-model.md), [Design overview](PLAN.md) |
| Fix something that does not work | [Troubleshooting](operating/troubleshooting.md) |

## The image

`ghcr.io/hizkifw/paduraksa`, for amd64 and arm64. The source, issues and releases are on [GitHub](https://github.com/hizkifw/paduraksa). Paduraksa is licensed under [Apache-2.0](https://github.com/hizkifw/paduraksa/blob/main/LICENSE).
