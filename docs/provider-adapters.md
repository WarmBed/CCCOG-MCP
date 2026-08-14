# CCCG provider adapters

## Goal and attribution boundary

CCCG maps a Claude-facing model alias to an explicitly configured backend. The
requested alias and the actual backend identity remain separate:

```text
requested_model = claude-haiku-4-5
provider        = codex-app-server
actual_model    = gpt-5.6-luna
```

Every assistant response is prefixed with the actual provider and model. A
provider reroute is also disclosed. CCCG does not claim that Luna is Haiku or
that Codex is an Anthropic service.

## Codex CLI adapter

### Verified interface

The official OpenAI Codex repository documents `codex app-server` as a
bidirectional JSON-RPC-like protocol. With `--listen stdio://`, each message is
one JSON object per line. The installed CLI can generate version-matched JSON
schemas, so CCCG does not have to copy an assumed wire format.

CCCG uses this ordered lifecycle:

1. launch `codex app-server --listen stdio://`;
2. send `initialize` and wait for its response;
3. send `initialized`;
4. send `thread/start` with the configured model, `ephemeral=true`,
   `approvalPolicy=never`, and `sandbox=read-only`;
5. send `turn/start` with the user text and configured reasoning effort;
6. collect completed `agentMessage` items;
7. finish only after the matching `turn/completed` event;
8. fail closed on server-initiated approval requests.

Codex stdout is private to the adapter. Claude-compatible JSONL is written only
by the outer router. Codex stderr is redacted and copied to the independent
router event log.

The official Codex Python and TypeScript SDKs are also valid embedding options.
The current C# implementation talks directly to the documented app-server
protocol so it can keep the CCCG executable self-contained.

### Local verification on 2026-08-13 (Asia/Taipei)

Environment:

- Codex CLI: `0.146.0-alpha.14`
- authentication status: logged in through ChatGPT
- target configuration: `config/routes.luna.json`
- Claude-facing alias: `claude-haiku-4-5`
- Desktop-observed alias: `claude-haiku-4-5-20251001`
- requested Codex model: `gpt-5.6-luna`
- prompt: `Reply only CCCG-CODEX-LUNA-OK`

Observed result:

- router exit code: `0`
- router stdout: three lines; every line parsed as JSON
- frames: `system/init`, `assistant`, `result/success`
- assistant disclosure: `[CCCG provider=codex-app-server model=gpt-5.6-luna]`
- response marker: `CCCG-CODEX-LUNA-OK`
- requested backend: `gpt-5.6-luna`
- actual backend: `gpt-5.6-luna`
- rerouted: `false`
- measured backend turn duration: `11527 ms`
- router event count: `12`
- credential-pattern matches in the event file: `0`

This proves the standalone translation path. It does not by itself prove that a
specific Claude Desktop release will accept CCCG as its child process.

A later exact-alias validation used `claude-haiku-4-5-20251001`. It completed
in 10.9 seconds with three JSON-only stdout frames and the marker
`CCCG-DESKTOP-ALIAS-LUNA-OK`. One preceding attempt hit the outer harness's
120-second limit, so the router now emits content-free app-server lifecycle
metadata and fails the backend turn after 120 seconds instead of hanging
indefinitely.

## Grok Build CLI adapter

### Verified interface

xAI now publishes the official `xai-org/grok-build` repository. The CLI
supports three useful integration levels:

1. **ACP stdio** — `grok agent stdio`; preferred for a stateful, streamed CCCG
   adapter because session and event semantics are structured.
2. **Headless single turn** — `grok -p ... --output-format streaming-json` (or
   `plain`); useful for a small one-shot adapter.
3. **Official Claude Code plugin** — `xai-org/grok-build-plugin-cc`; it shells
   out to the real `grok` CLI for review/delegation and explicitly states that
   it is not an app-server broker. This is prior art for Claude-to-Grok
   interoperability, not model impersonation.

The official plugin's read-only invocation pattern is:

```text
grok -p <prompt> --agent explore --permission-mode plan \
  --sandbox read-only --cwd <workspace> --output-format plain
```

For CCCG, ACP is the intended long-term adapter. The headless pattern is a
reasonable first vertical slice if the output parser is tested against recorded
synthetic event fixtures.

### Local readiness verification

- Grok Build CLI: `1.0.3 (1a29d5bc12) [stable]`
- executable: `%USERPROFILE%\\.grok\\bin\\grok.exe`
- login: grok.com session present (`grok models` succeeded)
- locally advertised models: `grok-4.6` (default), `grok-4.5`
- ACP entrypoint: `grok agent stdio`
- headless formats: `plain`, `json`, `streaming-json`, and
  `streaming-messages-json`

No Grok generation was sent during the Luna validation. Readiness was verified
without consuming an extra Grok model turn.

## Monitoring contract

Router stdout is reserved for Claude-compatible JSONL. Live diagnostics are
duplicated to stderr and to:

```text
%LOCALAPPDATA%\\CCCG\\router-data\\router-<timestamp>-<pid>.jsonl
```

The log records requested alias, provider, requested backend model, actual
backend model, reroute status, timings, and redacted backend diagnostics. It
records prompt length but not prompt text.

## References

- OpenAI Codex app-server:
  <https://github.com/openai/codex/tree/main/codex-rs/app-server>
- OpenAI Codex Python SDK:
  <https://github.com/openai/codex/tree/main/sdk/python>
- OpenAI Codex TypeScript SDK:
  <https://github.com/openai/codex/tree/main/sdk/typescript>
- xAI Grok Build:
  <https://github.com/xai-org/grok-build>
- xAI Grok Build Claude Code plugin:
  <https://github.com/xai-org/grok-build-plugin-cc>
