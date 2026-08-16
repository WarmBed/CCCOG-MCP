//! Presence heartbeat files: the "A/B layer" liveness signal that sits above
//! the C-layer transcript-event-window scan (`claude_sessions.rs`).
//!
//! `hooks/cccg-presence-hook.js` (registered by the operator, not by this
//! app) writes/updates one JSON file per Claude Code session at
//! `%LOCALAPPDATA%\CCCG\presence\<session-id>.json` on every hook
//! invocation: `{"sessionId":..., "cwd":..., "event":..., "ts":...}`, where
//! `event` is the Claude Code hook event name (`SessionStart`,
//! `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `Stop`) and `ts` is a
//! Unix-epoch-milliseconds timestamp (`Date.now()` — the hook runs under
//! Node, matching `hooks/cccg-state-hook.js`'s own JS runtime).
//!
//! This module is pure parsing + a pure state-resolution function
//! (unit-tested directly with fixture strings/records — no filesystem
//! needed for the interesting logic), plus a thin FS-scanning layer at the
//! bottom that reads the presence directory and prunes files that are
//! ancient enough (48h) to be leftover litter from a session that will
//! never heartbeat again.

use serde::Deserialize;
use std::collections::HashMap;
use std::path::Path;

/// An activity event fresher than this reads as "still actively working on
/// this turn" (執行中). Intentionally the same value as
/// `claude_sessions::RUNNING_FRESH_SECS` so the C-layer and this A/B layer
/// never disagree about what "fresh" means.
pub const PRESENCE_RUNNING_FRESH_SECS: i64 = 60;

/// Beyond this age a presence record can't say anything useful either way —
/// the bar falls back to the C-layer transcript scan's own judgment rather
/// than asserting a stale presence state. Matches
/// `claude_sessions::CLAUDE_ALIVE_WINDOW_SECS`.
pub const PRESENCE_MAX_AGE_SECS: i64 = 600;

/// Presence files older than this are pruned on scan — pure litter from a
/// session that closed and will never write to its file again.
const PRUNE_AGE_MS: i64 = 48 * 3600 * 1000;

#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
pub struct PresenceRecord {
    #[serde(rename = "sessionId")]
    pub session_id: String,
    #[serde(default)]
    pub cwd: Option<String>,
    pub event: String,
    /// Unix epoch milliseconds — matches JS `Date.now()`, what the hook
    /// script actually writes.
    pub ts: i64,
}

