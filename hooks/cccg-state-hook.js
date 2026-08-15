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

  if (rows.length === 0) process.exit(0); // idle = silent, no noise

  const lines = ['<cccg-state>', `In-flight CCCG dispatches: ${rows.length}`];
  lines.push(...rows.slice(0, 8));
  if (rows.length > 8) lines.push(`… and ${rows.length - 8} more`);

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
