# MCP (AI assistants)

Paduraksa has an MCP endpoint, so an AI assistant can read the API description and call the API by writing code. It acts as the API token it connects with, and can do exactly what that token can do over the REST API.

**MCP is off by default.** The tools run an assistant's JavaScript, so turning it on is your choice. While it is off, `/mcp` answers 404 before any authentication.

- Turn it on under Settings, Server, Integrations (Owner or Admin). It applies at once, with no restart.
- Or set `Paduraksa__Mcp__Enabled=true` in the environment. That wins and locks the dashboard's switch.

| Setting | Default | What |
|---|---|---|
| `Paduraksa__Mcp__Enabled` | `false` | Serve `/mcp` on the admin port |
| `Paduraksa__Mcp__IdleTimeout` | `00:10:00` | How long an unused sandbox process is kept (10 seconds to 24 hours). The next call starts a new one |

## What it serves

`https://<admin host>:9443/mcp` (streamable HTTP) has two tools:

- `search` reads the API description with code.
- `execute` calls the API with code.

Create the token under Settings, API tokens. The page shows the endpoint and the snippets below with your address in them.

## How the assistant's code is contained

The server runs the assistant's code in a separate sandbox process, started as `paduraksa mcp-sandbox`. The process holds no token, no configuration and no files. It can only ask the server to make requests, and the server checks each one.

The server stops a script that goes over a limit, and the script is told `The script was stopped: ...`. Only its own process is lost. The limits:

- 10 seconds on the clock (waiting for the API is not counted);
- 5 seconds of CPU;
- 384 MiB of memory;
- 50 requests;
- a 64 KiB result.

Up to 4 sandbox processes run at once. The first call starts one, which takes about 0.2 seconds and 60 MiB while idle. A call can hold one for up to 30 seconds, so one token can keep all four busy: give the token only to assistants you trust. A container's memory limit has to leave room for four of them, up to 384 MiB each while a script runs.

The process also closes its own file system and network access. A script that escaped the engine would have no files to read and nowhere to send what it saw. The server logs what it applied, and what it could not.

**Inside Docker,** the default seccomp profile lets the process apply all of it. If the log says `Landlock: not available (errno 1)`, your runtime's profile blocks `landlock_create_ruleset`, `landlock_add_rule` and `landlock_restrict_self`.

- The process is still limited and killed from outside, but a script that escaped could read files and open TCP connections.
- To fix it, copy your runtime's default seccomp profile and add those three calls to the allowed names. Then start the container with `--security-opt seccomp=profile.json` (Compose: `security_opt: [seccomp=./profile.json]`).
- On a host kernel older than 6.7, the log says `Landlock network: not available`. The seccomp filter still closes the sockets.

## What an assistant can change

**Use a token that can only read.** That means `config:read`, `logs:read`, `metrics:read`, `certificates:read` and nothing that writes. The assistant can then look at everything its scopes allow and change nothing.

With a writing token:

- A change is still two steps: a dry run, then a second request with `confirm: true`.
- It shows as `mcp` in History and Audit, with the token as the actor.
- Backups, API tokens, users, sessions, anything that carries a secret (DNS provider credentials, ACME keys, notification secrets, certificate uploads) and the live tail stream are refused whatever the token holds. `GET /api/v1/logs/recent` reads recent requests instead of the stream.
- Text the assistant reads from your traffic, such as user agents and paths, is data to it. Its descriptions say so.

**Changes go to a draft by default.** The tool descriptions tell the assistant to put config changes in your [draft](../guide/hosts-and-routes.md#drafts) unless you asked for a direct apply. You then see the change in the draft bar, review it and apply it yourself. A token acts on its owner's draft, so a model's change gets the same review as a person's.

This is guidance to the model, not a restriction. What limits an assistant is its token's scopes and the two-step rule.

**It checks its draft before it reports.** After adding entries, the assistant reviews the draft and simulates requests against it (`POST /api/v1/simulate` with `{kind: "draft", draft: "<id>"}`):

- requests the change is meant to affect;
- where it matters, one it must not break, such as the host's normal path still forwarding.

If a simulation shows the change is not what you asked for, it fixes the draft first. It then tells you what it drafted and what it checked, one line per simulated outcome. When you ask for a direct apply, it simulates first too.

Simulating changes nothing. It needs a token that can write config (the draft is its owner's) and `simulate`.

## Connect a client

Claude Code:

```sh
# Add the server, with your token
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

The admin certificate is signed by Paduraksa's own CA, so the machine that runs the client has to trust its root (`GET /api/v1/ca/root.pem`, or Local CA in the dashboard). Node-based clients (Claude Code, `mcp-remote`) read it from `NODE_EXTRA_CA_CERTS=/path/to/paduraksa-root.pem`.

Only a bearer token is accepted at `/mcp`, never a browser session cookie. The endpoint is on the admin port, so `Paduraksa:Listen:AdminAllowedSources` and everything else that protects the admin port protects it.
