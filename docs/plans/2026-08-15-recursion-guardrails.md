# Recursion guardrails: hop-count, per-caller quota, and audit notification

> Status: **plan + TDD specification only**.  This file is the review gate for
> the next implementation dispatch.  No source, test, MCP schema, or existing
> documentation is changed by this plan.

## 0. Baseline and implementation gate

- Current regression baseline is **85/85**.
- The required test command is:

  ```powershell
  dotnet run --project tests/CCCG.Tests/CCCG.Tests.csproj -c Release
  ```

- Build `CCCG.Core`, `CCCG.Dispatch`, `CCCG.Dispatch.Worker`, and the test
  project individually as needed.  Do not build `CCCG.sln`; it still refers to
  the absent `experiments` project.
- The next implementation dispatch must add the tests in §6 first, observe
  the expected red state, then make the smallest implementation changes.  It
  must finish with the original 85 tests green plus the new tests green.

## 1. Goal, requirements, and non-goals

The worker must be safe when a provider calls CCCG again, without requiring an
MCP reconnect or a Host schema change.  The guardrails are worker/process
contracts, carried through inherited environment variables and local ledgers.

### 1.1 Mandatory requirements

1. **Hop-count guard.**  A provider child receives `CCCG_HOP=<parent hop + 1>`.
   A worker reads its own `CCCG_HOP` (`0` when absent), records
   `hopCount = environment hop + 1` on every newly accepted job, and rejects a
   job when `hopCount > CCCG_MAX_HOP` (default `2`).  The rejection is
   fail-closed and identifies the source, hop, limit, and chain.
