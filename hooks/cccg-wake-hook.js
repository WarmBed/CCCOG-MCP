#!/usr/bin/env node
// CCCG wake hook — FileChanged (configure with "asyncRewake": true)
// Fires when this session's wake file (dispatch\wake\<session>\cccg_wake)
// changes, i.e. when CCCG's WakeNotifier recorded a terminal job for this
// session. Exits 2 with a one-screen summary on stderr; with asyncRewake the
// engine wakes the model and shows that summary as a system reminder.
//
// Bounded: at most WAKE_CAP wakes between human prompts (the state hook
// resets the counter on UserPromptSubmit), so two agents cannot volley.
// Exits 0 (no wake) when the change is not for this session, the file is
// empty, or the job was already announced.
//
// Must be fast (<1s) and never crash: any error exits 0 silently.

'use strict';

const fs = require('fs');
const path = require('path');

const WAKE_CAP = Number(process.env.CCCG_WAKE_CAP) > 0 ? Number(process.env.CCCG_WAKE_CAP) : 5;

try {
  const raw = process.stdin.isTTY ? '' : fs.readFileSync(0, 'utf8');
  const payload = raw.trim() ? JSON.parse(raw) : {};
  const sessionId = String(payload.session_id || '').replace(/[^A-Za-z0-9._-]/g, '_').slice(0, 80);
  if (!sessionId || !process.env.LOCALAPPDATA) process.exit(0);

  const wakeDir = path.join(process.env.LOCALAPPDATA, 'CCCG', 'dispatch', 'wake', sessionId);
  const wakeFile = path.join(wakeDir, 'cccg_wake');
  // Only react to our own wake file (the engine hands us the changed path).
  if (payload.file_path && path.resolve(payload.file_path).toLowerCase() !== wakeFile.toLowerCase()) process.exit(0);
  if (payload.event === 'unlink') process.exit(0);

  let job;
  try { job = JSON.parse(fs.readFileSync(wakeFile, 'utf8')); } catch { process.exit(0); }
  if (!job || !job.jobId) process.exit(0);

  // Announce each job once.
  const announcedPath = path.join(wakeDir, 'announced');
  let announced = [];
  try { announced = JSON.parse(fs.readFileSync(announcedPath, 'utf8')); } catch { /* none */ }
  if (announced.includes(job.jobId)) process.exit(0);
  announced = announced.slice(-200); announced.push(job.jobId);
  try { fs.writeFileSync(announcedPath, JSON.stringify(announced), 'utf8'); } catch { /* best effort */ }

  // Cap wakes per human prompt.
  const wakesPath = path.join(wakeDir, 'wakes');
  let wakes = 0;
  try { wakes = Number(fs.readFileSync(wakesPath, 'utf8')) || 0; } catch { /* none */ }
  if (wakes >= WAKE_CAP) process.exit(0);
  try { fs.writeFileSync(wakesPath, String(wakes + 1), 'utf8'); } catch { /* best effort */ }

  const status = job.cancelled ? 'cancelled' : job.status;
  const who = job.callerLabel ? `${job.callerLabel} → ` : '';
  const target = `${job.provider}${job.sessionId ? ':' + String(job.sessionId).slice(0, 8) : ''}${job.model ? ' ' + job.model : ''}`;
  const lines = [
    `CCCG job finished: [${status}] ${who}${target} · job ${job.jobId}`,
  ];
  if (job.retryCount > 0) lines.push(`retried ×${job.retryCount}`);
  if (job.error) lines.push(`error: ${String(job.error).slice(0, 200)}`);
  lines.push(`If this session dispatched it: collect with cccg_job_collect("${job.jobId}"), then act or report. If you did not dispatch it, ignore this wake. (wake ${wakes + 1}/${WAKE_CAP} since your last prompt)`);
  process.stderr.write(lines.join('\n'));
  process.exit(2);
} catch {
  process.exit(0);
}