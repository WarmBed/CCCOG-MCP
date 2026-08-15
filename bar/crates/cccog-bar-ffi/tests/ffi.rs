use cccog_bar_core::graph::{build_snapshot, DispatchRecord, GraphInput};
use cccog_bar_ffi::{
    enrich_quota_cards, envelope_from_json, envelope_from_parts, BackgroundQuotaPoller,
    CCCOG_BAR_SCHEMA_VERSION,
};
use cccog_bar_quota::{QuotaCards, QuotaState, QuotaWindow};
use serde_json::Value;
use std::ffi::{CStr, CString};

#[test]
fn snapshot_envelope_is_versioned_and_contains_all_sections() {
    let output = envelope_from_json(
        r#"{"snapshot":{"nodes":[],"edges":[]},"quotaCards":[],"diagnostics":["fixture"]}"#,
    );
    let value: Value = serde_json::from_str(&output).expect("JSON envelope");
    assert_eq!(value["schemaVersion"], CCCOG_BAR_SCHEMA_VERSION);
    assert_eq!(value["ok"], true);
    assert!(value["snapshot"].is_object());
    assert!(value["quotaCards"].is_array());
    assert_eq!(value["diagnostics"][0], "fixture");
}

#[test]
fn malformed_and_null_c_inputs_return_structured_errors() {
    let malformed = envelope_from_json("not-json");
    let value: Value = serde_json::from_str(&malformed).unwrap();
    assert_eq!(value["ok"], false);
    assert_eq!(value["error"]["code"], "invalid_input");

    let null = unsafe { cccog_bar_ffi::cccog_bar_snapshot_json(std::ptr::null()) };
    assert!(!null.is_null());
    let value: Value =
        serde_json::from_str(unsafe { CStr::from_ptr(null) }.to_str().unwrap()).unwrap();
    assert_eq!(value["ok"], false);
    unsafe { cccog_bar_ffi::cccog_bar_free_string(null) };
}

#[test]
fn repeated_calls_are_freeable_without_retaining_process_state() {
    for _ in 0..128 {
        let input = CString::new(r#"{"snapshot":{},"quotaCards":[],"diagnostics":[]}"#).unwrap();
        let output = unsafe { cccog_bar_ffi::cccog_bar_snapshot_json(input.as_ptr()) };
        assert!(!output.is_null());
        let text = unsafe { CStr::from_ptr(output) }.to_str().unwrap();
        assert!(text.contains("schemaVersion"));
        unsafe { cccog_bar_ffi::cccog_bar_free_string(output) };
    }
}

#[test]
fn envelope_rejects_non_object_snapshot_with_bounded_error() {
    let output = envelope_from_json(r#"{"snapshot":[],"quotaCards":[],"diagnostics":[]}"#);
    let value: Value = serde_json::from_str(&output).unwrap();
    assert_eq!(value["ok"], false);
    assert_eq!(value["error"]["code"], "invalid_input");
    assert!(output.len() < 4096);
}

#[test]
fn background_quota_poller_has_a_hard_sixty_second_floor() {
    let mut poller = BackgroundQuotaPoller::new(1);
    assert!(poller.poll(0, None, None).is_some());
    assert!(poller.poll(59, None, None).is_none());
    assert!(poller.poll(60, None, None).is_some());
}

fn synthetic_snapshot() -> cccog_bar_core::graph::GraphSnapshot {
    build_snapshot(GraphInput {
        jobs: vec![DispatchRecord {
            job_id: "job-1".to_owned(),
            provider: "codex".to_owned(),
            session_id: Some("thread-1".to_owned()),
            requested_session_id: None,
            caller_label: Some("doc1".to_owned()),
            hop_source: None,
            hop_chain: None,
            status: "running".to_owned(),
            started_at: Some("2026-08-16T01:00:00Z".to_owned()),
            finished_at: None,
            task_summary: "synthetic combined-envelope fixture".to_owned(),
        }],
        owners: vec![],
        usage: vec![],
    })
}

fn synthetic_quota_cards() -> Vec<QuotaCards> {
    vec![
        QuotaCards {
            client_id: "codex".to_owned(),
            windows: vec![],
            state: QuotaState::Unavailable,
            observed_at: None,
            diagnostic: Some("Codex rollout rate_limits not found in bounded tails".to_owned()),
        },
        QuotaCards {
            client_id: "claude".to_owned(),
            windows: vec![],
            state: QuotaState::Stale,
            observed_at: None,
            diagnostic: Some("HTTP 401".to_owned()),
        },
        QuotaCards {
            client_id: "grok".to_owned(),
            windows: vec![],
            state: QuotaState::Unavailable,
            observed_at: None,
            diagnostic: Some("Grok auth credential not found".to_owned()),
        },
    ]
}

#[test]
fn combined_flow_and_quota_envelope_is_versioned_and_preserves_client_order() {
    let snapshot = synthetic_snapshot();
    let cards = synthetic_quota_cards();
    let diagnostics = vec!["fixture diagnostic".to_owned()];

    let output = envelope_from_parts(&snapshot, &cards, &diagnostics);
    let value: Value = serde_json::from_str(&output).expect("combined envelope JSON");
    assert_eq!(value["schemaVersion"], CCCOG_BAR_SCHEMA_VERSION);
    assert_eq!(value["ok"], true);
    assert!(value["snapshot"]["nodes"].is_array());
    assert!(value["snapshot"]["edges"].is_array());

    let quota_cards = value["quotaCards"].as_array().expect("quota cards array");
    assert_eq!(quota_cards.len(), 3);
    let ids: Vec<&str> = quota_cards
        .iter()
        .map(|card| card["clientId"].as_str().unwrap())
        .collect();
    // Ordering must be preserved exactly as the caller supplied it (poller
    // pushes codex, then claude, then grok) — never reshuffled by a hash map.
    assert_eq!(ids, vec!["codex", "claude", "grok"]);
    assert_eq!(quota_cards[1]["diagnostic"], "HTTP 401");
    assert_eq!(value["diagnostics"][0], "fixture diagnostic");
}

#[test]
fn combined_envelope_serialization_is_byte_for_byte_deterministic() {
    let snapshot = synthetic_snapshot();
    let cards = synthetic_quota_cards();
    let diagnostics = vec!["fixture diagnostic".to_owned()];

    let first = envelope_from_parts(&snapshot, &cards, &diagnostics);
    let second = envelope_from_parts(&snapshot, &cards, &diagnostics);
    assert_eq!(first, second);
}

const NOW_SECONDS: u64 = 1786795200; // 2026-08-15T12:00:00Z

fn codex_card(observed_at_ms: Option<u64>) -> QuotaCards {
    QuotaCards {
        client_id: "codex".to_owned(),
        windows: vec![QuotaWindow {
            card_id: "primary".to_owned(),
            label: "Primary".to_owned(),
            used_percent: 37.0,
            remaining_percent: 63.0,
            resets_at: Some("2026-08-20T11:50:00Z".to_owned()),
        }],
        state: QuotaState::Fresh,
        observed_at: observed_at_ms,
        diagnostic: None,
    }
}

fn write_failed_job(root: &std::path::Path, id: &str, provider: &str, finished_at: &str, error: &str) {
    let dir = root.join(id);
    std::fs::create_dir_all(&dir).unwrap();
    std::fs::write(
        dir.join("status.json"),
        format!(
            r#"{{"jobId":"{id}","provider":"{provider}","sessionId":"s-{id}",
                "status":"failed","finishedAt":"{finished_at}","error":"{error}"}}"#
        ),
    )
    .unwrap();
}

