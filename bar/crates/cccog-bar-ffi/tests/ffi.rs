use cccog_bar_core::graph::{build_snapshot, DispatchRecord, GraphInput};
use cccog_bar_ffi::{envelope_from_json, envelope_from_parts, BackgroundQuotaPoller, CCCOG_BAR_SCHEMA_VERSION};
use cccog_bar_quota::{QuotaCards, QuotaState};
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
