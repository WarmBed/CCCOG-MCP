#!/usr/bin/env node
// CCCG session hook — SessionStart
// Registers this Claude session with CCCG's WakeNotifier and asks the engine
// to watch this session's wake file, so a finished CCCG job can wake the
// session instead of waiting for the next human prompt.
//
// Output (stdout JSON): hookSpecificOutput.watchPaths = [absolute wake file]
// — the engine keeps a file watcher on it for the life of the session and
// runs the FileChanged hooks (hooks/cccg-wake-hook.js) when it changes.
//
// Must be fast (<1s) and never crash: any error exits 0 silently.

'use strict';

const fs = require('fs');
const path = require('path');

try {
  const raw = process.stdin.isTTY ? '' : fs.readFileSync(0, 'utf8');
  const payload = raw.trim() ? JSON.parse(raw) : {};
  const sessionId = String(payload.session_id || '').replace(/[^A-Za-z0-9._-]/g, '_').slice(0, 80);
  if (!sessionId || !process.env.LOCALAPPDATA) process.exit(0);

  const dispatchRoot = path.join(process.env.LOCALAPPDATA, 'CCCG', 'dispatch');
  const watchRoot = path.join(dispatchRoot, 'watch');
  const wakeDir = path.join(dispatchRoot, 'wake', sessionId);
  const wakeFile = path.join(wakeDir, 'cccg_wake');

  fs.mkdirSync(watchRoot, { recursive: true });
  fs.mkdirSync(wakeDir, { recursive: true });
  // Same shape WakeNotifier.Register writes (session-<id>.json).
  fs.writeFileSync(
    path.join(watchRoot, `session-${sessionId}.json`),
    JSON.stringify({ sessionId, cwd: payload.cwd || process.cwd(), registeredAt: new Date().toISOString() }, null, 2),
    'utf8'
  );
  // The watcher needs the file to exist to notice later changes reliably.
  if (!fs.existsSync(wakeFile)) fs.writeFileSync(wakeFile, '', 'utf8');
  // Fresh session: nothing announced yet, and no wakes counted.
  try { fs.unlinkSync(path.join(wakeDir, 'wakes')); } catch { /* none */ }

  process.stdout.write(JSON.stringify({
    hookSpecificOutput: { hookEventName: 'SessionStart', watchPaths: [wakeFile] }
  }));
  process.exit(0);
} catch {
  process.exit(0);
}