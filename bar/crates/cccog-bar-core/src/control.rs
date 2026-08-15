//! CCCG dispatch control-flow rows for the Flow section.
//!
//! Ported from TokenBar-Windows `crates/tb_core_ffi/src/agent_cccog.rs`
//! (upstream commit `a829d51deb0f37bcff211c83de5e9d2f99f7663f`) — the
//! dispatch-root walk, the Active + last-2h terminal window, same-path `×N`
//! aggregation, and chain building from `hopChain`/`callerLabel` are reused
//! (adapted from that file's `Vec<String>` segment chain to this module's
//! flat `source`/`target` row shape), on top of this crate's own
//! `dispatch::parse_status` / `owners::{is_archived_path, parse_owner}` /
//! `privacy::summarize_prompt` — the same primitives TokenBar's adapter
//! already depends on via its vendored copy of this crate. See
//! `bar/SYNC.md` for the full derivation record.
//!
//! Two things here are CCCOG-original, not from that reference:
//! - **Stale-active collapse**: an active job queued/running past 6 hours
//!   folds into one synthetic `stale ×N` row instead of listing forever as
//!   "active" (TokenBar's Active rows have no age ceiling — CCCOG has always
//!   had this rule, from before this port).
//! - **Target title resolution**: TokenBar's target is the bare provider
//!   name. CCCOG shows `provider·title`, where `title` comes from the
//!   earliest dispatch job anywhere in the jobs tree (not window-limited)
//!   that targeted that exact session, using that job's own prompt summary.
//!   This is NOT a native per-provider session-store read — Codex, Claude,
//!   and Grok each store sessions in an incompatible, unreverse-engineered
//!   format, and reading three native stores for a title field was out of
//!   reach for this pass. It is CCCG's own dispatch history used as the
//!   best available honest proxy. Falls back to `provider·{first 8 id
//!   chars}` when no such job exists — exactly the fallback shape asked
//!   for.

use crate::dispatch::parse_status;
use crate::owners::{is_archived_path, parse_owner};
use crate::privacy::summarize_prompt;
use chrono::{DateTime, SecondsFormat, Utc};
use serde::Serialize;
use std::path::Path;

