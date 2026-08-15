# Backlog

Reviewed 2026-08-15 with the operator. Statuses reflect their calls.

| # | Item | Decision / status |
|---|---|---|
| 1 | Canonical tree = git clone | **DONE 2026-08-15.** `D:\code\CCCG` is now a clone of github.com/WarmBed/CCCG; `experiments\`, `artifacts\`, `.claude\`, `.mcp.json` stay as local untracked state. Host updates are now `git pull` + one Desktop restart. The `CCCG-patha` working copy is retired for new work. |
| 2 | Grok owner transport (ACP) | Deferred — extra work, revisit when live Grok delivery matters. CLI resume path already meets delivery semantics for closed sessions. |
| 3 | Orphan process reaper | **DONE 2026-08-15.** `scripts\reap-cccg-hosts.ps1` (+`-DryRun`). Kills Hosts / one-shot workers whose parent is dead and older than 5 min; never touches `run-job` / `run-owner`. Run manually or schedule. |
| 4 | Inbox governance | Needed. Inbox is still actively used: guardrail audit lines (`fromRole=system`), dispatch response mirrors, and peer notes. Plan: ack-then-archive policy + age-based pruning of acked entries; audit lines get a longer retention. Not yet implemented. |
| 5 | Human-entry wrapper (`cccg grok` launcher) | Feasible but non-trivial: a ConPTY passthrough proxy (human keyboard -> pty -> provider TUI) that also injects queued CCCG messages at turn boundaries. Note: taking over an ALREADY-OPEN window remains impossible; only wrapper-launched sessions can be owned. Deferred pending demand. |
| 6 | Grok reverse wiring | One command (`grok mcp add cccg-dispatch -- <host path>`), zero API quota needed to configure, but untestable until Grok balance is topped up. Do together with the next Grok session. |
| 7 | Misc | Deep-search opt-in caps; audit entries for quota overrides; `cccg_set_title` re-enable per provider once a CLI read-back contract exists. |
