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

Properties checked on every input: no exception; the same verdict for the same bytes; 0 B where the path promises it; and, per target, a differential
against an independent reading of the same bytes (see each target's remarks).

- `Paduraksa.Fuzz.Targets` holds the targets, `Paduraksa.Fuzz` the libFuzzer driver and the `replay`, `seed` and `list` modes.
- `corpus/<target>/` is the committed corpus (the `seed-*` files are well-formed starting points; the rest is what runs found, minimised with `scripts/fuzz.sh --merge`).
- `regressions/<target>/` holds inputs that once crashed a target. Both folders are replayed on every commit by `tests/Paduraksa.Tests.Fuzz`.
- `scripts/fuzz.sh` runs the targets for a time box; `.github/workflows/fuzz.yml` does it by hand or nightly and uploads crash inputs. `docs/development.md` has the commands.
