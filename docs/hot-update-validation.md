# Hot-update validation — 2026-08-13

## Automated suite

Release build completed with zero warnings and zero errors. All 19 tests passed,
including worker ready/stop control, immutable hash-addressed staging, transcript
append boundaries, content separation, tool/hook parsing, and host option parsing.

## Two-version handoff

Synthetic sources were used under
`artifacts/hot-update-e2e/runtime-20260813-b`; no Claude process was controlled.

Workers:

- v1 SHA-256:
  `B00B2E758C43A2F04C18EDD9110224AEB5DAA0EB9FA958411DE77B93AD51B527`
- v2 SHA-256:
  `4895E136657A49BA17787FF8B9DF6A73F8A6878244B1B345BB0E5898A44F35DC`

Observed controlled-overlap evidence:

- old worker PID: `45128`
- candidate worker PID: `34216`
- candidate ready: `2026-08-12T16:08:53.9325369Z`
- synthetic entry first captured: `2026-08-12T16:09:00.2283466Z`
- handoff completed: `2026-08-12T16:09:24.1250911Z`
- old worker stopped cooperatively: `true`
- old worker no longer running after handoff: `true`
- raw physical captures during overlap: `2`
- logical records after stable-key deduplication: `1`

The entry was therefore observed while both workers were alive, before the old
worker was asked to retire. The overlap behaved as at-least-once capture with a
deterministic deduplication key.

## Failed-candidate rollback

A non-monitor executable was deliberately submitted as a candidate. It exited
before creating a valid readiness file:

```text
Candidate exited before ready (exit code 2).
```

The update command returned exit code `2`; active PID `34216` and its worker hash
remained unchanged and running. The failure was written to `last-failure.json`.

## Live local handoff and completion-race correction

After synthetic validation, the supervisor was placed in front of the existing
local monitor. The supervised candidate reached ready state before the previous
unsupervised monitor and its terminal were stopped. Claude was not restarted.

The first live update exposed a reporting race: `update` returned after the new
worker was promoted but before the retiring worker had fully exited. The handoff
itself completed cooperatively, but the CLI success boundary was too early. The
acceptance predicate was corrected and covered by a nineteenth automated test:
success now requires the requested hash, a running active worker, and an empty
`retiring` list.

Post-fix live evidence:

- fixed host SHA-256:
  `372CB1E9BFCD76C0A2EA805088C2A88A675C0663575A83E6C51F459953D8BABE`
- old worker PID: `46788`
- new worker PID: `46684`
- update command duration: `2.819` seconds
- old worker stopped when the command returned: `true`
- retiring list empty when the command returned: `true`
- handoff completed before the command returned: `true`
- old worker stopped cooperatively: `true`

The monitor was then hot-updated back to the latest worker build. Final observed
state at validation time:

- host PID: `49536`
- worker PID: `17428`
- worker SHA-256:
  `2E057DC492788CBEB09B703C7571DDAF55627A8F6D6FACDCB89D0306F6037518`
- content capture: enabled
- retiring workers: `0`
- active dataset:
  `%LOCALAPPDATA%\CCCG\monitor-data\runs\20260812-162454Z_c2db831e`

## Result

The supervisor passed the tested acceptance criteria:

- candidate becomes ready before promotion;
- old and new workers overlap without a capture gap;
- duplicate overlap records are deterministically identifiable;
- old worker stops cooperatively after promotion;
- failed candidate does not stop the existing worker;
- Claude Desktop is outside the process-control scope.
