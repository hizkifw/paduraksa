# About these pages

These pages are for people changing Paduraksa: contributors and the agents that work on the code. They say how it is built and why, with the invariants, budgets and decisions a change has to respect. They are not the guide.

To use Paduraksa, start with the [user guide](../guide/getting-started.md). To run it, see [Operating it](../operating/docker.md).

## Start here

- [Working on the code](../development.md): build, test and format, and how to write tests that don't flake.
- [Overview](../PLAN.md): what Paduraksa is, how a request flows through it, the development rules and where the project stands.

## Pages by area

| Page | What it covers |
|---|---|
| [Process and hosting](hosting.md) | The two web apps and the core host in one process, listeners and ports, the admin certificate |
| [Config](config.md) | Typed IDs, the data model, settings layers, and how a change becomes a snapshot |
| [The hot path](hot-path.md) | The request budget, the pipeline, connection limits, HTTP/3 |
| [WAF building blocks](waf.md) | Rate limits, access lists, rule sets, forward-auth, proof of work, error pages |
| [Client IP behind Cloudflare](cloudflare.md) | Trusted proxies, edge ranges, origin lockdown |
| [Vault, certificates and ACME](vault-and-certificates.md) | Key material, the certificate inventory, the local CA, ACME and DNS-01 |
| [Identity](identity.md) | Users, sessions, roles and the admin sign-in pipeline |
| [Admin surfaces](admin-surfaces.md) | The REST API, MCP, the CLI, YAML import and export |
| [The dashboard](dashboard.md) | The Blazor app, drafts, conventions, first-run setup |
| [Observability](observability.md) | Logs, metrics, live tail, analytics, health and notifications |
| [Deployment](deployment.md) | The container, compose, bare metal, SQLite tuning |
| [Testing](testing.md) | Test levels, property tests and fuzzing |
| [Threat model (full analysis)](threat-model.md) | The analysis behind the short [threat model](../threat-model.md) |
| [Risks, spikes and milestones](milestones.md) | What each milestone built, and the spikes that de-risked the design |

The spikes ([S1](../spikes/S1-http3-sni.md), [S2](../spikes/S2-yarp-reload.md), [S3](../spikes/S3-zero-alloc-gate.md)) are the experiments behind three of those decisions.
