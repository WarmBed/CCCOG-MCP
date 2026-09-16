#!/usr/bin/env node
// CCCG state hook — UserPromptSubmit
// Injects a <cccg-state> block so any Claude session always knows the
// current cross-agent work state without tool calls, surviving context
// compaction (state lives on disk, re-injected each turn). Silent when
// there is nothing to say — zero noise on idle.
//
// Sections, each omitted when empty:
//   1. In-flight dispatches (queued/running), with stuck detection
//      (stateless, age-based: queued >30min or running >60min) and a
//      break-loop protocol so the coordinator diagnoses instead of waiting.
//   2. Completed since this session's previous turn: jobs that reached a
//      terminal status after the per-session cursor. This is the "push"
//      half CCCG otherwise lacks — cccg_dispatch is fire-and-forget and a
//      finished job used to sit silent until someone happened to poll it.
//      The cursor lives in dispatch\watch\hook-<session>.json; a session's
//      first turn only sets the baseline (no history dump).
//   3. Unread CCCG inbox notes addressed to claude (job result mirrors and
//      peer notes), so an unacked result is surfaced every turn until read.
//   4. Engine bridge-shim check (gh#86012).
//
// Must be fast (<1s) and never crash: any error exits 0 silently.

'use strict';

const fs = require('fs');
const path = require('path');

