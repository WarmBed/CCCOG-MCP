# Backlog

Reviewed 2026-08-15 with the operator. Statuses reflect their calls.

| # | Item | Decision / status |
|---|---|---|
| 1 | Canonical tree = git clone | **DONE 2026-08-15.** `D:\code\CCCG` is now a clone of github.com/WarmBed/CCCG; `experiments\`, `artifacts\`, `.claude\`, `.mcp.json` stay as local untracked state. Host updates are now `git pull` + one Desktop restart. The `CCCG-patha` working copy is retired for new work. |
| 2 | Grok owner transport (ACP) | Deferred — extra work, revisit when live Grok delivery matters. CLI resume path already meets delivery semantics for closed sessions. |
| 3 | Orphan process reaper | **DONE 2026-08-15.** `scripts\reap-cccg-hosts.ps1` (+`-DryRun`). Kills Hosts / one-shot workers whose parent is dead and older than 5 min; never touches `run-job` / `run-owner`. Run manually or schedule. |
| 4 | Inbox governance | **DONE 2026-09-06.** Age-based pruning in `DispatchRunner.Maintain`: acked notes after `CCCG_INBOX_READ_RETENTION_DAYS` (14), everything (audit lines included) after `CCCG_INBOX_MAX_RETENTION_DAYS` (90). See docs/dispatch.md "Housekeeping". |
| 5 | Human-entry wrapper (`cccg grok` launcher) | Feasible but non-trivial: a ConPTY passthrough proxy (human keyboard -> pty -> provider TUI) that also injects queued CCCG messages at turn boundaries. Note: taking over an ALREADY-OPEN window remains impossible; only wrapper-launched sessions can be owned. Deferred pending demand. |
| 6 | Grok reverse wiring | One command (`grok mcp add cccg-dispatch -- <host path>`), zero API quota needed to configure, but untestable until Grok balance is topped up. Do together with the next Grok session. |
| 7 | Misc | Deep-search opt-in caps; audit entries for quota overrides; `cccg_set_title` re-enable per provider once a CLI read-back contract exists. |
| 8 | Silent grok cancellation | **DONE 2026-08-30 / 09-06.** `stopReason != end_turn` is a failure, auto-retried up to `CCCG_GROK_CANCEL_RETRIES`; job records carry `providerArgv`, `providerStopReason`, `retryCount`, `providerCostUsd`, `workerVersion`. Root cause still open — long-session correlation noted in docs/dispatch.md. |
| 9 | Periodic reconciliation / retention | **DONE 2026-09-06.** `Maintain` sweep after every run-job Worker and on a Host timer (`CCCG_MAINTENANCE_INTERVAL_MINUTES`); terminal job dirs pruned after `CCCG_JOB_RETENTION_DAYS` (30). Replaces "reconcile only at Host startup or when someone polls". |
| 10 | Host versioned install | **DONE 2026-09-06 (install path); cutover pending.** `install-dispatch-host.ps1` + `host-current` junction; Host csproj no longer builds into the live artifacts dir. Each `.mcp.json` still has to be re-pointed at the junction and its session restarted once. |
| 11 | Completion surfacing | **DONE 2026-09-06 (pull) / 2026-09-12 (push).** State hook lists jobs completed since the session's previous turn; and the engine's SessionStart `watchPaths` + FileChanged `asyncRewake` now let `WakeNotifier` wake the dispatching session when a job goes terminal (see docs/dispatch.md "Completion surfacing"). Requires the two hooks in `~/.claude/settings.json`. |
| 12 | Cancel a queued job | **DONE 2026-09-11.** `cccg_job_cancel` withdraws a still-queued job; never kills a running provider. |
| 13 | Orphaned queued jobs | **DONE 2026-09-12.** A queued job whose worker never attached is failed after `CCCG_ORPHAN_QUEUED_GRACE_MINUTES` (10) instead of sitting STUCK forever. |
