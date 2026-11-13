# Installing Paduraksa

The install guide moved into the documentation site's pages, one task each, so that every fact has one home. This page is the map from the old sections to them.

Start with [Getting started](guide/getting-started.md).

| Section of the old install guide | Now |
|---|---|
| Docker, the compose file, ports, capabilities, bind mounts | [Running with Docker](operating/docker.md) |
| The setup token, trusting the local CA root, the first sign-in | [Getting started](guide/getting-started.md) |
| List settings (`AdminAllowedSources` and others) | [Server settings](operating/settings.md#list-settings) |
| UDP and HTTP/3 | [TLS and certificates](guide/tls.md#http3) |
| Exposing the admin | [Exposing the admin port](operating/admin-access.md) |
| Connection floods and slow clients | [Connections, limits and client addresses](guide/connections-and-limits.md) and [Server settings](operating/settings.md#connection-and-timeout-limits) |
| WebSockets and event streams, HTTPS upstreams | [Upstreams](guide/upstreams.md) |
| Where things live | [Running with Docker](operating/docker.md#where-things-live) |
| Backups | [Backups and restore](operating/backups.md) |
| Exporting and importing the config | [Export and import](operating/export-import.md) |
| Logs, the access log, metrics, analytics, notifications | [Logs, metrics and notifications](guide/observability.md) |
| MCP (AI assistants) | [MCP](automation/mcp.md) |
| Upgrading | [Upgrading](operating/upgrading.md) |
| Changing the master key | [The master key](operating/master-key.md#changing-the-master-key) |
| Health | [Health endpoints](operating/health.md) |
| Bare metal | [Bare metal (systemd)](operating/bare-metal.md) |

## The master key

The master key opens the vault, and the dashboard links here from the Overview when the key is stored beside it. The page for it is [The master key](operating/master-key.md): where it is read from, how to move it out of the data directory, and how to change it.
