# Reproduction and test plan

## Phase 0: read-only lifecycle baseline (current)

Follow [monitor.md](monitor.md) and collect the labeled active, idle, round-trip,
and burst cross-session cases before implementing more router behavior.

Acceptance:

- monitor startup and shutdown do not change Claude process count except for the
  monitor itself;
- no raw session IDs, prompt/response bodies, cwd, titles or credentials occur
  in `events.jsonl`; opt-in prompt/response text remains isolated in
  `content.jsonl`;
- duplicate Desktop log lines normalize to one event;
- test markers appear live in the same run dataset;
- transcript activity and health outcomes can be correlated to each labeled test;
- stopping/restarting the monitor does not affect any Claude session.

Before any engine A/B test, also collect one synthetic baseline of each host
operation:

1. Ask Claude to run `WebSearch` for a unique harmless marker; require matching
   `session.tool_requested` and `session.tool_completed` operation keys with
   `toolKind=web_search` and `isError=false`.
2. Trigger an installed harmless stop hook; require `session.hook_summary` with
   the expected hook count and zero hook errors.
3. Run one filesystem or shell tool against a temporary test file; require a
   matched non-error completion.
4. Repeat the same three cases after changing the engine. Text success alone is
   a failure if any host operation is missing, unmatched, or errors.

## Phase 0.5: monitor hot update (validated)

Follow [hot-update.md](hot-update.md). Acceptance requires:

- candidate executable is staged under its verified SHA-256;
- candidate PID, generation, dataset and manifest validate before promotion;
- old worker remains alive through the configured overlap window;
- an overlap transcript entry is present after unioning the two datasets;
- duplicates share a stable logical key;
- old worker exits cooperatively after promotion;
- a candidate that exits before ready leaves the prior worker unchanged;
- host stop/update never signals a Claude PID.

The recorded synthetic evidence is in
[hot-update-validation.md](hot-update-validation.md).

## Phase 1: protocol-only baseline (complete)

Purpose: prove the router independent of Claude Desktop and paid APIs.

1. Build Release.
2. Start the router with `routes.mock.json` and alias `claude-haiku-4-5`.
3. Send one synthetic stream-JSON user frame.
4. Assert stdout contains exactly an init frame, assistant frame, and success
   result, with no non-JSON diagnostics.
5. Assert the response discloses `provider=mock` and `model=gpt-5.6-sol`.
6. Repeat with `set_model` to the Sonnet alias and verify routing changes to the
   configured Grok mock target.
7. Send an unmapped alias and verify a fail-closed error result.

## Phase 2: Codex app-server integration

Prerequisites: an authenticated local `codex` CLI.

1. Route one alias to `codex-app-server`.
2. Send the synthetic prompt `Reply only CCCG-CODEX-OK`.
3. Confirm the response terminates and includes the provider disclosure plus the
   expected marker.
4. Confirm stderr contains no credential material.
5. Confirm the Codex thread is ephemeral, approval policy is `never`, and sandbox
   is `read-only`.
6. Interrupt a running turn and verify a receipt/error rather than a hung host.

Current evidence: the standalone Haiku alias to Luna path completed successfully
on 2026-08-13. See [provider-adapters.md](provider-adapters.md). Host acceptance
and live interrupt testing remain separate from this standalone result.

## Phase 3: xAI integration

Prerequisites: explicit user authorization to spend API quota and a caller-owned
`XAI_API_KEY` environment variable.

1. Route one alias to `xai-responses`.
2. Send `Reply only CCCG-XAI-OK`.
3. Confirm the response discloses xAI and the actual configured model.
4. Remove the key and verify fail-closed behavior before any request.

No paid live call is part of the default automated suite.

## Phase 4: authorized host experiment

This is deliberately separate from building CCCG.

1. Record the host version and executable hashes.
2. Back up only the exact files the tester owns and is authorized to alter.
3. Use a disposable test account/session and synthetic prompts.
4. Capture process path, stdin/stdout frames, timings, and host logs.
5. Run A/B: original process, CCCG mock, CCCG Codex.
6. Stop immediately on authentication, update, integrity, or licensing warnings.
7. Restore the original process and verify its hash and normal startup.

Success means a complete request lifecycle without host hang and with visible
backend attribution. A process merely starting is not success.

Current pre-install evidence on 2026-08-13:

- Release build: zero warnings and zero errors;
- automated tests: 25/25 passed;
- exact dated Haiku alias to Luna: passed in an isolated real turn;
- router stdout: three lines, all valid JSON;
- non-Haiku/no-model passthrough chain:
  `CCCG -> existing bridge shim -> Claude Code 2.1.227`, passed;
- adjacent `cccg.routes.json` discovery: passed;
- installer dry-run hash checks: passed;
- installer with `-Apply` while Claude was open: correctly refused before
  creating CCCG installation state.

The live Desktop Haiku-to-Luna and non-Haiku passthrough A/B passed on
2026-08-13. The only remaining regression check is a fresh synthetic
cross-session round trip through the preserved bridge layer. See
[desktop-luna-experiment.md](desktop-luna-experiment.md).