const PROMPT_MAX_READ: usize = 8 * 1024;
const SUMMARY_MAX_BYTES: usize = 240;
const LABEL_MAX_BYTES: usize = 80;
/// Target titles are meant to read like "SEO總管" — short, not a summary.
const TITLE_MAX_BYTES: usize = 24;
const STATUS_MAX_BYTES: usize = 32;
/// Terminal jobs stay visible for two hours after their authoritative time
/// (TokenBar's `agent_cccog.rs` constant, reused unchanged).
const TERMINAL_WINDOW_SECS: i64 = 2 * 60 * 60;
/// CCCOG-original: active jobs queued/running past this age collapse into
/// one `stale ×N` row (pre-dates this port; kept unchanged).
const STALE_ACTIVE_SECS: i64 = 6 * 60 * 60;
const MAX_ROWS: usize = 200;
const MAX_TOOLTIP_SUMMARIES: usize = 5;
const MAX_DIAGNOSTICS: usize = 8;
const AMBIGUOUS_SOURCE: &str = "unknown";

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ControlRow {
    pub source: String,
    pub target: String,
    /// Normalized provider id ("codex"/"claude"/"grok"/"owner"/"unknown") —
    /// what the shell colors the status dot by.
    pub target_provider: String,
    pub status: String,
    pub active: bool,
    /// True only for the synthesized "stale ×N" aggregate row.
    pub is_stale_group: bool,
    /// Active rows: seconds since start. Terminal rows: seconds since the
    /// newest authoritative time (age). Absent when no usable timestamp.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub elapsed_seconds: Option<u64>,
    /// 1 for an unaggregated row; N for a same-path terminal reduction (or
    /// the stale-group size).
    pub count: u32,
    pub task_summary: String,
    /// Bounded, newest-first distinct summaries for the hover tooltip.
    pub task_summaries: Vec<String>,
    /// True when `source` is the ambiguous "unknown" fallback — the shell
    /// renders this muted rather than full-brightness.
    pub source_is_unknown: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ControlPayload {
    /// False only when the CCCG dispatch root is absent.
    pub available: bool,
    pub generated_at: String,
    pub rows: Vec<ControlRow>,
    pub diagnostics: Vec<String>,
    pub truncated: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_dispatch_at: Option<String>,
}

struct JobEntry {
    provider: String,
    session_id: Option<String>,
    caller_label: Option<String>,
    hop_chain: Option<String>,
    status: String,
    active: bool,
    /// Active: start time. Terminal: authoritative completion time.
    time: Option<DateTime<Utc>>,
    created_at: Option<DateTime<Utc>>,
    summary: String,
    /// Deterministic tie-breaker only; never serialized.
    job_id: String,
}

/// Resolve the dispatch root and build the windowed, aggregated Flow view.
pub fn snapshot_at(root: Option<&Path>, now: DateTime<Utc>) -> ControlPayload {
    let generated_at = now.to_rfc3339_opts(SecondsFormat::Millis, true);
    let Some(root) = root.filter(|root| root.is_dir()) else {
        return ControlPayload {
            available: false,
            generated_at,
            rows: Vec::new(),
            diagnostics: Vec::new(),
            truncated: false,
            last_dispatch_at: None,
        };
    };

    let mut jobs = Vec::new();
    let mut diagnostics = Vec::new();
    collect_jobs(&root.join("jobs"), &mut jobs, &mut diagnostics);

    let mut owner_rows = collect_live_owner_rows(&root.join("owners"), &mut diagnostics);

    let last_dispatch_at = jobs
        .iter()
        .filter_map(|job| job.time)
        .max()
        .map(|time| time.to_rfc3339_opts(SecondsFormat::Millis, true));

    let mut active_rows = build_active_rows(&jobs, now);
    let mut terminal_rows = build_terminal_rows(&jobs, now);

    let mut rows = Vec::new();
    rows.append(&mut active_rows);
    rows.append(&mut owner_rows);
    rows.append(&mut terminal_rows);
    let truncated = rows.len() > MAX_ROWS;
    rows.truncate(MAX_ROWS);

    dedupe_and_cap(&mut diagnostics);

    ControlPayload {
        available: true,
        generated_at,
        rows,
        diagnostics,
        truncated,
        last_dispatch_at,
    }
}

/// Fresh (<=6h) active jobs list individually, newest-elapsed first; jobs
/// stale past 6h collapse into one synthetic row.
fn build_active_rows(jobs: &[JobEntry], now: DateTime<Utc>) -> Vec<ControlRow> {
    let mut actives: Vec<&JobEntry> = jobs.iter().filter(|job| job.active).collect();
    actives.sort_by(|a, b| {
        elapsed_for_sort(a, now)
            .cmp(&elapsed_for_sort(b, now))
            .then_with(|| a.job_id.cmp(&b.job_id))
    });

    let mut rows = Vec::new();
    let mut stale: Vec<&JobEntry> = Vec::new();
    for job in actives {
        let age = elapsed_for_sort(job, now);
        if age != u64::MAX && age as i64 > STALE_ACTIVE_SECS {
            stale.push(job);
        } else {
            rows.push(job_row(job, jobs, now, 1, summaries_of(&[job])));
        }
    }
    if !stale.is_empty() {
        rows.push(ControlRow {
            source: AMBIGUOUS_SOURCE.to_owned(),
            target: format!("stale ×{}", stale.len()),
            target_provider: "unknown".to_owned(),
            status: "queued".to_owned(),
            active: true,
            is_stale_group: true,
            elapsed_seconds: None,
            count: u32::try_from(stale.len()).unwrap_or(u32::MAX),
            task_summary: String::new(),
            task_summaries: summaries_of(&stale),
            source_is_unknown: true,
        });
    }
    rows
}

/// Terminal jobs inside the 2h window, reduced by identical (source,
/// target) display path — same-path aggregation, ported from
/// `agent_cccog.rs`'s segment-equality grouping.
fn build_terminal_rows(jobs: &[JobEntry], now: DateTime<Utc>) -> Vec<ControlRow> {
    struct Windowed<'a> {
        job: &'a JobEntry,
        source: String,
        target: String,
        target_provider: String,
        source_is_unknown: bool,
    }

    let mut terminals: Vec<Windowed> = jobs
        .iter()
        .filter(|job| !job.active)
        .filter(|job| {
            job.time.is_some_and(|time| {
                let age = (now - time).num_seconds();
                (0..=TERMINAL_WINDOW_SECS).contains(&age)
            })
        })
        .map(|job| {
            let (source, source_is_unknown) = source_label(job);
            let (target, target_provider) = target_label(job, jobs);
            Windowed {
                job,
                source,
                target,
                target_provider,
                source_is_unknown,
            }
        })
        .collect();

    terminals.sort_by(|a, b| {
        b.job
            .time
            .cmp(&a.job.time)
            .then_with(|| a.job.job_id.cmp(&b.job.job_id))
    });

    let mut grouped: Vec<(String, String, Vec<usize>)> = Vec::new();
    for (index, item) in terminals.iter().enumerate() {
        match grouped
            .iter_mut()
            .find(|(source, target, _)| *source == item.source && *target == item.target)
        {
            Some((_, _, members)) => members.push(index),
            None => grouped.push((item.source.clone(), item.target.clone(), vec![index])),
        }
    }
    grouped.sort_by(|a, b| {
        let newest_a = terminals[a.2[0]].job.time;
        let newest_b = terminals[b.2[0]].job.time;
        newest_b
            .cmp(&newest_a)
            .then_with(|| (a.0.clone(), a.1.clone()).cmp(&(b.0.clone(), b.1.clone())))
    });

    let mut rows = Vec::new();
    for (_, _, members) in grouped {
        let newest = &terminals[members[0]];
        let jobs_in_group: Vec<&JobEntry> = members.iter().map(|&i| terminals[i].job).collect();
        let count = u32::try_from(members.len()).unwrap_or(u32::MAX);
        rows.push(ControlRow {
            source: newest.source.clone(),
            target: newest.target.clone(),
            target_provider: newest.target_provider.clone(),
            status: newest.job.status.clone(),
            active: false,
            is_stale_group: false,
            elapsed_seconds: newest
                .job
                .time
                .map(|time| (now - time).num_seconds().max(0) as u64),
            count,
            task_summary: newest.job.summary.clone(),
            task_summaries: summaries_of(&jobs_in_group),
            source_is_unknown: newest.source_is_unknown,
        });
    }
    rows
}

