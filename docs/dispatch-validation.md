# CCCG Dispatch 0.4.5 validation

Date: 2026-08-14 (Asia/Taipei)

## Result

| Requirement | Result | Evidence |
|---|---|---|
| List and inspect Claude sessions | PASS | Listed 401 transcripts in `D:\code\openruterati`, including 7 live Desktop writers with real titles/model/PID; content was not returned |
| Create and resume Claude | PASS | Created `e7ce93a3-c2d8-40a4-bea4-d261a6ef5dce`; second turn used the same id and recalled `NEBULA-519` |
| Claude text-only isolation | PASS | Init reported `tools=[]`, `mcp_servers=[]`, `skills=[]`, `plugins=[]`, and `slash_commands=[]`; normalized responses contained only final text |
| Claude multi-host FIFO | PASS | Jobs `20260813T182006Z_8c5eb6ca` and `20260813T182011Z_336baa3f` used the same session and did not overlap |
| Active Claude Desktop routing | IMPLEMENTED; native channel live-proven | CCCG rejects CLI resume with exact `mcp__ccd_session_mgmt__list_sessions` + `mcp__ccd_session_mgmt__send_message` instructions; current Desktop shows successful doc1/doc2/doc4 native cross-session messages |
| Resume a specific existing session | PASS (Codex) | Created and resumed `019ffad3-3141-78d1-a0d0-927f4200ae91`; second response was `CCCG-CODEX-RESUME-002` |
| Bind a newly created session | PASS (Claude/Codex/Grok) | Claude/Grok preassign UUIDs; Codex parses `thread.started`; all three bind only after success |
| Submit while a managed session is busy | PASS | Two independent MCP Hosts queued jobs to the same Codex session; provider execution intervals did not overlap |
| Multiple Claude sessions | PASS (Codex) | Host PIDs `8304` and `26016` returned distinct job IDs; a third Host PID `50620` collected both results |
| Background survival | PASS | Both creating MCP Hosts were closed before provider completion; detached Workers completed and persisted results |
| Automatic result return | PASS (Claude/Codex/Grok) | `cccg_dispatch_wait` returned normalized create and resume responses in the original MCP tool call |
| Cross-process inbox/bindings/jobs | PASS | Concurrent tests preserve 80/80 inbox posts; same-peer maximum provider concurrency was 1 |
| MCP Worker hot update | PASS | The unchanged fixed Host returned Worker `0.4.2` on its next call; later calls used `0.4.4`, without replacing the locked Host exe |
| Dead Worker recovery | PASS | Unfinished job with a dead Worker PID is atomically marked failed |
| Grok create and resume | PASS | Created `a476f2ae-e34a-4e5f-a0d6-6abcc8c9c6f5`; resume used the same id and recalled `QUASAR-284` |
| Grok normalized response | PASS | Worker `0.4.4+` returns only top-level `text`; provider thought and usage remain local diagnostics and do not reach Claude/inbox |
| Grok multi-host FIFO/background survival | PASS | Distinct dispatch Host PIDs `59432` and `11288`; A/B completed in order with no overlap; separate Hosts `26688` and `56036` collected results |

Automated suite: 49 passed, 0 failed. Installed current Worker: `0.4.5`.

## Claude timing evidence

The real create and resume responses were:

| Phase | Session | Response |
|---|---|---|
| create | `e7ce93a3-c2d8-40a4-bea4-d261a6ef5dce` | `CLAUDE-CCCG-CREATE-042` |
| resume | same | `NEBULA-519 CLAUDE-CCCG-RESUME-042` |

The multi-host/FIFO pair was:

| Job | Started UTC | Finished UTC | Response |
|---|---|---|---|
| `20260813T182006Z_8c5eb6ca` | 18:20:11.246 | 18:20:15.650 | `CLAUDE-MULTI-HOST-A-042` |
| `20260813T182011Z_336baa3f` | 18:20:19.854 | 18:20:23.900 | `CLAUDE-MULTI-HOST-B-042` |

The second process began after the first process finished, preserving one
writer per Claude transcript across MCP Host processes.

## Codex multi-session timing evidence

Both jobs used Codex session `019ffad3-3141-78d1-a0d0-927f4200ae91`:

| Job | Started UTC | Finished UTC | Response |
|---|---|---|---|
| `20260813T111431Z_c86b880c` | 11:14:31.734 | 11:14:38.361 | `CCCG-MULTI-CLAUDE-A` |
| `20260813T111431Z_dad8f918` | 11:14:38.580 | 11:14:44.312 | `CCCG-MULTI-CLAUDE-B` |

The second provider process started only after the first finished. This is the
required single-writer behavior.

## Grok recovery and timing evidence

Quota recovery was confirmed with a real `grok-4.6-build` response. Create and
resume returned:

| Phase | Session | Response |
|---|---|---|
| create | `a476f2ae-e34a-4e5f-a0d6-6abcc8c9c6f5` | `GROK-CCCG-CREATE-043` |
| resume | same | `QUASAR-284 GROK-CCCG-RESUME-044` |

The background FIFO pair was:

| Job | Host PID | Started UTC | Finished UTC | Response |
|---|---:|---|---|---|
| `20260813T182411Z_706191d5` | 59432 | 18:24:11.737 | 18:24:26.862 | `GROK-MULTI-HOST-A-044` |
| `20260813T182412Z_4adae98a` | 11288 | 18:24:26.942 | 18:24:42.005 | `GROK-MULTI-HOST-B-044` |

The dispatch Hosts had already closed when the detached workers completed;
separate Host PIDs 26688 and 56036 collected the persisted results.

## Historical Grok blocker

The real Grok create request produced a CCCG job and preassigned session UUID,
then Grok returned exit code 1 with:

```text
API error (status 402 Payment Required): Grok Build usage balance exhausted
```

This proved process launch, prompt handoff, output capture, and error
propagation. CCCG correctly did not save a successful binding for the failed
call. The restored-quota acceptance matrix above now supersedes this blocker.
