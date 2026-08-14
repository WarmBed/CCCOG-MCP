# Claude Desktop Haiku-to-Luna experiment

## Status

Installed and live-validated on 2026-08-13 against Claude Desktop 1.28929.0 and
Claude Code 2.1.227. Installation remains intentionally blocked while any
Claude Desktop or Claude engine process is running.

The experiment does not call Luna "Haiku". Claude Desktop supplies the Haiku
alias, but every routed assistant answer begins with the actual identity:

```text
[CCCG provider=codex-app-server model=gpt-5.6-luna]
```

## Layering

The current machine already uses a local bridge shim for the Claude Code 2.1.227
cross-session regression. CCCG preserves that layer:

```text
Claude Desktop
  -> claude.exe                      CCCG outer router
       -> Haiku 4.5 alias            Codex app-server -> gpt-5.6-luna
       -> every other invocation     preserved bridge shim
                                        -> Anthropic Claude Code 2.1.227
```

The preserved bridge shim continues to perform its existing cross-session
compatibility behavior. CCCG does not patch the bridge shim or the Anthropic
binary.

## Prepared files

- router: `artifacts/cccg-router/desktop-luna/cccg-router.exe`
- installer: `scripts/Install-DesktopLunaExperiment.ps1`
- restore: `scripts/Restore-DesktopLunaExperiment.ps1`
- status: `scripts/Get-DesktopLunaStatus.ps1`

The installer reads the existing bridge-shim manifest and refuses to proceed if
either the active bridge-shim SHA-256 or the Anthropic sidecar SHA-256 has
changed. It writes recovery material beneath:

```text
%LOCALAPPDATA%\CCCG\desktop-luna\recovery\<timestamp>\
```

## Install gate

From `D:\code\CCCG`, first run the non-mutating check:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Install-DesktopLunaExperiment.ps1
```

It must report `Ready: True` and `Mode: dry-run`. Then completely exit Claude
Desktop, including the tray/background process. Confirm no Claude process is
left, and apply:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Install-DesktopLunaExperiment.ps1 -Apply
```

Do not force-kill active sessions merely to install the experiment. Finish or
pause their work and exit Claude normally.

## First Desktop acceptance

1. Reopen Claude Desktop.
2. Create a fresh local session and select Haiku 4.5.
3. Send: `Reply only CCCG-DESKTOP-LUNA-001`.
4. Require a successful response containing both the provider disclosure and
   `CCCG-DESKTOP-LUNA-001`.
5. Inspect `%LOCALAPPDATA%\CCCG\router-data\` and require:
   `requestedModel=claude-haiku-4-5-20251001`,
   `actualBackendModel=gpt-5.6-luna`, and `WasRerouted=false`.
6. Open a fresh Sonnet or Opus session and send
   `Reply only CCCG-PASSTHROUGH-001`. It must remain an ordinary Claude answer
   without a CCCG provider disclosure.
7. Send one synthetic cross-session message between two non-Haiku sessions and
   confirm the existing bridge behavior still works.

### Recorded live result

- Desktop requested `claude-haiku-4-5-20251001`.
- The outer engine process was CCCG PID 3328; its backend child was
  `codex.cmd app-server`, not the Anthropic sidecar.
- First user prompt `hi` had a recorded length of 2 characters.
- Requested and actual backend were both `gpt-5.6-luna`.
- `WasRerouted=false`.
- Backend duration was 6857 ms.
- The visible response began with
  `[CCCG provider=codex-app-server model=gpt-5.6-luna]`.
- Desktop recorded `session.cycle_healthy`, duration 7 seconds, with
  `hadFirstResponse=true` and the session advanced to one completed turn.
- A second turn on the same Haiku engine also reached `turn/completed`. It took
  about 60.3 seconds because the Codex backend attempted tool work; tool errors
  confirmed that Claude tool compatibility is outside this milestone.
- Concurrent Fable, Opus, and Sonnet engines each followed the complete
  `CCCG -> preserved bridge shim -> Anthropic sidecar` chain.
- Fable completed a post-restart healthy cycle with `hadFirstResponse=true`;
  Opus and Sonnet produced post-restart response events.

The Haiku-to-Luna route and non-Haiku passthrough A/B therefore pass. A fresh
synthetic cross-session round trip through the newly layered wrapper remains a
separate regression check before claiming that the old bridge behavior has been
revalidated after this installation.

Stop and restore if Desktop reports an engine integrity/update warning, the
Haiku session never receives `system/init`, stdout contains a non-JSON line, or
the non-Haiku/cross-session controls regress.

## Current functional boundary

The first Haiku-to-Luna slice supports plain text turns and returns compatible
assistant/result frames. The Codex backend is ephemeral, read-only, and uses
`approvalPolicy=never`. Claude-specific tools, hooks, WebSearch, plugins,
resume, and full multi-turn context are not yet translated on the Luna route.
The non-Haiku passthrough path retains the existing Claude behavior.

## Restore

Completely exit Claude Desktop, then dry-run and apply the restore:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Restore-DesktopLunaExperiment.ps1

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Restore-DesktopLunaExperiment.ps1 -Apply
```

Restore verifies the currently installed CCCG hash before changing anything,
moves CCCG-owned files into the recovery directory, restores the previous
bridge shim, and verifies its SHA-256. The Anthropic sidecar is never replaced
by this experiment.

## Updates

Claude Desktop or Claude Code updates may create a new version directory and
bypass this wrapper. Treat that as "experiment no longer installed" rather
than automatically modifying the new version. Re-run discovery, hash checks,
standalone validation, and the full A/B before installing against a new version.