#[test]
fn stale_snapshot_past_one_hour_is_flagged_and_annotated_with_its_age() {
    let fresh_at = (NOW_SECONDS - 20 * 60) * 1_000; // 20 minutes old
    let fresh = enrich_quota_cards(vec![codex_card(Some(fresh_at))], None, NOW_SECONDS);
    assert_eq!(fresh[0].state, QuotaState::Fresh, "under an hour stays Fresh");
    assert!(fresh[0].diagnostic.is_none());

    let stale_at = (NOW_SECONDS - 18 * 3600) * 1_000; // 18 hours old
    let stale = enrich_quota_cards(vec![codex_card(Some(stale_at))], None, NOW_SECONDS);
    assert_eq!(stale[0].state, QuotaState::Stale);
    assert_eq!(stale[0].diagnostic.as_deref(), Some("as of 18h ago"));
    // The percentage itself is untouched — this is an honest age flag, not
    // an invented number.
    assert_eq!(stale[0].windows[0].used_percent, 37.0);
}

#[test]
fn job_failure_override_beats_the_stale_annotation_and_carries_the_failure_time() {
    let root = tempfile::tempdir().unwrap();
    write_failed_job(
        root.path(),
        "blocked",
        "codex",
        "2026-08-15T09:30:00Z",
        "usage limit reached; resets 2026-08-20T00:00:00Z",
    );
    let stale_at = (NOW_SECONDS - 18 * 3600) * 1_000;
    let cards = enrich_quota_cards(
        vec![codex_card(Some(stale_at))],
        Some(root.path()),
        NOW_SECONDS,
    );
    let card = &cards[0];
    assert_eq!(card.windows.len(), 1);
    assert_eq!(card.windows[0].used_percent, 100.0, "red 100% bar");
    assert_eq!(card.windows[0].remaining_percent, 0.0);
    assert_eq!(
        card.diagnostic.as_deref(),
        Some("limit reached (per dispatch failure at 09:30) · resets 2026-08-20T00:00:00Z")
    );
}