2. **Per-caller daily quota (opt-in enforcement).**  Always count accepted
   dispatches by `hopSource + provider + local calendar date` and store
   counters below `%LOCALAPPDATA%\CCCG\dispatch\quotas\`.  With neither quota
   environment variable set, count is unlimited and no dispatch is rejected
   for volume.  Setting `CCCG_QUOTA_CLAUDE` or `CCCG_QUOTA_DEFAULT` to a
   positive integer enables enforcement for that provider class; a limit
   rejection is fail-closed and says that the quota resets tomorrow.
3. **Audit notification.**  For `hopCount >= 1`, after the job is successfully
   created, post one inbox audit message with `fromRole=system`.  Its content
   must state who, which cwd, which model, and which provider/session was
   called.  The prompt text and secrets must not be copied into the audit.
4. **Three-path propagation.**  The tests and implementation must cover
   `FileProcessLauncher` resume/create, the `OwnerDaemon` provider child, and a
   `cccg-dispatch` Host started by the Codex GUI's `config.toml`.

### 1.2 Non-goals

- No new MCP tool argument and no MCP protocol/schema change.  Existing Host
  binaries can remain installed while the worker is updated.
- No change to provider prompts, session selection, peer discovery, or the
  existing 85-test behavior except for the additive job/audit metadata.
- No attempt to make an LLM decide whether a call is recursive.  The decision
  is deterministic and made before provider execution.
- No quota sharing with a remote service.  The quota is local to the Windows
  user profile and is intentionally not a billing meter.

## 2. Mechanism verification and contract

### 2.1 What is true in the current checkout

The three process boundaries were inspected before writing this plan:

| Boundary | Current behavior | Required change |
| --- | --- | --- |
| `DispatchRunner` → `FileProcessLauncher` | `ProcessStartInfo.UseShellExecute=false`, redirected stdio, but no environment override | Carry the job's child-hop/source/chain values in the launch contract and assign them to `ProcessStartInfo.Environment` for both resume and create commands. |
| `OwnerDaemon` → Codex app-server | `CodexOwnerTurnTransport` ultimately calls `ProcessJsonLineTransport.StartCodexAsync`; the child currently receives no explicit CCCG variables | Start the provider child with the computed hop environment.  Because this child is long-lived, see the restart/rebind rule in §2.4. |
| Codex GUI → `cccg-dispatch` Host → worker | Local `C:\Users\<user>\.codex\config.toml` has a `[mcp_servers.cccg-dispatch]` command and no `env` block.  `DispatchBackendClient` uses `UseShellExecute=false`, so the worker inherits the Host environment by default. | Preserve the inherited value explicitly and test it.  An unset value is the correct root-session state for a human/GUI-launched Host; a provider-launched Host must inherit the provider's injected value. |

There is no repository `config.toml`; the above is a read-only inspection of
the active local Codex configuration.  The implementation test must use a
small environment-probe child or an injected process launcher rather than
depending on a particular user's absolute executable path.

### 2.2 Hop terminology and exact arithmetic

The following convention removes the otherwise ambiguous `n` in
`CCCG_HOP=<n+1>`:

- `processHop` is the integer in the current process environment, or `0` when
  `CCCG_HOP` is missing.  Negative, non-integer, or whitespace-only values are
  invalid and fail closed with a configuration error.
- A worker accepting a new request computes `jobHop = processHop + 1`, stores
  it as `DispatchJob.HopCount`, and checks it against `maxHop` (default 2).
- A **provider child** launched for that job receives
  `CCCG_HOP=jobHop`.  The child is already the provider edge represented by
  that job; a later worker request from that child therefore becomes
  `jobHop + 1`.
- The MCP Host and its worker are infrastructure for one request, not another
  provider edge.  Host → worker inherits `CCCG_HOP` unchanged; the worker adds
  one only when it creates the job.
- The direct human/GUI sequence is therefore `processHop=0 → jobHop=1`
  (allowed), a provider child with `CCCG_HOP=1` creates `jobHop=2` (allowed),
  and a child with `CCCG_HOP=2` would create `jobHop=3` (rejected by default).

The following inherited variables are part of the worker-only contract:

| Variable | Meaning and default |
| --- | --- |
| `CCCG_HOP` | Completed provider edges before this process; default `0`. |
| `CCCG_MAX_HOP` | Positive integer maximum job hop; default `2`.  Malformed or non-positive overrides fail closed. |
| `CCCG_HOP_SOURCE` | Stable, non-secret caller identity propagated unchanged through children.  If absent at a human root, derive `user:<user>@<machine>` once and propagate that value. |
| `CCCG_HOP_CHAIN` | Redacted diagnostic chain, default `human`; append the target provider at each provider spawn and cap its length (for example 512 characters). |

`DispatchJob` adds `hopCount`, `hopSource`, and `hopChain` additively.  Old
JSON without these properties remains readable as a legacy job; the guard is
applied at new enqueue time and legacy execution does not invent a false
chain.

A rejection message has a stable prefix and actionable details, for example:

```text
CCCG recursion guard rejected dispatch: hopCount=3 exceeds maxHop=2; source=user:alice@pc; chain=human>claude>codex. No provider process was started.
```

### 2.3 Quota contract

- `hopSource` is the key's caller component; `provider` is normalized to the
  existing lower-case provider name; the date is the machine's local
  `yyyy-MM-dd` calendar date (a `TimeProvider`/clock injection makes the
  boundary test deterministic).
- The on-disk layout is:

  ```text
  %LOCALAPPDATA%\CCCG\dispatch\quotas\<yyyy-MM-dd>\<sha256(source|provider|date)>.json
  ```

  The file contains `schemaVersion`, source, provider, date, count, limit, and
  `updatedAtUtc`; the source is not exposed in the filename.  `count` is
  always incremented, including when enforcement is disabled; the recorded
  `limit` field keeps the existing provider baseline for ledger visibility.
- Enforcement is disabled when the corresponding environment variable is
  absent.  A positive `CCCG_QUOTA_CLAUDE` enables Claude enforcement and a
  positive `CCCG_QUOTA_DEFAULT` enables enforcement for other providers.
- `QuotaLedger.TryConsume` takes the existing cross-process file gate, reads,
  increments, and atomically replaces the file.  The reservation occurs after
  provider/peer selection has succeeded and before job creation.  If job
  persistence fails, the reservation is released under the same lock; a
  successfully created job consumes one request even if the provider later
  fails.
- Invalid override values (`CCCG_QUOTA_CLAUDE` or
  `CCCG_QUOTA_DEFAULT`) fail closed with the variable name.  A limit error
  includes source, provider, count/limit, and `resets tomorrow`.
- A new date uses a new directory/key and therefore resets without mutating
  the previous day.  Cleanup of old files is optional and must not run in the
  enqueue critical path.

### 2.4 OwnerDaemon long-lived child rule

`OwnerDaemon` currently keeps one provider transport alive across turns.  An
environment variable cannot be changed inside an already-running Codex
process.  Simply setting `CCCG_HOP` when `run-owner` starts would therefore
protect the first edge but would reuse the same hop for every later recursive
turn.

The implementation must make this limitation explicit and solve it rather
than silently claiming propagation:

1. Add an owner transport factory/restart seam.  When a claimed
   `OwnerMessage` has a hop different from the active provider child, dispose
   that child and recreate the transport with `CCCG_HOP=message.HopCount`,
   `CCCG_HOP_SOURCE`, and the extended chain.  Rebind/resume the existing Codex
   thread where the current client supports it; if rebind fails, fail the
   message closed and write an owner failure receipt rather than running with a
   stale hop.
2. `DeliverToOwner` maps the job's hop metadata onto `OwnerMessage`.  The
   owner test transport records the values it was asked to run with, so no
   real Codex process is needed for the TDD test.
3. A root owner started by a human has process hop 0; its first provider child
   is explicitly started at hop 1.  A later message at hop 2 must exercise the
   restart/rebind seam and must not reuse hop 1.

This is the only intentionally broader change in the plan; it is required for
the stated OwnerDaemon path to have real hop semantics rather than a
process-start-only approximation.

## 3. Data flow and file/change inventory

### 3.1 Enqueue and provider flow

```text
MCP tool (unchanged payload)
  -> cccg-dispatch Host (inherits current env)
  -> DispatchBackendClient worker (inherits current env)
  -> WorkerRuntime reads processHop/source/chain
  -> DispatchRunner normalizes overrides
  -> select peer/owner
  -> hop guard, then atomic quota reservation
  -> DispatchJob JSON + prompt persisted
  -> hop>=1 system audit posted to InboxLedger
  -> Run/Dispatch
  -> FileProcessLauncher or OwnerDaemon provider child
       with CCCG_HOP=jobHop and propagated source/chain
