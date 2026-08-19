#!/usr/bin/env node
// CCCG state hook — UserPromptSubmit
// Injects a <cccg-state> block describing in-flight CCCG dispatch jobs so any
// Claude session always knows the current cross-agent work state without tool
// calls, surviving context compaction (state lives on disk, re-injected each
// turn). Silent when nothing is in flight — zero noise on idle.
//
// Stuck detection (stateless, age-based): queued >30min or running >60min is
// flagged with a break-loop style protocol so the coordinator diagnoses
// instead of waiting forever.
//
// Must be fast (<1s) and never crash: any error exits 0 silently.

'use strict';

const fs = require('fs');
const path = require('path');

try {
  const root = path.join(
    process.env.LOCALAPPDATA || '',
    'CCCG', 'dispatch', 'jobs'
  );
  if (!root || !fs.existsSync(root)) process.exit(0);

  const now = Date.now();
  const MAX_AGE_MS = 48 * 3600 * 1000; // ignore ancient job dirs entirely
  const rows = [];
  let stuck = 0;

  for (const name of fs.readdirSync(root)) {
    const dir = path.join(root, name);
    let st;
    try { st = fs.statSync(dir); } catch { continue; }
    if (!st.isDirectory() || now - st.mtimeMs > MAX_AGE_MS) continue;

    let job;
    try {
      job = JSON.parse(fs.readFileSync(path.join(dir, 'status.json'), 'utf8'));
    } catch { continue; }

    if (job.status !== 'queued' && job.status !== 'running') continue;

    const startedMs = Date.parse(job.startedAt || job.createdAt || 0) || st.mtimeMs;
    const ageMin = Math.floor((now - startedMs) / 60000);
    const limitMin = job.status === 'queued' ? 30 : 60;
    const isStuck = ageMin > limitMin;
    if (isStuck) stuck++;

    const caller = job.callerLabel || (job.hopCount >= 1 ? 'agent' : 'human');
    const target = `${job.provider}${job.sessionId ? ':' + String(job.sessionId).slice(0, 8) : ''}`;
    const model = job.model ? ` ${job.model}` : '';
    rows.push(
      `- [${job.status}${isStuck ? ' ⚠STUCK' : ''}] ${caller} → ${target}${model} · ${ageMin}m · job ${job.jobId}`
    );
  }

  // Bridge-shim presence check (gh#86012): every Desktop/engine auto-update
  // installs a clean engine dir and silently drops the shim, breaking CCD
  // cross-session messaging until someone notices. Warn on the first turn
  // instead. Engine dirs live under the MSIX LocalCache path (the visible
  // %APPDATA% variant is a container-only virtualized view).
  const shimWarnings = [];
  try {
    const engineRoot = path.join(
      process.env.LOCALAPPDATA || '', 'Packages',
      'Claude_pzs8sxrjxfjjc', 'LocalCache', 'Roaming', 'Claude', 'claude-code'
    );
    if (fs.existsSync(engineRoot)) {
      // Only the highest engine version matters — Desktop launches the
      // newest dir; older dirs are superseded leftovers (stale stock copies
      // of already-shimmed engines would otherwise false-alarm).
      const versions = fs.readdirSync(engineRoot)
        .filter(v => /^\d+\.\d+\.\d+$/.test(v))
        .sort((a, b) => {
          const pa = a.split('.').map(Number), pb = b.split('.').map(Number);
          return (pa[0] - pb[0]) || (pa[1] - pb[1]) || (pa[2] - pb[2]);
        });
      const v = versions[versions.length - 1];
      if (v) {
        const dir = path.join(engineRoot, v);
        const hasManifest = fs.existsSync(path.join(dir, '.bridge-shim-manifest.json'));
        const hasSidecar = fs.readdirSync(dir).some(n => n.startsWith('claude.anthropic-'));
        if (!hasManifest || !hasSidecar) {
          shimWarnings.push(
            `⚠️ Engine ${v} has NO bridge shim (likely a fresh auto-update). CCD cross-session ` +
            `messaging is presumed BROKEN on it (gh#86012). Do not dispatch via send_message without a ` +
            `single-shot transcript-verified test; reinstall via experiments/claude-desktop-bridge-shim ` +
            `(adapt install-test-shim-*.ps1: new version + SHA-256, PS 5.1 compat, LocalCache path).`
          );
        }
      }
    }
  } catch { /* never block the turn on this check */ }

  if (rows.length === 0 && shimWarnings.length === 0) process.exit(0); // idle = silent, no noise

  const lines = ['<cccg-state>'];
  if (rows.length > 0) lines.push(`In-flight CCCG dispatches: ${rows.length}`);
  lines.push(...rows.slice(0, 8));
  if (rows.length > 8) lines.push(`… and ${rows.length - 8} more`);
  lines.push(...shimWarnings);

  if (stuck > 0) {
    lines.push('');
    lines.push(`⚠️ ${stuck} job(s) exceeded expected duration. BREAK-LOOP PROTOCOL:`);
    lines.push('  1. Do NOT keep waiting passively — diagnose now (cccg_job_status, stderr/stdout tails).');
    lines.push('  2. Known causes: provider quota exhausted (check for 402/usage-limit in job stdout), hung provider GUI engine, worker killed.');
    lines.push('  3. Options: collect partial result, re-dispatch to another provider, or report the blocker to the user explicitly.');
    lines.push('  4. Do not re-send the same dispatch unchanged — that caused the wait.');
  }

  lines.push('</cccg-state>');
  process.stdout.write(lines.join('\n'));
  process.exit(0);
} catch {
  process.exit(0);
}