#[test]
fn job_failure_override_applies_to_grok_and_is_skipped_without_a_match() {
    let root = tempfile::tempdir().unwrap();
    write_failed_job(
        root.path(),
        "g1",
        "grok",
        "2026-08-15T10:00:00Z",
        "HTTP 402 Payment Required",
    );
    let grok_card = QuotaCards {
        client_id: "grok".to_owned(),
        windows: vec![QuotaWindow {
            card_id: "credits".to_owned(),
            label: "Credits".to_owned(),
            used_percent: 10.0,
            remaining_percent: 90.0,
            resets_at: None,
        }],
        state: QuotaState::Fresh,
        observed_at: None,
        diagnostic: None,
    };
    let overridden = enrich_quota_cards(vec![grok_card.clone()], Some(root.path()), NOW_SECONDS);
    assert_eq!(overridden[0].windows[0].used_percent, 100.0);
    assert!(overridden[0].diagnostic.as_deref().unwrap().contains("10:00"));

    // An unrelated failure (no quota-limit phrasing) must never trigger the
    // override — a real process crash is not a quota signal.
    let other_root = tempfile::tempdir().unwrap();
    write_failed_job(
        other_root.path(),
        "g2",
        "grok",
        "2026-08-15T10:00:00Z",
        "Provider exited with code 1.",
    );
    let untouched = enrich_quota_cards(
        vec![grok_card.clone()],
        Some(other_root.path()),
        NOW_SECONDS,
    );
    assert_eq!(untouched[0].windows[0].used_percent, 10.0);
}

#[test]
fn job_failure_override_is_scoped_to_codex_and_grok_only() {
    let root = tempfile::tempdir().unwrap();
    write_failed_job(
        root.path(),
        "c1",
        "claude",
        "2026-08-15T10:00:00Z",
        "usage limit reached",
    );
    let claude_card = QuotaCards {
        client_id: "claude".to_owned(),
        windows: vec![QuotaWindow {
            card_id: "five_hour".to_owned(),
            label: "Five hour".to_owned(),
            used_percent: 6.0,
            remaining_percent: 94.0,
            resets_at: None,
        }],
        state: QuotaState::Fresh,
        observed_at: None,
        diagnostic: None,
    };
    let result = enrich_quota_cards(vec![claude_card], Some(root.path()), NOW_SECONDS);
    assert_eq!(result[0].windows[0].used_percent, 6.0, "claude is out of scope for this cross-check");
}

#[test]
fn control_snapshot_json_is_versioned_and_unavailable_for_a_missing_root() {
    let output = cccog_bar_ffi::control_snapshot_json(
        r#"{"dispatchRoot":"D:\\no-such-cccg-dispatch-root","now":1755298800}"#,
    );
    let value: Value = serde_json::from_str(&output).expect("control envelope JSON");
    assert_eq!(value["schemaVersion"], CCCOG_BAR_SCHEMA_VERSION);
    assert_eq!(value["ok"], true);
    assert_eq!(value["control"]["available"], false);
    assert_eq!(value["control"]["rows"].as_array().unwrap().len(), 0);
}

#[test]
fn control_snapshot_json_windows_and_aggregates_a_real_dispatch_root() {
    let root = tempfile::tempdir().unwrap();
    let jobs = root.path().join("jobs");
    for (id, session, finished) in [
        ("j1", "s-shared", "2026-08-15T11:00:00Z"),
        ("j2", "s-shared", "2026-08-15T11:40:00Z"),
    ] {
        let dir = jobs.join(id);
        std::fs::create_dir_all(&dir).unwrap();
        std::fs::write(
            dir.join("status.json"),
            format!(
                r#"{{"jobId":"{id}","provider":"codex","sessionId":"{session}",
                    "callerLabel":"doc1","status":"succeeded","finishedAt":"{finished}"}}"#
            ),
        )
        .unwrap();
    }
    // Outside the 2h window — must not appear in the default view.
    let old = jobs.join("old");
    std::fs::create_dir_all(&old).unwrap();
    std::fs::write(
        old.join("status.json"),
        r#"{"jobId":"old","provider":"claude","sessionId":"s-old",
            "status":"succeeded","finishedAt":"2026-08-13T09:00:00Z"}"#,
    )
    .unwrap();

    let input = serde_json::json!({
        "dispatchRoot": root.path().to_string_lossy(),
        "now": 1786795200i64, // 2026-08-15T12:00:00Z
    });
    let output = cccog_bar_ffi::control_snapshot_json(&input.to_string());
    let value: Value = serde_json::from_str(&output).expect("control envelope JSON");
    assert_eq!(value["ok"], true);
    assert_eq!(value["control"]["available"], true);
    let rows = value["control"]["rows"].as_array().unwrap();
    assert_eq!(rows.len(), 1, "same-session terminal jobs aggregate, the stale job is windowed out");
    assert_eq!(rows[0]["count"], 2);
    assert!(rows[0]["target"].as_str().unwrap().contains("s-shared"));

    let null = unsafe { cccog_bar_ffi::cccog_bar_control_snapshot(std::ptr::null()) };
    let error: Value =
        serde_json::from_str(unsafe { CStr::from_ptr(null) }.to_str().unwrap()).unwrap();
    assert_eq!(error["ok"], false);
    unsafe { cccog_bar_ffi::cccog_bar_free_string(null) };
}
