# Fuzzing

Coverage-guided fuzzing of the byte-level parsers that read raw attacker input (docs/PLAN.md §15), with SharpFuzz and libFuzzer.

| Target | What it fuzzes |
|---|---|
| `proxy-protocol` | PROXY protocol v1 and v2 headers, in one segment and in two |
| `pow-cookie` | the `paduraksa_pow` cookie: the header scan and the token check |
| `pow-form` | the posted challenge form: field parser, challenge, solution and return target |
| `basic-auth` | `Authorization: Basic` |
| `client-address` | `CF-Connecting-IP`, `X-Forwarded-For`, `True-Client-IP` and `CF-IPCountry` through the client address resolver in every trusted-proxy mode |
| `forward-auth-answer` | a forward-auth provider's answer: relayed headers and body, `Location`, identity headers (no socket: the provider is a handler) |
| `error-page` | the operator's custom error pages through the real gate, with the client's `Host`, a trusted peer's `X-Request-Id`, `CF-Ray` and `Accept` in every variable, as a message and as a document |
| `upstream-error-replacement` | whether an upstream's own error answer is replaced by the host's page: status, `Content-Type`, mode (`off`, `html`, `any`), upgrade and a started response |
| `access-log-tail` | a request's path, query, host, request ID, referer and user agent through the access-log file line and the live tail's entry, which must say the same thing |
| `yaml-import` | the config YAML an import reads: the guarded reader (caps on size, depth, nodes with aliases expanded, scalars, anchors), the mapping onto the document, merge and replace, and that an accepted document exports and reads back to the same canonical bytes |
| `mcp-sandbox` | a model's JavaScript through the MCP sandbox (`search` over a spec, `execute` against a recording stub): it returns within the limits and never throws, the output stays under its cap, nothing outside the globals is reachable, and what reaches the stub is what the request policy allows |

Properties checked on every input: no exception; the same verdict for the same bytes; 0 B where the path promises it; and, per target, a differential
against an independent reading of the same bytes (see each target's remarks).

- `Paduraksa.Fuzz.Targets` holds the targets, `Paduraksa.Fuzz` the libFuzzer driver and the `replay`, `seed` and `list` modes.
- `corpus/<target>/` is the committed corpus (the `seed-*` files are well-formed starting points; the rest is what runs found, minimised with `scripts/fuzz.sh --merge`).
- `regressions/<target>/` holds inputs that once crashed a target. Both folders are replayed on every commit by `tests/Paduraksa.Tests.Fuzz`.
- `scripts/fuzz.sh` runs the targets for a time box; `.github/workflows/fuzz.yml` does it by hand or nightly and uploads crash inputs. `docs/development.md` has the commands.
