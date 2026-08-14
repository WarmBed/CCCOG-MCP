# Monitor hot update

`cccg-host` is a long-running supervisor for `cccg-monitor`. It updates only the
read-only monitor worker; it does not stop, restart, patch, or configure Claude
Desktop or a Claude engine.

## Guarantees

For each update, the host:

1. computes the candidate SHA-256;
2. copies it to an immutable `versions/<sha256>/cccg-monitor.exe` path;
3. starts it as a separate process with a unique generation directory;
4. waits for a worker-produced `ready.json`;
5. verifies PID, generation, content-capture mode, dataset path and manifest;
6. keeps the old worker alive for a short overlap/soak window;
7. promotes the candidate only if it remains alive;
8. requests a cooperative stop from the old worker;
9. records the handoff and whether the old worker stopped cooperatively.

If launch, readiness, dataset validation, or the overlap health check fails, the
candidate is rejected and the previous worker remains active. `last-failure.json`
contains the reason.

The overlap provides at-least-once capture. A transcript entry written during
the overlap can appear in both run datasets. Content records include a stable
`logicalRecordKey`; deduplicate on that key. Older datasets without the field
can use `(cliSessionKey, entryKey, kind, blockIndex)`. Tool operations already
have a stable `operationKey`.

## Start

```powershell
cccg-host.exe run `
  --worker D:\path\to\cccg-monitor.exe `
  --capture-content
```

The default state directory is:

```text
%LOCALAPPDATA%\CCCG\host
```

Monitor datasets remain under:

```text
%LOCALAPPDATA%\CCCG\monitor-data
```

## Update without restarting Claude

```powershell
cccg-host.exe update --worker D:\path\to\new\cccg-monitor.exe
```

The command waits for activation and reports the new worker PID, generation and
dataset. It exits non-zero when the candidate is rejected.

## Status and stop

```powershell
cccg-host.exe status
cccg-host.exe stop
```

`stop` cooperatively stops the host and monitor worker only. It does not signal
any Claude process.

## State files

```text
%LOCALAPPDATA%\CCCG\host\
  desired.json          requested immutable worker hash/path
  active.json           validated active worker and retiring worker, if any
  heartbeat.json        host PID/path and current worker PID
  last-failure.json     most recent rejected candidate
  handoffs.jsonl        append-only successful handoff audit
  host-events.jsonl     supervisor diagnostics
  versions\<sha256>\    immutable staged workers
  generations\<id>\    ready and cooperative-stop signals
```

PID reuse is not trusted. Before attaching to, waiting for, or force-stopping a
worker, the host verifies that the process executable path matches the immutable
path recorded in state. A force stop is limited to a validated monitor worker
after the cooperative timeout.

## Boundaries

- The monitor worker is hot-swappable; the small supervisor itself is not.
- Upgrading `cccg-host.exe` remains an explicit, rare supervisor restart.
- Raw run datasets are not merged during handoff. Analyze the union using the
  stable logical keys above.
- The host does not update itself from the network. `update` accepts an explicit
  local executable supplied by the tester.
- This mechanism does not install into Claude Desktop and is independent of the
  paused Codex/Grok router work.
