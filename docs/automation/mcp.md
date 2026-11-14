# MCP (AI assistants)

Paduraksa has an MCP endpoint so an AI assistant can read the API description and call the API by writing code, as the API token it connects with.

**MCP is off until you turn it on**: under Settings, Server, Integrations (the Owner or an Admin; it applies at once, no restart), or with
`Paduraksa__Mcp__Enabled=true` in the environment, which then wins and locks the dashboard's switch. The tools run an assistant's JavaScript, so that is the
operator's choice for now; while it is off `/mcp` answers 404 before any authentication and no sandbox process runs, and switching it off stops the running ones.
The default may change once the sandbox below has run in homelabs for a while.

| Setting | Default | What |
|---|---|---|
| `Paduraksa__Mcp__Enabled` | `false` | Serve `/mcp` on the admin port |
| `Paduraksa__Mcp__IdleTimeout` | `00:10:00` | How long an unused sandbox process is kept (10 seconds to 24 hours); the next call starts a new one |

When it is on, the admin port serves an MCP endpoint, `https://<admin host>:9443/mcp` (streamable HTTP), with two tools: `search` reads the API description with code
and `execute` calls the API with code, as the access token the assistant connects with. The assistant writes the code; the server runs it in a **separate sandbox
process** (the same binary, started as `paduraksa mcp-sandbox`) that holds no token, no configuration and no files, has almost none of the server's environment, and
can only ask the server to make requests, which the server checks first. The server limits the process from outside and kills it the moment it steps out of line: 10
seconds on the clock (waiting for the API is not counted), 5 seconds of CPU, 384 MiB of memory (its heap is capped at 256 MiB), 50 requests, a 64 KiB result. A script
that breaks a limit costs only its own process and is told `The script was stopped: ...`. Processes are started on the first call (about 0.2 seconds, 60 MiB each
while idle), kept while the assistant is in use, up to 4 at once, and stopped after the idle time or when the server stops. The process also closes its own file system
and network (Landlock, and a seccomp filter that leaves it no socket but a local one), so a script that got out of the engine has no files to read and nowhere to send
what it saw. The server logs what the process applied once, and what it could not apply each time, and whether a cgroup v2 was available to add memory, CPU and
process-count limits (Docker's default read-only cgroup file system is fine without it). The assistant can do exactly what its token can do over the REST API and nothing else. Create the token under Settings, API tokens (the page
shows the endpoint and the snippets below with your address in them).

**Inside Docker.** The default seccomp profile of Docker 29 on Linux 7.3 lets the process apply all of it: with `Paduraksa__Mcp__Enabled=true` the first call logs
`Sandbox child hardening applied: ...; Landlock: no file system access, ABI 11 (every thread); Landlock network: no TCP bind or connect (every thread); seccomp: only AF_UNIX sockets and 12 calls denied (every thread)`.
That was checked on the image built from this repository with the compose hardening (`read_only`, `cap_drop: [ALL]`, `no-new-privileges`). Landlock's network rules need Linux
6.7 on the host; on an older kernel the line says `Landlock network: not available (ABI 3, needs 4)` and the seccomp filter, which does not depend on the kernel version, still closes
the sockets. A runtime or an older Docker whose profile blocks `landlock_create_ruleset`, `landlock_add_rule` and `landlock_restrict_self` makes the log say
`Landlock: not available (errno 1)` (with the filter still applied). Either accept that (the process is still limited and killed from outside, and the file system and TCP are then open to an
escaped script), or run the container with a seccomp profile that allows those three calls: copy your runtime's default profile, add them to the allowed names and start
the container with `--security-opt seccomp=profile.json` (Compose: `security_opt: [seccomp=./profile.json]`).

**Use a token that can only read** (the `config:read`, `logs:read`, `metrics:read`, `certificates:read` family and nothing that writes): the assistant can then
look at everything its scopes allow and change nothing. With a writing token a change is still two steps, a dry run and then a second request with `confirm: true`, and shows as
`mcp` in History and Audit with the token as the actor. Backups, API tokens, users, sessions, anything that carries a secret (DNS provider credentials, ACME
keys, notification secrets, certificate uploads) and the live tail stream are refused whatever the token holds; `GET /api/v1/logs/recent` reads recent
requests instead of the stream. Text the assistant reads from your traffic (user agents, paths) is data to it, and its descriptions say so.

**Changes go to a draft by default.** The tool descriptions tell the assistant to put config changes in your [draft](../guide/hosts-and-routes.md#drafts) unless you asked for a change to be applied directly. You then see it in the draft bar, review it and apply it yourself. A token acts on its owner's draft, so a model's change gets the same review as a person's. This is guidance to the model, not a restriction: what limits an assistant is its token's scopes and the two-step rule below.

Claude Code:

```sh
claude mcp add --transport http paduraksa https://proxy.lan:9443/mcp --header "Authorization: Bearer pdk_..."
```

Claude Desktop (`claude_desktop_config.json`, through `mcp-remote`):

```json
{
  "mcpServers": {
    "paduraksa": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "https://proxy.lan:9443/mcp", "--header", "Authorization:${PADURAKSA_TOKEN}"],
      "env": { "PADURAKSA_TOKEN": "Bearer pdk_..." }
    }
  }
}
```

The admin certificate is signed by Paduraksa's own CA, so the machine that runs the client has to trust its root (`GET /api/v1/ca/root.pem`, or Local CA in the
dashboard). Node-based clients (Claude Code, `mcp-remote`) read it from `NODE_EXTRA_CA_CERTS=/path/to/paduraksa-root.pem`. Only a bearer token is accepted at
`/mcp`: a browser session cookie never is. The endpoint is on the admin port, so `Paduraksa:Listen:AdminAllowedSources` and everything else that protects the
admin port protects it. A call can hold one of the 4 sandbox processes for up to 30 seconds, so one token can keep all four busy: give the token to assistants you
trust. The processes are children of the server: a container's memory limit has to leave room for four of them (about 60 MiB each when idle, up to 384 MiB while a
script runs).