fn job_row(
    job: &JobEntry,
    all_jobs: &[JobEntry],
    now: DateTime<Utc>,
    count: u32,
    task_summaries: Vec<String>,
) -> ControlRow {
    let elapsed_seconds = job
        .time
        .map(|time| (now - time).num_seconds().max(0) as u64);
    let (source, source_is_unknown) = source_label(job);
    let (target, target_provider) = target_label(job, all_jobs);
    ControlRow {
        source,
        target,
        target_provider,
        status: job.status.clone(),
        active: job.active,
        is_stale_group: false,
        elapsed_seconds,
        count,
        task_summary: job.summary.clone(),
        task_summaries,
        source_is_unknown,
    }
}

/// `callerLabel` wins when present; otherwise the last `hopChain` hop;
/// otherwise the explicit ambiguous fallback. `hopSource` (user@machine) is
/// deliberately never used — it would leak identity the payload must not
/// carry (same rule as `agent_cccog.rs`).
fn source_label(job: &JobEntry) -> (String, bool) {
    if let Some(label) = job
        .caller_label
        .as_deref()
        .map(str::trim)
        .filter(|label| !label.is_empty())
    {
        return (sanitize_label(label, LABEL_MAX_BYTES), false);
    }
    if let Some(chain) = job.hop_chain.as_deref() {
        if let Some(last) = chain
            .split('>')
            .map(str::trim)
            .filter(|segment| !segment.is_empty())
            .last()
        {
            return (sanitize_label(last, LABEL_MAX_BYTES), false);
        }
    }
    (AMBIGUOUS_SOURCE.to_owned(), true)
}

/// `provider·title` where `title` is resolved from CCCG's own dispatch
/// history for that exact session (see module doc); falls back to
/// `provider·{first 8 id chars}` with no session id or no resolvable title.
fn target_label(job: &JobEntry, all_jobs: &[JobEntry]) -> (String, String) {
    let provider = sanitize_label(&job.provider, LABEL_MAX_BYTES).to_lowercase();
    let provider = if provider.is_empty() {
        "provider".to_owned()
    } else {
        provider
    };
    let Some(session_id) = job.session_id.as_deref().filter(|id| !id.is_empty()) else {
        return (provider.clone(), provider);
    };
    let title = resolve_title(&provider, session_id, all_jobs);
    let suffix = title.unwrap_or_else(|| session_id.chars().take(8).collect());
    (format!("{provider}·{suffix}"), provider)
}