pub fn parse_presence(json: &str) -> Option<PresenceRecord> {
    serde_json::from_str(json).ok()
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PresenceState {
    /// A submit/tool-use heartbeat fresher than [`PRESENCE_RUNNING_FRESH_SECS`]
    /// with no later `Stop`.
    Running,
    /// A heartbeat exists (engine alive) but is stale, or the latest event
    /// is `Stop` (turn ended) — open, no recent activity.
    Idle,
    /// No usable signal (missing, or too old to trust either way) — the
    /// caller falls back to the C-layer transcript-window judgment.
    Unknown,
}

/// Resolve the tri-state per the operator's rule: "Submit/PreToolUse/
/// PostToolUse heartbeat fresher than 60s with no later Stop = 執行中;
/// heartbeat exists + engine alive but stale = 閒置; no/ancient heartbeat =
/// fall back to C-layer judgment." `Stop` itself always marks turn end
/// (idle), regardless of how fresh it is — a `Stop` event fresher than 60s
/// is still "no longer running a turn", not "still running".
pub fn resolve_presence_state(record: &PresenceRecord, now_ms: i64) -> PresenceState {
    let age_secs = now_ms.saturating_sub(record.ts).max(0) / 1000;
    if age_secs > PRESENCE_MAX_AGE_SECS {
        return PresenceState::Unknown;
    }
    if record.event.eq_ignore_ascii_case("Stop") {
        return PresenceState::Idle;
    }
    let is_activity_event = matches!(
        record.event.as_str(),
        "UserPromptSubmit" | "PreToolUse" | "PostToolUse" | "SessionStart"
    );
    if is_activity_event && age_secs <= PRESENCE_RUNNING_FRESH_SECS {
        return PresenceState::Running;
    }
    PresenceState::Idle
}

/// Read every `*.json` file directly under `dir`, keyed by session id (the
/// record's own `sessionId` field is authoritative, not the filename, but
/// in practice the hook names the file after the session id too). Bad/torn
/// files are silently skipped — presence is a best-effort accelerant, never
/// a hard dependency.
pub fn collect_presence(dir: &Path) -> HashMap<String, PresenceRecord> {
    let mut out = HashMap::new();
    let Ok(entries) = std::fs::read_dir(dir) else {
        return out;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.extension().and_then(|extension| extension.to_str()) != Some("json") {
            continue;
        }
        let Ok(raw) = std::fs::read_to_string(&path) else {
            continue;
        };
        let Some(record) = parse_presence(&raw) else {
            continue;
        };
        out.insert(record.session_id.clone(), record);
    }
    out
}

/// Best-effort delete of presence files whose mtime is older than
/// [`PRUNE_AGE_MS`]. Never panics or propagates an error — a prune failure
/// (permissions, file in use) just leaves the litter for next time.
pub fn prune_stale_presence_files(dir: &Path, now_ms: i64) {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.extension().and_then(|extension| extension.to_str()) != Some("json") {
            continue;
        }
        let Ok(metadata) = std::fs::metadata(&path) else {
            continue;
        };
        let Ok(modified) = metadata.modified() else {
            continue;
        };
        let mtime_ms = modified
            .duration_since(std::time::UNIX_EPOCH)
            .map(|duration| duration.as_millis() as i64)
            .unwrap_or(0);
        if now_ms.saturating_sub(mtime_ms) > PRUNE_AGE_MS {
            let _ = std::fs::remove_file(&path);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn record(event: &str, ts: i64) -> PresenceRecord {
        PresenceRecord {
            session_id: "s1".to_owned(),
            cwd: Some("D:\\code\\CCCG".to_owned()),
            event: event.to_owned(),
            ts,
        }
    }

    #[test]
    fn parse_presence_reads_the_expected_shape() {
        let record = parse_presence(
            r#"{"sessionId":"s1","cwd":"D:\\code\\CCCG","event":"PostToolUse","ts":1700000000000}"#,
        )
        .unwrap();
        assert_eq!(record.session_id, "s1");
        assert_eq!(record.event, "PostToolUse");
        assert_eq!(record.ts, 1700000000000);
    }

    #[test]
    fn parse_presence_rejects_malformed_json() {
        assert!(parse_presence("{not json").is_none());
        assert!(parse_presence("").is_none());
    }

    #[test]
    fn fresh_submit_with_no_later_stop_is_running() {
        let now_ms = 1_700_000_060_000; // 60s after ts below
        let r = record("UserPromptSubmit", 1_700_000_000_000);
        assert_eq!(resolve_presence_state(&r, now_ms - 1), PresenceState::Running);
    }

    #[test]
    fn stale_submit_past_60s_is_idle() {
        let ts = 1_700_000_000_000;
        let now_ms = ts + 120_000; // 2 minutes later
        let r = record("PreToolUse", ts);
        assert_eq!(resolve_presence_state(&r, now_ms), PresenceState::Idle);
    }

    #[test]
    fn stop_event_is_always_idle_even_if_fresh() {
        let ts = 1_700_000_000_000;
        let r = record("Stop", ts);
        assert_eq!(resolve_presence_state(&r, ts + 5_000), PresenceState::Idle);
    }

    #[test]
    fn ancient_record_falls_back_to_unknown() {
        let ts = 1_700_000_000_000;
        let now_ms = ts + (PRESENCE_MAX_AGE_SECS + 1) * 1000;
        let r = record("PostToolUse", ts);
        assert_eq!(resolve_presence_state(&r, now_ms), PresenceState::Unknown);
    }

    #[test]
    fn post_tool_use_exactly_at_the_fresh_boundary_is_running() {
        let ts = 1_700_000_000_000;
        let now_ms = ts + PRESENCE_RUNNING_FRESH_SECS * 1000;
        let r = record("PostToolUse", ts);
        assert_eq!(resolve_presence_state(&r, now_ms), PresenceState::Running);
    }

    #[test]
    fn collect_presence_reads_directory_and_skips_torn_files() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(
            dir.path().join("s1.json"),
            r#"{"sessionId":"s1","event":"PostToolUse","ts":1700000000000}"#,
        )
        .unwrap();
        std::fs::write(dir.path().join("torn.json"), "{not json").unwrap();
        std::fs::write(dir.path().join("ignored.txt"), "not a presence file").unwrap();

        let records = collect_presence(dir.path());
        assert_eq!(records.len(), 1);
        assert!(records.contains_key("s1"));
    }

    #[test]
    fn collect_presence_on_missing_directory_is_empty_not_panicking() {
        let dir = tempfile::tempdir().unwrap();
        let missing = dir.path().join("no-such-dir");
        assert!(collect_presence(&missing).is_empty());
    }

    #[test]
    fn prune_removes_only_files_older_than_48h() {
        let dir = tempfile::tempdir().unwrap();
        let fresh = dir.path().join("fresh.json");
        let old = dir.path().join("old.json");
        std::fs::write(&fresh, "{}").unwrap();
        std::fs::write(&old, "{}").unwrap();
        let ancient = std::time::SystemTime::now() - std::time::Duration::from_secs(72 * 3600);
        std::fs::OpenOptions::new()
            .write(true)
            .open(&old)
            .unwrap()
            .set_modified(ancient)
            .unwrap();

        let now_ms = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_millis() as i64;
        prune_stale_presence_files(dir.path(), now_ms);

        assert!(fresh.exists(), "a fresh presence file must survive pruning");
        assert!(!old.exists(), "a 72h-old presence file must be pruned");
    }
}
