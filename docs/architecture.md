# Architecture

## Goal

Prove whether a local process can accept a narrow Claude-style JSONL input stream,
route a model alias to an explicitly configured provider, and return a compatible
assistant/result event sequence.

The read-only monitoring plane is separate from that paused routing plane:

```text
Claude Desktop files/process metadata
        |
        | read-only observation
        v
  cccg-monitor worker  <---- health-checked handoff ---->  next worker
        ^
        |
    cccg-host supervisor
```

The supervisor never launches, stops, or modifies Claude. It controls only
workers staged beneath its own SHA-256-addressed state directory. Monitor
handoffs use a ready-before-stop sequence and stable logical keys for overlap
deduplication.

```text
test host / authorized client
        |
        | Claude-style JSONL subset
        v
  cccg-router.exe
        |
        +-- exact route lookup
        +-- protocol translation
        +-- provider disclosure
        |
        +--> mock
        +--> codex app-server (official stdio JSONL)
        `--> xAI Responses API (official HTTPS API)
```

## Trust boundaries

1. The host owns stdin/stdout and chooses the requested Claude model alias.
2. `routes.json` is the only authority mapping an alias to a backend.
3. Credentials remain owned by the official provider client or environment.
4. The router emits protocol JSON on stdout and diagnostics on stderr.
5. Each answer includes `[CCCG provider=... model=...]` to prevent deceptive
   attribution.

## Codex lifecycle

CCCG follows the official app-server sequence:

1. start `codex app-server --listen stdio://`;
2. send `initialize`, then `initialized`;
3. create an ephemeral, read-only thread for the configured backend model;
4. send `turn/start` with a text input;
5. collect completed agent messages;
6. finish only when `turn/completed` reports a terminal status.

The app-server client identifies itself as `cccg_router`. It does not pose as an
OpenAI or Anthropic first-party client.

## xAI lifecycle

CCCG calls `POST https://api.x.ai/v1/responses` with `store: false`, an explicit
model, a disclosure-oriented system message, and the user's text. It extracts
`output[].content[].output_text`. No xAI server-side tools are enabled.

## Fail-closed behavior

- missing route: reject;
- missing configuration: reject;
- missing `XAI_API_KEY`: reject before any network request;
- malformed JSONL: ignore safely;
- unsupported provider: configuration load fails;
- backend failure: emit a Claude-style error result;
- unmapped passthrough: allowed only when an explicit original executable path
  is configured; it is not implemented in this first vertical slice.