fn resolve_title(provider: &str, session_id: &str, jobs: &[JobEntry]) -> Option<String> {
    jobs.iter()
        .filter(|job| job.provider.eq_ignore_ascii_case(provider))
        .filter(|job| job.session_id.as_deref() == Some(session_id))
        .filter(|job| !job.summary.is_empty())
        .min_by(|a, b| compare_earliest(a, b))
        .map(|job| sanitize_label(&job.summary, TITLE_MAX_BYTES))
}

fn compare_earliest(a: &JobEntry, b: &JobEntry) -> std::cmp::Ordering {
    match (a.created_at, b.created_at) {
        (Some(x), Some(y)) => x.cmp(&y).then_with(|| a.job_id.cmp(&b.job_id)),
        (Some(_), None) => std::cmp::Ordering::Less,
        (None, Some(_)) => std::cmp::Ordering::Greater,
        (None, None) => a.job_id.cmp(&b.job_id),
    }
}

fn elapsed_for_sort(job: &JobEntry, now: DateTime<Utc>) -> u64 {
    job.time
        .map(|time| (now - time).num_seconds().max(0) as u64)
        .unwrap_or(u64::MAX)
}

/// Newest-first distinct non-empty summaries, bounded for the tooltip.
fn summaries_of(members: &[&JobEntry]) -> Vec<String> {
    let mut out: Vec<String> = Vec::new();
    for member in members {
        if member.summary.is_empty() || out.iter().any(|have| have == &member.summary) {
            continue;
        }
        out.push(member.summary.clone());
        if out.len() >= MAX_TOOLTIP_SUMMARIES {
            break;
        }
    }
    out
}

fn dedupe_and_cap(diagnostics: &mut Vec<String>) {
    let mut seen: Vec<String> = Vec::new();
    diagnostics.retain(|diagnostic| {
        if seen.iter().any(|have| have == diagnostic) {
            false
        } else {
            seen.push(diagnostic.clone());
            true
        }
    });
    diagnostics.truncate(MAX_DIAGNOSTICS);
}

fn collect_jobs(dir: &Path, jobs: &mut Vec<JobEntry>, diagnostics: &mut Vec<String>) {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return; // missing jobs/ is simply "no jobs"
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if is_archived_path(&path) {
            continue;
        }
        if path.is_dir() {
            collect_jobs(&path, jobs, diagnostics);
            continue;
        }
        if path.file_name().and_then(|name| name.to_str()) != Some("status.json") {
            continue;
        }
        let Ok(raw) = std::fs::read_to_string(&path) else {
            diagnostics.push("unreadable dispatch status skipped".to_owned());
            continue;
        };
        let Ok(status) = parse_status(&raw) else {
            diagnostics.push("invalid dispatch status skipped".to_owned());
            continue;
        };

        let normalized_status = sanitize_label(&status.status, STATUS_MAX_BYTES).to_lowercase();
        let active = is_active_status(&normalized_status);
        let started = parse_time(status.started_at.as_deref());
        let created = parse_time(status.created_at.as_deref());
        let finished = parse_time(status.finished_at.as_deref());
        let time = if active {
            started.or(created)
        } else {
            finished.or(started).or(created)
        };
        let summary = path
            .parent()
            .map(|parent| parent.join("prompt.txt"))
            .and_then(|prompt| std::fs::read(prompt).ok())
            .map(|bytes| summarize_prompt(&bytes, PROMPT_MAX_READ, SUMMARY_MAX_BYTES))
            .unwrap_or_default();

        jobs.push(JobEntry {
            provider: status.provider,
            session_id: status.session_id.or(status.requested_session_id),
            caller_label: status.caller_label,
            hop_chain: status.hop_chain,
            status: normalized_status,
            active,
            time,
            created_at: created,
            summary,
            job_id: status.job_id,
        });
    }
}

/// Live owner leases render as active "owner → provider" relations, one row
/// per provider with a count. Stale leases (no `.lock`) produce no rows.
fn collect_live_owner_rows(dir: &Path, diagnostics: &mut Vec<String>) -> Vec<ControlRow> {
    let mut live: Vec<String> = Vec::new();
    collect_live_owner_providers(dir, &mut live, diagnostics);
    live.sort();
    let mut rows: Vec<ControlRow> = Vec::new();
    for provider in live {
        match rows.iter_mut().find(|row| row.target_provider == provider) {
            Some(row) => row.count = row.count.saturating_add(1),
            None => rows.push(ControlRow {
                source: "owner".to_owned(),
                target: provider.clone(),
                target_provider: provider,
                status: "live".to_owned(),
                active: true,
                is_stale_group: false,
                elapsed_seconds: None,
                count: 1,
                task_summary: String::new(),
                task_summaries: Vec::new(),
                source_is_unknown: false,
            }),
        }
    }
    rows
}

