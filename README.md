# CCCG

CCCG is a local interoperability experiment that translates a small, documented
subset of Claude Code's stream-JSON process protocol into explicit backend
adapters. It is designed for controlled testing, not for concealing which
provider answered.

Every generated answer is prefixed with the actual provider and backend model.
CCCG does not patch Claude binaries, reuse Claude credentials, bypass billing or
authentication, suppress product updates, or claim that a third-party model is
an Anthropic model.

## Current milestone: monitor plus reversible Desktop experiment

- live tail of Claude Desktop `main.log` lifecycle events
- session metadata changes without title, cwd, prompt, or response content
- transcript file activity by size/timestamp
- optional local capture of newly appended user prompts and visible assistant text
- tool request/result and stop-hook lifecycle observation, including WebSearch classification
- Claude Desktop/engine process start and exit observation
- stable pseudonymous session correlation across monitor runs
- duplicate-log suppression
- payload-free test markers
- normalized local JSONL datasets and an interactive terminal dashboard
- supervised monitor worker updates without restarting Claude Desktop

Content capture is disabled by default. `watch --capture-content` writes prompt
and visible response text to a separate `content.jsonl`; it does not capture
thinking, tool inputs/results, attachments, cwd, credentials, or historical
transcript contents. Local datasets are ignored by source control and must not
be published.

`cccg-host` can supervise the monitor and hand off to a new immutable worker
version after readiness and dataset validation. See the
[hot-update guide](docs/hot-update.md) and
[validation evidence](docs/hot-update-validation.md).

The router implements deterministic mock and Codex app-server vertical slices.
The verified mapping recognizes both `claude-haiku-4-5` and the Desktop alias
`claude-haiku-4-5-20251001`, and sends those aliases to Codex
`gpt-5.6-luna`. Every answer discloses the actual provider and model.

An explicitly authorized, reversible Windows host experiment is packaged but
is not installed automatically. It preserves the existing cross-session bridge
shim as a sidecar, routes only Haiku to Luna, and passes every other invocation
through byte-for-byte. See the
[Desktop Luna experiment guide](docs/desktop-luna-experiment.md).

The separate Claude Desktop `2.1.229` cross-session recovery Shim is maintained
under [experiments/claude-desktop-bridge-shim](experiments/claude-desktop-bridge-shim/README.md).
It is a reversible local workaround for issue `#86012`, not an upstream fix and
not part of the model-routing experiment. Its installer is fail-closed on
version, SHA-256, Authenticode, live Claude processes, and recovery readiness.

## Build and test

```powershell
dotnet build .\CCCG.sln -c Release
dotnet run --project .\tests\CCCG.Tests\CCCG.Tests.csproj -c Release
```

Start the supervised monitor:

```powershell
cccg-host.exe run --worker .\cccg-monitor.exe --capture-content
```

Apply a later monitor build without restarting Claude:

```powershell
cccg-host.exe update --worker D:\path\to\new\cccg-monitor.exe
```

Mock smoke test:

```powershell
dotnet .\src\CCCG.Router\bin\Release\net8.0\cccg-router.dll `
  --model claude-haiku-4-5 `
  --cccg-config .\config\routes.mock.json
```

The isolated Codex Luna route is in `config/routes.luna.json`. See
[provider adapters](docs/provider-adapters.md) for the exact protocol and
verification evidence.

Add a test marker immediately before a synthetic cross-session test:

```powershell
dotnet run --project .\src\CCCG.Monitor\CCCG.Monitor.csproj -c Release -- \
  mark --label CROSS-IDLE-001
```

The Dispatch MCP keeps Claude as coordinator and delegates to versioned Codex
or Grok workers. It supports deterministic new-session binding, cross-process
FIFO dispatch, background survival, automatic wait-and-return, and Worker hot
updates. See [dispatch](docs/dispatch.md) and
[dispatch validation](docs/dispatch-validation.md).

See the [monitor guide](docs/monitor.md), [architecture](docs/architecture.md),
[provider adapters](docs/provider-adapters.md), [test plan](docs/test-plan.md),
the [Desktop Luna experiment](docs/desktop-luna-experiment.md), and
[safety boundaries](docs/safety.md).

## Important limitation

The standalone route and passthrough chain are verified. Acceptance inside the
currently installed Claude Desktop release still requires a manual A/B after
Claude is fully stopped and the reversible wrapper is installed. The Luna route
currently supports text turns, interrupt/error lifecycle, and attribution; it
does not reproduce Claude tools, hooks, web search, or session resume semantics.
