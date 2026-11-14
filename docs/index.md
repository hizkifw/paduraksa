---
template: home.html
hide:
  - toc
---

<!-- The landing page. The hero (logo, tagline, buttons, screenshot) is in overrides/home.html; the docs' introduction is introduction.md. -->

## What it does

<div class="pdk-features" markdown>

<div markdown>
### [HTTP/1.1, HTTP/2 and HTTP/3](guide/connections-and-limits.md)
A certificate per name, WebSockets and HSTS, and the real client address from behind Cloudflare, a trusted proxy or the PROXY protocol.
</div>

<div markdown>
### [Automatic certificates](guide/tls.md)
ACME issuance and renewal (HTTP-01, DNS-01 with Cloudflare or deSEC), and a built-in local CA for LAN names.
</div>

<div markdown>
### [Access control](guide/security.md)
Access lists, rule sets, rate limits, a proof-of-work browser check, and forward-auth to authentik, Authelia or oauth2-proxy.
</div>

<div markdown>
### [A review before every change](guide/hosts-and-routes.md#review-apply-and-undo)
Each change is shown as a diff before it goes live. Collect changes in a draft, and roll back any revision.
</div>

<div markdown>
### [Logs, metrics and analytics](guide/observability.md)
Access and error logs, a live tail with each request's timing, Prometheus metrics, traffic analytics and notifications.
</div>

<div markdown>
### [REST API, YAML, CLI and MCP](automation/api.md)
An OpenAPI-described API, YAML import and export, a command line, and an MCP endpoint for AI assistants.
</div>

<div markdown>
### [Secrets stay encrypted](operating/master-key.md)
Certificate keys, DNS credentials, TOTP secrets and access-list passwords are kept in a vault that one master key opens.
</div>

</div>

## Quick start

With Docker and Compose v2:

```sh
mkdir paduraksa && cd paduraksa
curl -fsSLO https://raw.githubusercontent.com/hizkifw/paduraksa/main/deploy/compose.yaml
head -c 32 /dev/urandom | base64 | sudo install -m 400 -o 10001 -g 10001 /dev/stdin master.key
docker compose up -d
docker logs paduraksa 2>&1 | grep -A8 "Paduraksa first-run setup"   # the one-time setup token
```

Keep a copy of `master.key` somewhere other than this machine: nothing else opens the vault. Then open `https://localhost:9443/` on the server and create the Owner with the setup token. [Getting started](guide/getting-started.md) goes on from there to a service behind HTTPS.

!!! warning "Pre-alpha"
    v0.1, the first release, is coming. Until it is out, expect breaking changes, and do not put Paduraksa in front of anything that matters yet.

<footer class="pdk-landing-footer" markdown>
[Apache-2.0](https://github.com/hizkifw/paduraksa/blob/main/LICENSE) [GitHub](https://github.com/hizkifw/paduraksa) [Documentation](introduction.md)
</footer>
