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
const { execFileSync } = require('child_process');

// Resolve the Claude engine's pid: the first claude.exe ancestor of this
// hook process. The Host records ITS parent pid (always the engine, see
// WakeNotifier), so recording the same number here gives exact wake
// routing. Hooks may be spawned through a shell on Windows, so the direct
// parent is not necessarily the engine; walk up to four levels with one
// PowerShell call (~0.5s, once per session start). Falls back to the
// direct parent pid when the walk fails.
function resolveEnginePid() {
  const fallback = process.ppid || null;
  if (process.platform !== 'win32') return fallback;
  try {
    // No double quotes inside the script: PowerShell's command-line parsing of
    // escaped quotes is unreliable, single quotes are not.
    const script = '$p=' + process.ppid + "; foreach ($i in 1..5) { $x = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $p); if (-not $x) { break }; Write-Output ($x.ProcessId.ToString() + ' ' + $x.Name); $p = $x.ParentProcessId }";
    const out = execFileSync('powershell', ['-NoProfile', '-NonInteractive', '-Command', script], { encoding: 'utf8', timeout: 4000, windowsHide: true });
    for (const line of out.split(/\r?\n/)) {
      const m = line.trim().match(/^(\d+)\s+(.+)$/);
      if (m && /^claude(\.exe)?$/i.test(m[2].trim())) return Number(m[1]);
    }
  } catch { /* fall back */ }
  return fallback;
}

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
    JSON.stringify({ sessionId, cwd: payload.cwd || process.cwd(), registeredAt: new Date().toISOString(), lastSeenAt: new Date().toISOString(), enginePid: resolveEnginePid() }, null, 2),
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