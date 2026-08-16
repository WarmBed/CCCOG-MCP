#!/usr/bin/env node
// CCCG presence hook — SessionStart | UserPromptSubmit | PreToolUse |
// PostToolUse | Stop
//
// Writes/updates one heartbeat file per Claude Code session at
// %LOCALAPPDATA%\CCCG\presence\<session-id>.json so CCCOG-Bar's Flow tree
// can tell "still running this turn right now" apart from "session is open
// but idle" without re-parsing the whole transcript on every tick (the
// "A/B layer" on top of the bar's own C-layer transcript-event-window scan
// — see bar/crates/cccog-bar-core/src/presence.rs).
//
// Usage (registered in settings.json by the coordinator, not by this repo's
// own automation):
//   node "D:\code\CCCG\hooks\cccg-presence-hook.js" <EventName>
// <EventName> is the Claude Code hook event name, passed as argv[2] (same
// convention as cc-hub's miki-emit.ps1's -EventType param, ported to argv
// here since this hook is invoked directly rather than via a wrapper).
// Session id and cwd come from the hook's own stdin JSON payload
// (`session_id`, `cwd` — the same fields miki-emit.ps1 reads from the
// identical Claude Code hook stdin contract).
//
// Same conventions as hooks/cccg-state-hook.js: must be fast (<1s) and
// never crash — any error exits 0 silently, never blocking the turn.
// Also prunes presence files older than 48h on every invocation (cheap:
// one readdir + a handful of stat calls, same bound as cccg-state-hook.js's
// own MAX_AGE_MS sweep of dispatch/jobs).

'use strict';

const fs = require('fs');
const path = require('path');

const PRUNE_AGE_MS = 48 * 3600 * 1000;

try {
  const eventName = process.argv[2] || '';

  let stdin = '';
  try {
    stdin = fs.readFileSync(0, 'utf8');
  } catch {
    stdin = '';
  }
  let payload = {};
  if (stdin) {
    try {
      payload = JSON.parse(stdin);
    } catch {
      payload = {};
    }
  }

  const sessionId = payload.session_id || payload.sessionId;
  if (!sessionId || !eventName) process.exit(0); // nothing to key the file on

  const cwd = payload.cwd || process.env.CLAUDE_PROJECT_DIR || null;

  const dir = path.join(process.env.LOCALAPPDATA || '', 'CCCG', 'presence');
  if (!process.env.LOCALAPPDATA) process.exit(0);
  fs.mkdirSync(dir, { recursive: true });

  const record = {
    sessionId,
    cwd,
    event: eventName,
    ts: Date.now(),
  };

  const finalPath = path.join(dir, `${sessionId}.json`);
  const tmpPath = `${finalPath}.tmp-${process.pid}`;
  fs.writeFileSync(tmpPath, JSON.stringify(record));
  fs.renameSync(tmpPath, finalPath); // atomic on the same volume — never a torn read

  // Best-effort prune, same "never let this block or crash the turn" policy
  // as everything above.
  try {
    const now = Date.now();
    for (const name of fs.readdirSync(dir)) {
      if (!name.endsWith('.json')) continue;
      const filePath = path.join(dir, name);
      let stat;
      try {
        stat = fs.statSync(filePath);
      } catch {
        continue;
      }
      if (now - stat.mtimeMs > PRUNE_AGE_MS) {
        try {
          fs.unlinkSync(filePath);
        } catch {
          // leave it for next time
        }
      }
    }
  } catch {
    // pruning is best-effort only
  }

  process.exit(0);
} catch {
  process.exit(0);
}