fn collect_live_owner_providers(dir: &Path, live: &mut Vec<String>, diagnostics: &mut Vec<String>) {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if is_archived_path(&path) {
            continue;
        }
        if path.is_dir() {
            collect_live_owner_providers(&path, live, diagnostics);
            continue;
        }
        if path.extension().and_then(|extension| extension.to_str()) != Some("json") {
            continue;
        }
        if !path.with_extension("lock").is_file() {
            continue; // stale lease — invisible
        }
        let Ok(raw) = std::fs::read_to_string(&path) else {
            diagnostics.push("unreadable owner registration skipped".to_owned());
            continue;
        };
        let Ok(owner) = parse_owner(&raw) else {
            diagnostics.push("invalid owner registration skipped".to_owned());
            continue;
        };
        if owner.session_id.as_deref().is_none_or(str::is_empty) {
            continue;
        }
        let provider = sanitize_label(&owner.provider, LABEL_MAX_BYTES).to_lowercase();
        if !provider.is_empty() {
            live.push(provider);
        }
    }
}

/// Untrusted single-line display text: first line only, control characters
/// escaped, UTF-8-safe truncation.
fn sanitize_label(value: &str, max_bytes: usize) -> String {
    summarize_prompt(value.trim().as_bytes(), 4 * LABEL_MAX_BYTES, max_bytes)
        .trim()
        .to_owned()
}

fn is_active_status(status: &str) -> bool {
    matches!(status, "running" | "queued" | "starting" | "working")
}

