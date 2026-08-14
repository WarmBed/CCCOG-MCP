# CCCG Monitor

`cccg-monitor` is a read-only live observer for Claude Desktop session lifecycle
data. It does not attach to process memory, replace an engine, intercept stdin or
stdout, decrypt TLS, or read credentials.

## Sources

| Source | Collected | Explicitly excluded |
|---|---|---|
| Desktop `main.log` | lifecycle category, timing, health and reason | raw line and message content |
| Desktop session JSON | model, effort, completed-turn count, activity time, pseudonymous bridge links | title, cwd and arbitrary fields |
| CLI transcript files (default) | write time, total bytes and delta bytes | JSONL contents |
| CLI transcript files (`--capture-content`) | newly appended user text and visible assistant text | history before startup, thinking, tool inputs/results and attachments |
| CLI operation records | tool name/class, pseudonymous operation key, input field names, payload length, result/error state, hook counts/status | tool input/result bodies and hook output bodies |
| Windows processes | PID, parent PID, role, redacted path and start/exit time | command line, memory and environment |

Session and bridge identifiers are transformed with HMAC-SHA256 and a local
salt stored at `%LOCALAPPDATA%\CCCG\monitor-data\.monitor-salt`. This permits
correlation across runs without placing raw session IDs in datasets. Do not
publish the salt.

## Commands

Verify discovery without starting a dataset:

```powershell
cccg-monitor.exe doctor
```

Import the existing Desktop log into a normalized historical baseline:

```powershell
cccg-monitor.exe replay
```

Open the live dashboard:

```powershell
cccg-monitor.exe watch --history 2000
```

Enable local prompt/response capture for future transcript writes:

```powershell
cccg-monitor.exe watch --history 2000 --capture-content
```

This mode is intentionally explicit. It starts at the current end of every
existing transcript, so it does not import earlier conversations. Safe metadata
events named `session.prompt_captured` and `session.response_captured` appear on
the dashboard with character counts; the text itself never goes to stdout.

For a headless collector:

```powershell
cccg-monitor.exe watch --history 2000 --no-dashboard --quiet
```

Every event is flushed immediately under:

```text
%LOCALAPPDATA%\CCCG\monitor-data\runs\<run-id>\
  manifest.json
  events.jsonl
  content.jsonl  # only when --capture-content is enabled
  summary.json
```

`content.jsonl` stores one record per visible text block with pseudonymous CLI,
Desktop-session, entry, parent, prompt, request, and provider-message keys when
available. These keys allow prompt/response reconstruction without writing raw
session identifiers. Prompt and response bodies can still contain sensitive
material supplied by the user, so keep the dataset local and do not commit or
publish it.

## Tool, WebSearch, and hook monitoring

Operation monitoring is enabled in every live run and does not execute or proxy
tools. Newly appended transcript records produce:

- `session.tool_requested`: tool name, class, pseudonymous operation key, input
  field names and serialized payload size;
- `session.tool_completed`: the same operation key when available, result block
  count, serialized result size and `isError`;
- `session.hook_summary`: hook count, info/error counts, whether continuation was
  prevented, whether the hook produced output, and the stop reason.

`WebSearch` and `WebFetch` are classified as `web_search` and `web_fetch`.
Browser MCP, generic MCP, cross-session, shell and filesystem tools have separate
classes. This lets a before/after engine experiment distinguish successful text
generation from preservation of the host tool chain. The monitor does not claim
that a tool worked merely because it was requested: acceptance requires a
matching completion with `isError=false`; hook acceptance requires a summary
with the expected hook count and no hook errors.

Press `Ctrl+C` for a cooperative stop and a final summary.

## Test markers

Immediately before a controlled test, write a short marker from another terminal:

```powershell
cccg-monitor.exe mark --label CROSS-IDLE-001
```

The running monitor records `test.marker` at that time. Labels are restricted to
80 ASCII letters, digits, `-`, `_`, `.`, or `:`; they cannot carry prompt text.

## Initial data campaign

Use synthetic messages only. Run at least ten repetitions of each case:

| Label prefix | Sender | Recipient before send | Expected evidence |
|---|---|---|---|
| `DIRECT-ACTIVE` | user | focused/active | normal health baseline |
| `CROSS-ACTIVE` | session A | recently active | send → transcript → healthy cycle |
| `CROSS-IDLE` | session A | idle | send → resume/start → first assistant → healthy |
| `CROSS-ROUNDTRIP` | A then B | mixed | two correlated healthy cycles |
| `CROSS-BURST` | session A, two sends | idle then running | no dropped or merged lifecycle |

Suggested sequence:

1. Start `watch` and leave its terminal open.
2. Run `mark --label CROSS-IDLE-001`.
3. Send one short synthetic cross-session message.
4. Require the recipient to reply to the sender with a short synthetic marker.
5. Wait for both health outcomes before starting the next repetition.
6. Increment the suffix and repeat.
7. Stop the monitor with `Ctrl+C` after the batch.

The default monitor dataset does not prove semantic delivery by itself because
it does not save payloads. When content capture is deliberately enabled,
matching the synthetic marker text in receiver and sender transcript records
adds semantic delivery evidence. Lifecycle timing and `hadFirstResponse=true`
remain necessary to distinguish content written to disk from a healthy completed
cycle.

## Interpretation

```text
message_sent but no transcript activity
  -> Desktop-to-engine boundary is suspect

starting/resuming but no start_timing
  -> engine initialization or pre-model pipeline is suspect

start_timing present and transcript grows
  -> model turn reached first assistant output

cycle_unhealthy reason=no_response
  -> lifecycle never produced a valid first response

cycle_healthy hadFirstResponse=true
  -> Desktop observed a completed responsive cycle
```

All conclusions remain black-box observations. Timing correlation should be
reported as correlation unless a product-produced identifier directly joins two
events.