try {
  const dispatchRoot = path.join(process.env.LOCALAPPDATA || '', 'CCCG', 'dispatch');
  const jobsRoot = path.join(dispatchRoot, 'jobs');
  if (!process.env.LOCALAPPDATA || !fs.existsSync(jobsRoot)) process.exit(0);

  const now = Date.now();
  const MAX_AGE_MS = 48 * 3600 * 1000; // ignore ancient job dirs entirely
  const MAX_ROWS = 8;

  // Hook input (Claude Code passes JSON on stdin): session_id keys the
  // per-session cursor. Never block on a TTY when run by hand.
  let sessionId = 'anon';
  try {
    if (!process.stdin.isTTY) {
      const raw = fs.readFileSync(0, 'utf8');
      if (raw.trim()) {
        const payload = JSON.parse(raw);
        if (payload && typeof payload.session_id === 'string' && payload.session_id) {
          sessionId = payload.session_id.replace(/[^A-Za-z0-9._-]/g, '_').slice(0, 80);
        }
      }
    }
  } catch { /* stdin absent or not JSON: fall back to a shared cursor */ }

  const watchRoot = path.join(dispatchRoot, 'watch');
  const cursorPath = path.join(watchRoot, `hook-${sessionId}.json`);
  let lastSeenMs = null;
  try {
    const cursor = JSON.parse(fs.readFileSync(cursorPath, 'utf8'));
    if (cursor && Number.isFinite(cursor.lastSeenMs)) lastSeenMs = cursor.lastSeenMs;
  } catch { /* first turn for this session: baseline only */ }

  const inflight = [];
  const completed = [];
  let stuck = 0;

  const label = (job) => {
    const caller = job.callerLabel || (job.hopCount >= 1 ? 'agent' : 'human');
    const target = `${job.provider}${job.sessionId ? ':' + String(job.sessionId).slice(0, 8) : ''}`;
    const model = job.model ? ` ${job.model}` : '';
    return `${caller} → ${target}${model}`;
  };

  for (const name of fs.readdirSync(jobsRoot)) {
    const dir = path.join(jobsRoot, name);
    let st;
    try { st = fs.statSync(dir); } catch { continue; }
    if (!st.isDirectory() || now - st.mtimeMs > MAX_AGE_MS) continue;

    let job;
    try {
      job = JSON.parse(fs.readFileSync(path.join(dir, 'status.json'), 'utf8'));
    } catch { continue; }

    if (job.status === 'queued' || job.status === 'running') {
      const startedMs = Date.parse(job.startedAt || job.createdAt || 0) || st.mtimeMs;
      const ageMin = Math.floor((now - startedMs) / 60000);
      const limitMin = job.status === 'queued' ? 30 : 60;
      const isStuck = ageMin > limitMin;
      if (isStuck) stuck++;
      inflight.push({
        sortMs: startedMs,
        text: `- [${job.status}${isStuck ? ' ⚠STUCK' : ''}] ${label(job)} · ${ageMin}m · job ${job.jobId}`
      });
      continue;
    }

    if (lastSeenMs === null) continue; // first turn: baseline, no history
    if (job.status !== 'succeeded' && job.status !== 'failed') continue;
    const finishedMs = Date.parse(job.finishedAt || 0) || st.mtimeMs;
    if (finishedMs <= lastSeenMs) continue;

    const agoMin = Math.max(0, Math.floor((now - finishedMs) / 60000));
    const extras = [];
    if (job.retryCount > 0) extras.push(`retried ×${job.retryCount}`);
    if (job.status === 'failed' && job.error) extras.push(`error: ${String(job.error).slice(0, 160)}`);
    completed.push({
      sortMs: finishedMs,
      text: `- [${job.cancelledAt ? 'cancelled' : job.status}] ${label(job)} · ${agoMin}m ago · job ${job.jobId}`
        + (extras.length ? ` · ${extras.join(' · ')}` : '')
    });
  }

  // A human prompt resets the wake budget (see cccg-wake-hook.js).
  try { fs.unlinkSync(path.join(dispatchRoot, 'wake', sessionId, 'wakes')); } catch { /* none */ }

  // Advance the cursor before rendering so a crash below cannot re-announce.
  try {
    fs.mkdirSync(watchRoot, { recursive: true });
    fs.writeFileSync(cursorPath, JSON.stringify({ lastSeenMs: now, sessionId }), 'utf8');
  } catch { /* cursor is a convenience; never block the turn on it */ }

  // Unread inbox notes addressed to claude.
  const unread = [];
  try {
    const inboxPath = path.join(dispatchRoot, 'inbox.jsonl');
    if (fs.existsSync(inboxPath)) {
      for (const line of fs.readFileSync(inboxPath, 'utf8').split('\n')) {
        if (!line.trim()) continue;
        let note;
        try { note = JSON.parse(line); } catch { continue; }
        if (note.status !== 'pending' || note.toRole !== 'claude') continue;
        const createdMs = Date.parse(note.createdAt || 0) || 0;
        if (now - createdMs > MAX_AGE_MS) continue;
        unread.push({ sortMs: createdMs, note });
      }
    }
  } catch { /* mailbox unreadable: skip the section */ }

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
        // Engines verified to deliver cross-session messages STOCK (no shim),
        // by single-shot transcript-verified round-trip on this machine.
        // 2.1.237: verified 2026-08-21 (upstream fix landed; shim era over).
        // 2.1.246: verified 2026-08-27 by SEO1: single-shot send to Doc1 confirmed in recipient transcript.
        // 2.1.260: verified 2026-09-06 by nuclei-c0: single-shot send to Doc1 confirmed in recipient transcript (marker CCD-RECV-20260906-0121).
        const STOCK_OK = ['2.1.237', '2.1.246', '2.1.260', '2.1.270', '2.1.271'];
        if (!STOCK_OK.includes(v)) {
          const hasManifest = fs.existsSync(path.join(dir, '.bridge-shim-manifest.json'));
          const hasSidecar = fs.readdirSync(dir).some(n => n.startsWith('claude.anthropic-'));
          if (!hasManifest || !hasSidecar) {
            shimWarnings.push(
              `⚠️ NEW engine ${v} — CCD cross-session delivery UNVERIFIED on it (gh#86012 history: ` +
              `some engines shipped broken). Before any send_message dispatching, run ONE single-shot ` +
              `receipt test (transcript-verified, never retry on failure — a failed delivery jams the ` +
              `recipient ~1000s). If it fails: shim reinstall procedure in experiments/claude-desktop-bridge-shim; ` +
              `if it passes: add ${v} to STOCK_OK in hooks/cccg-state-hook.js.`
            );
          }
        }
      }
    }
  } catch { /* never block the turn on this check */ }

  if (inflight.length === 0 && completed.length === 0 && unread.length === 0 && shimWarnings.length === 0) {
    process.exit(0); // idle = silent, no noise
  }

  const byNewest = (a, b) => b.sortMs - a.sortMs;
  const lines = ['<cccg-state>'];

  if (inflight.length > 0) {
    inflight.sort(byNewest);
    lines.push(`In-flight CCCG dispatches: ${inflight.length}`);
    lines.push(...inflight.slice(0, MAX_ROWS).map(r => r.text));
    if (inflight.length > MAX_ROWS) lines.push(`… and ${inflight.length - MAX_ROWS} more`);
  }

  if (completed.length > 0) {
    completed.sort(byNewest);
    if (lines.length > 1) lines.push('');
    lines.push(`Completed since your previous turn: ${completed.length} (collect with cccg_job_collect)`);
    lines.push(...completed.slice(0, MAX_ROWS).map(r => r.text));
    if (completed.length > MAX_ROWS) lines.push(`… and ${completed.length - MAX_ROWS} more`);
  }

  if (unread.length > 0) {
    unread.sort(byNewest);
    if (lines.length > 1) lines.push('');
    lines.push(`📬 Unread CCCG inbox notes for claude: ${unread.length} (cccg_inbox_list unreadOnly=true, then cccg_inbox_ack)`);
    for (const { note } of unread.slice(0, 3)) {
      const from = note.fromProvider || note.fromRole || '?';
      const jobRef = note.jobId ? ` · job ${note.jobId}` : '';
      const preview = String(note.content || '').replace(/\s+/g, ' ').slice(0, 100);
      lines.push(`- from ${from}${jobRef} · id ${note.id} · "${preview}"`);
    }
  }

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