```

The same guard path is used for `dispatch` and `dispatchWait`; no caller can
skip it by selecting the synchronous operation.

### 3.2 Planned source files

| Area | Planned files and responsibility |
| --- | --- |
| Hop/config contract | New `CCCG.Core/Dispatch/RecursionGuard.cs` (or equivalent small internal helper): parse/validate env, compute hop, max, source, chain, and stable errors. |
| Job persistence | `DispatchJob` model/store: additive `hopCount`, `hopSource`, `hopChain` JSON properties and legacy-read behavior. |
| Enqueue/run | `DispatchRunner.cs`: compute the job hop, enforce guard before quota, reserve/release quota, pass environment overrides to both resume/create launches, and post the audit after successful creation. |
| Quota | New `QuotaLedger.cs` under `CCCG.Core/Dispatch`, using `CrossProcessFileGate` and atomic writes. |
| Process launch | `LaunchCommand`/`FileProcessLauncher` contract in `DispatchRunner.cs`: apply explicit environment overrides while keeping existing UTF-8/stdio behavior. |
| Owner path | `OwnerSpool.cs`, `OwnerDaemon.cs`, `IProviderTurnTransport.cs`, `ProcessJsonLineTransport.cs`, and Worker `Program.cs`: carry hop metadata, inject it when starting the Codex child, and use the restart/rebind seam from §2.4.  `PersistentCodexAppServerClient.CompleteAsync` remains unchanged. |
| Host inheritance | `DispatchBackendClient.cs`: preserve/verify inherited `CCCG_HOP`, source, and chain when starting the worker; do not increment at this infrastructure boundary. |
| Audit | Reuse `InboxLedger.Post`; add only the minimum metadata needed (`fromRole=system`, target provider/session, job id) and a deterministic content formatter. |
| Tests | Add focused tests to the existing `tests/CCCG.Tests` project, with injectable clock, launcher, transport factory, environment capture, and temporary quota/inbox roots. |
| Optional child mode | Only if approved in §7: the Claude command builder and its focused command-argument tests. |

No `DispatchTools.cs` change is planned for the mandatory three guardrails.

## 4. Audit message contract

For a successful recursive job creation, post exactly one inbox entry:

- `fromRole = "system"`;
- `toRole = "claude"` (the human-visible inbox owner; the target provider is
  also recorded in `toProvider`);
- `fromProvider = "cccg"`, `toProvider = job.Provider`, `toSessionId` when
  known, and `jobId = job.JobId`;
- content is a single-line deterministic message such as:

  ```text
  CCCG audit: source=user:alice@pc chain=human>claude cwd=D:\work model=gpt-5.6-luna called provider=codex session=<new> job=<id> hop=1.
  ```

  Empty model is rendered as `provider-default`, and an absent session as
  `<new>`; no prompt, token, or full environment dump is included.

Audit posting is part of the enqueue gate.  If the job file was created but
the audit cannot be written, mark that job failed with an audit-specific
error, do not queue a provider process, and surface the error to the caller.
The implementation must make this partial-file case recoverable and test it;
silently running an un-audited recursive job is not acceptable.

## 5. Compatibility, rollout, and rollback

- All new job fields are additive and nullable for old JSON.  Existing jobs
  remain inspectable and runnable; only new enqueue operations are subject to
  the new guard/quota/audit gates.
- Existing MCP clients send the same payload.  A worker-only deployment can be
  hot-installed; the active Host can be left running until its normal worker
  resolution path selects the new worker.  No MCP reconnect is required for
  the mandatory feature.
- New environment variables are opt-in.  A missing `CCCG_HOP` means a human
  root, not an error; missing quota variables leave enforcement unlimited while
  the local ledger continues counting.
- Quota files and audit inbox entries are append/local operational state.  A
  rollback to the previous worker leaves them harmlessly on disk; the old
  worker ignores new job properties.  If rollout must be aborted, restore the
  previous worker descriptor/binary and preserve the failed-job/audit files for
  diagnosis; do not delete quota state as a way to bypass a limit.
- Risk controls: reject malformed numeric env values, cap chain length,
  sanitize filesystem key material, use atomic writes/locks, and never include
  prompts or secrets in audit/error text.

## 6. TDD test plan (red first, then green)

The following tests are to be added before implementation.  Each listed test
must be observed failing for the missing behavior, then made green in the
order below.

### Wave 0 — regression checkpoint

1. Run the unchanged 85-test baseline with the command in §0 and record the
   green result before adding the new tests.

### Wave 1 — hop contract and job JSON

2. `HopCountDefaultsMissingEnvironmentToZeroThenRecordsOne`: worker env absent
   yields `processHop=0`, `HopCount=1`, and a persisted `hopCount` property.
3. `HopCountAllowsZeroToOneAndOneToTwo`: injected hops 0 and 1 create jobs 1
   and 2 respectively, with no provider process started before the normal
   enqueue/run step.
4. `HopCountRejectsTwoToThreeFailClosed`: injected hop 2 returns an error
   containing `hopCount=3`, `maxHop=2`, the source/chain, and proves the
   launcher was never called.
5. `HopEnvironmentRejectsMalformedOrNegativeValues`: invalid `CCCG_HOP` and
   `CCCG_MAX_HOP` fail closed with the variable name.
6. `HopSourceAndChainPropagateAndRoundTrip`: explicit source/chain survive
   job JSON and are included in the next child environment; a root derives a
   stable non-secret source.

### Wave 2 — all three process paths

7. `FileProcessLauncherResumeInjectsHopEnvironment` and
   `FileProcessLauncherCreateInjectsHopEnvironment`: capture
   `ProcessStartInfo.Environment` for both actions and assert
   `CCCG_HOP=1`, source, and an appended provider chain.
8. `OwnerDaemonStartsProviderChildWithClaimedHop`: a fake transport factory
   captures the Codex app-server child environment and asserts a claimed
   hop-1 owner message starts at 1, not the daemon's stale root value.
9. `OwnerDaemonRecreatesProviderChildWhenClaimedHopChanges`: a second claimed
   hop-2 message disposes/recreates the transport with hop 2; it is not run
   through the hop-1 child.  This is the regression test for the long-lived
   process issue in §2.4.
10. `DispatchHostWorkerLaunchPreservesInheritedHop`: a fake/probe worker
    launched by `DispatchBackendClient` observes the caller's `CCCG_HOP=1`
    unchanged, while its enqueue result becomes job hop 2.
11. `GuiConfigWithoutHopStartsAtHumanRoot`: using the actual active config
    shape (command only, no cccg env block), an unset Host environment is
    observed as process hop 0 and the first job as hop 1.  This test must not
    read or assert unrelated user secrets.

### Wave 3 — quota ledger (opt-in enforcement)

12. `QuotaCountsAtomicallyBySourceProviderAndDate`: two temporary-root
    ledgers for the same source/provider/date share one counter; a different
    provider or source uses a different file/key.
13. `QuotaUsesExplicitEnvironmentLimits`: positive
    `CCCG_QUOTA_CLAUDE`/`CCCG_QUOTA_DEFAULT` values enable and enforce the
    corresponding limits.
14. `QuotaWithoutEnvironmentCountsButNeverRejects`: with both quota variables
    absent, more than 200 dispatch reservations remain allowed while the JSON
    ledger count reaches the actual number of reservations.
15. `QuotaRejectsAtLimitBeforeJobCreation`: the limit error contains current
    count, limit, provider, source, and `resets tomorrow`; job JSON and the
    provider launcher remain absent.
16. `QuotaResetsOnNextLocalDate`: a clock crossing local midnight uses a new
    key and allows the first request without modifying yesterday's count.
17. `QuotaReservationRollsBackWhenJobPersistenceFails`: a forced atomic job
    write failure releases the reservation so a retry is not charged twice.

### Wave 4 — audit and end-to-end enqueue behavior

18. `RecursiveEnqueuePostsSystemAuditAfterSuccessfulCreate`: hop 1 creates
    exactly one inbox message with `fromRole=system`, target provider/session,
    job id, cwd, model, source, chain, and hop; the prompt is absent.
19. `HumanRootEnqueueDoesNotPostRecursiveAudit`: hop 0/process-root behavior
    has no audit notification (ordinary existing inbox behavior remains).
20. `AuditWriteFailureFailsClosed`: a forced inbox write failure leaves no
    runnable recursive provider process and records a clear failed-job reason.

### Wave 5 — optional child capability switch (only after §7 approval)

21. `ClaudeChildModeDefaultsToTextOnly`: no environment variable keeps the
    exact current text-only command contract.
22. `ClaudeChildModeToolsStillHasNoMcp`: `CCCG_CLAUDE_CHILD_MODE=tools` enables
    only the approved local tool capability, never an MCP server/config, and
    retains the hop environment.  The exact CLI flags must be checked against
    the installed Claude command before this test is finalized.

### Final green gate

23. Run all tests with the §0 command: **85 existing + all approved new tests
    green**.  Build the two shipped executables and the test project
    individually with zero errors; do not use `CCCG.sln`.

## 7. Reviewer-only decision: Claude child capability mode

This section is intentionally separate from the mandatory guardrails.  Please
choose one before implementation:

- **Keep text-only (recommended):** `CCCG_CLAUDE_CHILD_MODE` is absent or
  `text-only`; preserve the current command, permission, and strict empty-MCP
  behavior exactly.
- **Allow `tools`:** when explicitly set to `tools`, enable the smallest
  verified Claude local-tool set, while still passing no MCP configuration and
  still enforcing the hop guard/quota/audit gates.  The switch must be read
  only by the worker and must not alter Host schema.

The default remains `text-only` either way.  Tools mode cannot weaken or bypass
hop-count; a Claude child with tools is still a provider child and receives the
same `CCCG_HOP=<jobHop>` environment.  If the installed Claude CLI does not
have a stable, testable distinction for these modes, leave this section
unimplemented and retain text-only.

## 8. Reviewer decisions requested before implementation

1. Approve the explicit arithmetic in §2.2 (Host/worker inherits the current
   hop; only job creation increments it).
2. Approve the OwnerDaemon restart/rebind seam in §2.4; without it, a
   persistent provider child cannot satisfy per-turn hop propagation.
3. Approve the derived default `user:<user>@<machine>` for `CCCG_HOP_SOURCE`,
   or provide another stable non-secret caller identity.
4. Approve audit failure as fail-closed after job-file creation, with a failed
   job left for diagnosis.
5. Decide the optional Claude mode in §7.