fn parse_time(value: Option<&str>) -> Option<DateTime<Utc>> {
    DateTime::parse_from_rfc3339(value?.trim())
        .ok()
        .map(|time| time.with_timezone(&Utc))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn at(value: &str) -> DateTime<Utc> {
        DateTime::parse_from_rfc3339(value)
            .unwrap()
            .with_timezone(&Utc)
    }

    const NOW: &str = "2026-08-15T12:00:00Z";

    fn write_job(root: &Path, id: &str, status_json: &str, prompt: Option<&str>) {
        let dir = root.join("jobs").join(id);
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(dir.join("status.json"), status_json).unwrap();
        if let Some(prompt) = prompt {
            std::fs::write(dir.join("prompt.txt"), prompt).unwrap();
        }
    }

    #[test]
    fn missing_root_is_unavailable_and_never_created() {
        let base = tempfile::tempdir().unwrap();
        let missing = base.path().join("no-such-dispatch");
        let payload = snapshot_at(Some(&missing), at(NOW));
        assert!(!payload.available);
        assert!(payload.rows.is_empty());
        assert!(!missing.exists(), "adapter must not create the dispatch root");
    }

    #[test]
    fn default_view_is_windowed_not_a_raw_dump() {
        // A 3-day-old terminal job (older than the 2h window) must be
        // excluded from the default view — this is the exact shape of the
        // "33 raw rows with naked session-UUID targets" regression.
        let root = tempfile::tempdir().unwrap();
        write_job(
            root.path(),
            "old",
            r#"{"jobId":"old","provider":"claude","sessionId":"0a6b435a-174a-45ac-ba57-e699e2210291",
                "status":"succeeded","finishedAt":"2026-08-13T09:00:00Z"}"#,
            None,
        );
        write_job(
            root.path(),
            "recent",
            r#"{"jobId":"recent","provider":"codex","sessionId":"s-recent",
                "callerLabel":"doc1","status":"succeeded","finishedAt":"2026-08-15T11:30:00Z"}"#,
            None,
        );
        let payload = snapshot_at(Some(root.path()), at(NOW));
        assert_eq!(payload.rows.len(), 1, "the 3-day-old row must fall out of the window");
        assert_eq!(payload.rows[0].source, "doc1");
    }

    #[test]
    fn active_stale_past_six_hours_collapses_to_one_group() {
        let root = tempfile::tempdir().unwrap();
        for index in 0..3 {
            write_job(
                root.path(),
                &format!("stale-{index}"),
                &format!(
                    r#"{{"jobId":"stale-{index}","provider":"codex","sessionId":"s{index}",
                        "callerLabel":"doc1","status":"queued","startedAt":"2026-08-15T04:00:00Z"}}"#
                ),
                None,
            );
        }
        write_job(
            root.path(),
            "fresh",
            r#"{"jobId":"fresh","provider":"codex","sessionId":"sf",
                "callerLabel":"doc1","status":"running","startedAt":"2026-08-15T11:55:00Z"}"#,
            None,
        );
        let payload = snapshot_at(Some(root.path()), at(NOW));
        assert_eq!(payload.rows.len(), 2, "3 stale + 1 fresh collapse to 2 rows");
        let stale = payload.rows.iter().find(|row| row.is_stale_group).unwrap();
        assert_eq!(stale.count, 3);
        assert_eq!(stale.target, "stale ×3");
        let fresh = payload.rows.iter().find(|row| !row.is_stale_group).unwrap();
        assert_eq!(fresh.elapsed_seconds, Some(300));
    }

    #[test]
    fn terminal_same_path_aggregates_with_newest_status_and_count() {
        // Same source AND same target session (repeated dispatches into one
        // session) is the "same path" that aggregates — a different session
        // id changes the target label (it carries the session's resolved
        // title/id-prefix) and must NOT be folded in, or a real distinction
        // between sessions would silently disappear.
        let root = tempfile::tempdir().unwrap();
        for (id, status, finished) in [
            ("t1", "succeeded", "2026-08-15T11:00:00Z"),
            ("t2", "failed", "2026-08-15T11:40:00Z"),
            ("t3", "succeeded", "2026-08-15T11:20:00Z"),
        ] {
            write_job(
                root.path(),
                id,
                &format!(
                    r#"{{"jobId":"{id}","provider":"codex","sessionId":"s-shared",
                        "callerLabel":"doc1","status":"{status}","finishedAt":"{finished}"}}"#
                ),
                None,
            );
        }
        // A different session under the same caller must stay a separate row.
        write_job(
            root.path(),
            "other-session",
            r#"{"jobId":"other-session","provider":"codex","sessionId":"s-other",
                "callerLabel":"doc1","status":"succeeded","finishedAt":"2026-08-15T11:10:00Z"}"#,
            None,
        );
        let payload = snapshot_at(Some(root.path()), at(NOW));
        assert_eq!(payload.rows.len(), 2);
        let shared = payload
            .rows
            .iter()
            .find(|row| row.target.contains("s-shared"))
            .unwrap();
        assert_eq!(shared.count, 3);
        assert_eq!(shared.status, "failed", "newest member's status wins");
        let other = payload
            .rows
            .iter()
            .find(|row| row.target.contains("s-other"))
            .unwrap();
        assert_eq!(other.count, 1);
    }

    #[test]
    fn target_title_resolves_from_earliest_job_targeting_the_session_else_id_prefix() {
        let root = tempfile::tempdir().unwrap();
        write_job(
            root.path(),
            "origin",
            r#"{"jobId":"origin","provider":"codex","sessionId":"s-titled",
                "callerLabel":"doc1","status":"succeeded",
                "createdAt":"2026-08-15T09:00:00Z","finishedAt":"2026-08-15T09:05:00Z"}"#,
            Some("SEO總管 setup\n"),
        );
        write_job(
            root.path(),
            "followup",
            r#"{"jobId":"followup","provider":"codex","sessionId":"s-titled",
                "callerLabel":"doc1","status":"succeeded",
                "createdAt":"2026-08-15T11:00:00Z","finishedAt":"2026-08-15T11:50:00Z"}"#,
            Some("unrelated later prompt\n"),
        );
        write_job(
            root.path(),
            "untitled",
            r#"{"jobId":"untitled","provider":"grok","sessionId":"s-plain",
                "callerLabel":"doc1","status":"succeeded","finishedAt":"2026-08-15T11:55:00Z"}"#,
            None,
        );
        let payload = snapshot_at(Some(root.path()), at(NOW));
        let titled = payload
            .rows
            .iter()
            .find(|row| row.target.starts_with("codex·"))
            .unwrap();
        assert_eq!(titled.target, "codex·SEO總管 setup");
        let plain = payload
            .rows
            .iter()
            .find(|row| row.target.starts_with("grok·"))
            .unwrap();
        assert_eq!(plain.target, "grok·s-plain".chars().take(13).collect::<String>());
    }

    #[test]
    fn source_falls_back_to_hop_chain_then_ambiguous() {
        let root = tempfile::tempdir().unwrap();
        write_job(
            root.path(),
            "hop-only",
            r#"{"jobId":"hop-only","provider":"codex","sessionId":"s1",
                "hopChain":"human>doc2","status":"succeeded","finishedAt":"2026-08-15T11:45:00Z"}"#,
            None,
        );
        write_job(
            root.path(),
            "neither",
            r#"{"jobId":"neither","provider":"codex","sessionId":"s2",
                "status":"succeeded","finishedAt":"2026-08-15T11:50:00Z"}"#,
            None,
        );
        let payload = snapshot_at(Some(root.path()), at(NOW));
        let hop = payload.rows.iter().find(|r| r.target.contains("s1")).unwrap();
        assert_eq!(hop.source, "doc2");
        assert!(!hop.source_is_unknown);
        let none = payload
            .rows
            .iter()
            .find(|r| r.source == AMBIGUOUS_SOURCE)
            .unwrap();
        assert!(none.source_is_unknown);
    }

    #[test]
    fn live_owner_leases_collapse_by_provider_and_stale_ones_are_invisible() {
        let root = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(root.path().join("jobs")).unwrap();
        let owners = root.path().join("owners");
        std::fs::create_dir_all(&owners).unwrap();
        std::fs::write(
            owners.join("live1.json"),
            r#"{"schemaVersion":1,"provider":"claude","sessionId":"a"}"#,
        )
        .unwrap();
        std::fs::write(owners.join("live1.lock"), "live").unwrap();
        std::fs::write(
            owners.join("live2.json"),
            r#"{"schemaVersion":1,"provider":"claude","sessionId":"b"}"#,
        )
        .unwrap();
        std::fs::write(owners.join("live2.lock"), "live").unwrap();
        std::fs::write(
            owners.join("stale.json"),
            r#"{"schemaVersion":1,"provider":"grok","sessionId":"c"}"#,
        )
        .unwrap();
        let payload = snapshot_at(Some(root.path()), at(NOW));
        assert_eq!(payload.rows.len(), 1);
        assert_eq!(payload.rows[0].target_provider, "claude");
        assert_eq!(payload.rows[0].count, 2);
    }

    #[test]
    fn archived_and_torn_entries_are_invisible_and_degrade_to_diagnostics() {
        let root = tempfile::tempdir().unwrap();
        let archived = root.path().join("jobs").join("cccg-archive").join("old");
        std::fs::create_dir_all(&archived).unwrap();
        std::fs::write(
            archived.join("status.json"),
            r#"{"jobId":"old","provider":"codex","sessionId":"sa","status":"running"}"#,
        )
        .unwrap();
        let torn = root.path().join("jobs").join("torn");
        std::fs::create_dir_all(&torn).unwrap();
        std::fs::write(torn.join("status.json"), r#"{"jobId":"torn","provider":"#).unwrap();
        write_job(
            root.path(),
            "ok",
            r#"{"jobId":"ok","provider":"codex","sessionId":"sok","callerLabel":"doc1",
                "status":"succeeded","finishedAt":"2026-08-15T11:59:00Z"}"#,
            None,
        );
        let payload = snapshot_at(Some(root.path()), at(NOW));
        assert_eq!(payload.rows.len(), 1);
        assert_eq!(payload.diagnostics, vec!["invalid dispatch status skipped"]);
        let serialized = serde_json::to_string(&payload).unwrap();
        assert!(!serialized.contains("\"sa\""));
    }

    #[test]
    fn row_cap_marks_truncation_deterministically() {
        let root = tempfile::tempdir().unwrap();
        for index in 0..(MAX_ROWS + 5) {
            write_job(
                root.path(),
                &format!("j{index}"),
                &format!(
                    r#"{{"jobId":"j{index}","provider":"codex","sessionId":"s{index}",
                        "callerLabel":"caller-{index:03}","status":"succeeded",
                        "finishedAt":"2026-08-15T11:30:00Z"}}"#
                ),
                None,
            );
        }
        let payload = snapshot_at(Some(root.path()), at(NOW));
        assert_eq!(payload.rows.len(), MAX_ROWS);
        assert!(payload.truncated);
    }
}
