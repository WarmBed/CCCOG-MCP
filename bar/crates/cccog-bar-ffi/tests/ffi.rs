use cccog_bar_core::graph::{build_snapshot, DispatchRecord, GraphInput};
use cccog_bar_ffi::{
    enrich_quota_cards, enrich_quota_cards_persistent, envelope_from_json, envelope_from_parts,
    postprocess_http_provider, precheck_http_provider, render_with_cache_fallback,
    BackgroundQuotaPoller, PersistedLimitEvidence, QuotaResilienceCache, CCCOG_BAR_SCHEMA_VERSION,
    CLAUDE_GROK_TTL_SECS, MANUAL_REFRESH_MIN_INTERVAL_SECS,
};
use cccog_bar_quota::{parse_reset_instant, QuotaCards, QuotaState, QuotaWindow};
use chrono::{DateTime, Local, TimeZone, Utc};
use serde_json::Value;
use std::collections::HashMap;
use std::ffi::{CStr, CString};

/// Same string `normalize_reset_display` (cccog-bar-quota) would produce for
/// a UTC instant — deterministic on any machine since both sides run the
/// same `Local` conversion, never a hardcoded offset. Bare `MM/DD HH:MM`,
/// no leading word (operator direction: bar/SYNC.md).
fn expected_reset_display(utc: DateTime<Utc>) -> String {
    utc.with_timezone(&Local).format("%m/%d %H:%M").to_string()
}

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
            diagnostic_detail: None,
            retry_after_seconds: None,
        },
        QuotaCards {
            client_id: "claude".to_owned(),
            windows: vec![],
            state: QuotaState::Stale,
            observed_at: None,
            diagnostic: Some("HTTP 401".to_owned()),
            diagnostic_detail: None,
            retry_after_seconds: None,
        },
        QuotaCards {
            client_id: "grok".to_owned(),
            windows: vec![],
            state: QuotaState::Unavailable,
            observed_at: None,
            diagnostic: Some("Grok auth credential not found".to_owned()),
            diagnostic_detail: None,
            retry_after_seconds: None,
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
fn quota_windows_carry_a_composed_display_line() {
    let snapshot = synthetic_snapshot();
    let cards = vec![QuotaCards {
        client_id: "codex".to_owned(),
        windows: vec![QuotaWindow {
            card_id: "primary".to_owned(),
            label: "5h".to_owned(),
            used_percent: 7.0,
            remaining_percent: 93.0,
            resets_at: Some("08/20 11:50".to_owned()),
        }],
        state: QuotaState::Fresh,
        observed_at: None,
        diagnostic: None,
        diagnostic_detail: None,
        retry_after_seconds: None,
    }];
    let output = envelope_from_parts(&snapshot, &cards, &[]);
    let value: Value = serde_json::from_str(&output).expect("envelope JSON");
    assert_eq!(
        value["quotaCards"][0]["windows"][0]["displayLine"],
        "5h  7%  08/20 11:50",
        "displayLine composes label/percent/date in the operator's locked order (bar/SYNC.md), not just passing usedPercent/resetsAt through separately"
    );
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
        diagnostic_detail: None,
        retry_after_seconds: None,
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

/// Same as `write_failed_job`, plus a `stdout.log` — for the case where
/// `status.json`'s own `error` is a generic wrapper message and the real
/// quota rejection only reached the provider CLI's process output.
fn write_failed_job_with_stdout(
    root: &std::path::Path,
    id: &str,
    provider: &str,
    finished_at: &str,
    status_error: &str,
    stdout: &str,
) {
    write_failed_job(root, id, provider, finished_at, status_error);
    std::fs::write(root.join(id).join("stdout.log"), stdout).unwrap();
}

/// Real message, byte-for-byte from job 20260815T141347Z_ce298f32's
/// stdout.log tail (verified on the operator's machine) — see bar/SYNC.md.
const REAL_CODEX_STDOUT_USAGE_LIMIT: &str = "{\"type\":\"thread.started\",\"thread_id\":\"019fe5e1-ce57-7b83-9235-6ace526d929d\"}\n\
{\"type\":\"turn.started\"}\n\
{\"type\":\"error\",\"message\":\"You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at Aug 20th, 2026 11:50 AM.\"}\n\
{\"type\":\"turn.failed\",\"error\":{\"message\":\"You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at Aug 20th, 2026 11:50 AM.\"}}\n";

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
    // Task 11 (2026-08-17, operator direction: "same one-row treatment... so
    // BOTH paths render identically" as the persisted override): no second
    // status line under the window row — diagnostic is None, state is
    // Fresh (this IS the app's current belief, not stale fallback data).
    // The dispatch-failure time and "reached" prose live in
    // diagnostic_detail for the hover tooltip only.
    let want_reset = expected_reset_display(Utc.with_ymd_and_hms(2026, 8, 20, 0, 0, 0).unwrap());
    assert!(card.diagnostic.is_none(), "no second status line on the scan-path override either");
    assert_eq!(card.state, QuotaState::Fresh);
    assert_eq!(
        card.diagnostic_detail.as_deref(),
        Some(format!("limit reached (dispatch failure 09:30) · {want_reset}").as_str())
    );
}

#[test]
fn job_failure_override_scans_stdout_tail_when_status_error_is_generic() {
    // The real-world case: status.json's own error is the wrapper's
    // generic "exited with code 1", not the provider's actual message —
    // the override must still fire from the stdout.log tail.
    let root = tempfile::tempdir().unwrap();
    // Same job id/content as the real fixture; the timestamp is shifted
    // earlier (still within the 24h lookback) so it precedes this test's
    // fixed NOW_SECONDS (2026-08-15T12:00:00Z) — the operator's own machine
    // observed the real finishedAt (14:13:56Z) after that instant, which a
    // fixed "now" in a test can't be.
    write_failed_job_with_stdout(
        root.path(),
        "20260815T141347Z_ce298f32",
        "codex",
        "2026-08-15T09:13:56Z",
        "Provider exited with code 1.",
        REAL_CODEX_STDOUT_USAGE_LIMIT,
    );
    let cards = enrich_quota_cards(vec![codex_card(None)], Some(root.path()), NOW_SECONDS);
    let card = &cards[0];
    assert_eq!(card.windows[0].used_percent, 100.0, "red 100% bar");
    // "Aug 20th, 2026 11:50 AM" is Codex CLI's own LOCAL-time string (task 6
    // addendum, 2026-08-16/17) — format-only, never shifted by the test
    // machine's UTC offset, so the expected text is the literal source
    // numbers, not `expected_reset_display`'s UTC->Local conversion (that
    // helper is still correct for every OTHER reset source in this file,
    // which do carry an explicit/unambiguous UTC instant).
    assert!(card.diagnostic.is_none(), "no second status line on the scan-path override either");
    assert_eq!(card.state, QuotaState::Fresh);
    assert_eq!(
        card.diagnostic_detail.as_deref(),
        Some("limit reached (dispatch failure 09:13) · 08/20 11:50")
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
        diagnostic_detail: None,
        retry_after_seconds: None,
    };
    let overridden = enrich_quota_cards(vec![grok_card.clone()], Some(root.path()), NOW_SECONDS);
    assert_eq!(overridden[0].windows[0].used_percent, 100.0);
    // No date-shaped text in "HTTP 402 Payment Required" -> honest "unknown"
    // in the window row's own resetsAt, never a guess. Task 11: no second
    // status line either way — the dispatch-failure time lives only in
    // diagnostic_detail for the hover tooltip.
    assert!(overridden[0].diagnostic.is_none(), "no second status line on the scan-path override either");
    assert_eq!(overridden[0].state, QuotaState::Fresh);
    assert!(overridden[0]
        .diagnostic_detail
        .as_deref()
        .unwrap()
        .contains("10:00"));

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
        diagnostic_detail: None,
        retry_after_seconds: None,
    };
    let result = enrich_quota_cards(vec![claude_card], Some(root.path()), NOW_SECONDS);
    assert_eq!(result[0].windows[0].used_percent, 6.0, "claude is out of scope for this cross-check");
}

// ── Task 6: persist-until-T quota-limit evidence ────────────────────────
// Live-confirmed bug: the Codex card fell back from "Limit 100% ·
// 08/20 19:50" to a stale, 22h-old rollout snapshot overnight purely
// because the evidence job aged out of the scan window/lookback — read by
// the operator as "quota reset" when it hadn't. `enrich_quota_cards_persistent`
// is the fix: once evidence with a parsed reset instant T is established,
// the limit persists until `now >= T`, independent of whether a fresh scan
// still finds the originating job.

/// `reset_at_english` goes through the SAME `parse_reset_instant` production
/// code parses a Codex evidence string with — not a hand-built RFC3339
/// string — so this fixture exercises the real local-time interpretation
/// (task 6 addendum) instead of accidentally asserting UTC-instant
/// semantics that don't apply to this shape.
fn persistent_codex_evidence(reset_at_english: &str, observed_at_rfc3339: &str) -> PersistedLimitEvidence {
    PersistedLimitEvidence {
        provider: "codex".to_owned(),
        reset_at: parse_reset_instant(reset_at_english).expect("valid English reset text"),
        evidence_job_id: "20260816T162448Z_07d41b5f".to_owned(),
        observed_at: observed_at_rfc3339.parse().unwrap(),
    }
}

#[test]
fn evidence_ages_out_of_the_scan_window_but_t_is_still_future_so_the_limit_persists() {
    // Empty dispatch root: nothing for `find_quota_limit_failure` to find
    // this round — simulates the job's status.json having aged out of the
    // 24h lookback, or been archived/rotated away entirely, while the
    // evidence is pre-seeded as already-established (a previous round DID
    // see it).
    let root = tempfile::tempdir().unwrap();
    let mut limit_evidence = HashMap::new();
    // NOW_SECONDS is 2026-08-15T12:00:00Z; reset_at is nearly a week later
    // and observed_at is well over an hour before NOW -- old evidence is
    // exactly the case this test exists to cover.
    limit_evidence.insert("codex".to_owned(), persistent_codex_evidence("Aug 20th, 2026 11:50 AM", "2026-08-15T08:00:00Z"));

    let cards = enrich_quota_cards_persistent(vec![codex_card(Some((NOW_SECONDS - 22 * 3600) * 1000))], Some(root.path()), NOW_SECONDS, &mut limit_evidence);

    let card = &cards[0];
    assert_eq!(card.windows[0].used_percent, 100.0, "the persisted evidence must still override, not the 22h-old rollout snapshot");
    assert_eq!(card.windows[0].resets_at.as_deref(), Some("08/20 11:50"));
    // Task 9 (operator feedback): the persisted-limit card shows ONLY the
    // window row + gauge -- no second status line, regardless of how old
    // the evidence is. No "as of X ago" marker; the lock's own validity has
    // nothing to do with how recently the app re-observed it.
    assert!(card.diagnostic.is_none(), "no second status line on a persisted-limit card, however old the evidence");
    assert_eq!(card.state, QuotaState::Fresh, "a persisted lock is the app's own current belief, not stale fallback data");
    // The full detail (when the failure was observed, exact reset) survives
    // for the hover tooltip.
    assert_eq!(card.diagnostic_detail.as_deref(), Some("limit reached (dispatch failure 08:00) · 08/20 11:50"));
    assert!(limit_evidence.contains_key("codex"), "evidence must remain persisted -- T hasn't passed");
}

#[test]
fn once_now_passes_t_the_limit_is_dropped_and_normal_sources_show_through() {
    let root = tempfile::tempdir().unwrap();
    let mut limit_evidence = HashMap::new();
    // reset_at is BEFORE NOW_SECONDS -- T has already passed.
    limit_evidence.insert("codex".to_owned(), persistent_codex_evidence("2026-08-15T09:00:00Z", "2026-08-14T20:00:00Z"));

    let normal_card = codex_card(Some((NOW_SECONDS - 300) * 1000));
    let cards = enrich_quota_cards_persistent(vec![normal_card], Some(root.path()), NOW_SECONDS, &mut limit_evidence);

    let card = &cards[0];
    assert_eq!(card.windows[0].used_percent, 37.0, "normal rollout data must show through once T has passed");
    assert!(card.diagnostic.is_none(), "no limit diagnostic once expired");
    assert!(!limit_evidence.contains_key("codex"), "expired evidence must be dropped from the cache");
}

#[test]
fn a_newer_non_limit_snapshot_supersedes_and_clears_the_persisted_limit() {
    // No fresh failure evidence this round (empty dispatch root) -- but the
    // incoming card's own observed_at is NEWER than the persisted
    // evidence's observed_at, meaning a real, more recent successful read
    // happened since the block was recorded (e.g. the provider-side block
    // was lifted early, or the persisted state was simply wrong).
    let root = tempfile::tempdir().unwrap();
    let mut limit_evidence = HashMap::new();
    limit_evidence.insert("codex".to_owned(), persistent_codex_evidence("Aug 20th, 2026 11:50 AM", "2026-08-15T08:00:00Z"));

    let fresh_card = codex_card(Some((NOW_SECONDS - 60) * 1000)); // observed 1 minute ago -- newer than the evidence
    let cards = enrich_quota_cards_persistent(vec![fresh_card], Some(root.path()), NOW_SECONDS, &mut limit_evidence);

    let card = &cards[0];
    assert_eq!(card.windows[0].used_percent, 37.0, "the newer successful snapshot wins over the stale limit lock");
    assert!(card.diagnostic.is_none());
    assert!(!limit_evidence.contains_key("codex"), "a newer good observation must clear the persisted limit state");
}

#[test]
fn a_fresh_scan_hit_refreshes_the_persisted_evidence_with_no_status_line() {
    // The evidence's own observed_at (the failed job's finished_at) is
    // recent -- this round's scan finds it directly. Same one-row rendering
    // as the aged-out case above (task 8): no second status line either way.
    let root = tempfile::tempdir().unwrap();
    write_failed_job(
        root.path(),
        "fresh1",
        "codex",
        "2026-08-15T11:30:00Z",
        "You've hit your usage limit. try again at Aug 20th, 2026 11:50 AM.",
    );
    let mut limit_evidence = HashMap::new();

    let cards = enrich_quota_cards_persistent(vec![codex_card(None)], Some(root.path()), NOW_SECONDS, &mut limit_evidence);

    let card = &cards[0];
    assert_eq!(card.windows[0].used_percent, 100.0);
    assert_eq!(card.windows[0].resets_at.as_deref(), Some("08/20 11:50"));
    assert!(card.diagnostic.is_none(), "no second status line on a freshly-reconfirmed round either");
    assert_eq!(card.state, QuotaState::Fresh);
    let stored = limit_evidence.get("codex").expect("a fresh hit with a parseable reset must be persisted");
    assert_eq!(stored.evidence_job_id, "fresh1");
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

// ── HTTP quota resilience: TTL cache, stale-on-failure fallback, backoff ──

fn fresh_claude_card() -> QuotaCards {
    QuotaCards {
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
        diagnostic_detail: None,
        retry_after_seconds: None,
    }
}

fn failed_429_card(retry_after_seconds: Option<u64>) -> QuotaCards {
    QuotaCards {
        client_id: "claude".to_owned(),
        windows: vec![],
        state: QuotaState::Stale,
        observed_at: None,
        diagnostic: Some("HTTP 429".to_owned()),
        diagnostic_detail: None,
        retry_after_seconds,
    }
}

#[test]
fn cached_fallback_render_keeps_bars_and_labels_stale_with_the_cached_time() {
    // Exactly the operator's screenshot: a 429 must not wipe the five-hour/
    // seven-day bars — the last successful card renders unchanged, relabeled.
    let cache = QuotaResilienceCache::empty().with_success("claude", fresh_claude_card(), 1786795200);
    let rendered = render_with_cache_fallback("claude", "HTTP 429", &cache, 1786795320);
    assert_eq!(rendered.state, QuotaState::Stale);
    assert_eq!(rendered.windows.len(), 1, "bars must survive the failure");
    assert_eq!(rendered.windows[0].used_percent, 6.0, "the cached value, unchanged");
    // Condensed on-card line (operator direction, bar/SYNC.md: "at most ONE
    // short status line" — no "data from" prose); the full sentence
    // survives in diagnostic_detail for a hover tooltip.
    assert_eq!(rendered.diagnostic.as_deref(), Some("stale · HTTP 429 · 12:00"));
    assert_eq!(
        rendered.diagnostic_detail.as_deref(),
        Some("stale · HTTP 429 · data from 12:00")
    );
}

#[test]
fn never_succeeded_case_renders_the_bare_failure_not_a_fabricated_cache_hit() {
    let cache = QuotaResilienceCache::empty(); // no prior success recorded
    let rendered = render_with_cache_fallback("claude", "HTTP 429", &cache, 1786795200);
    assert_eq!(rendered.state, QuotaState::Unavailable);
    assert!(rendered.windows.is_empty());
    assert_eq!(rendered.diagnostic.as_deref(), Some("HTTP 429"));
}

#[test]
fn postprocess_success_populates_the_cache_and_clears_any_backoff() {
    let mut cache = QuotaResilienceCache::empty().with_backoff("claude", 1786795500, "HTTP 429");
    let rendered = postprocess_http_provider("claude", fresh_claude_card(), &mut cache, 1786795200);
    assert_eq!(rendered.state, QuotaState::Fresh);
    assert!(!cache.is_backed_off("claude", 1786795200), "success clears the backoff");
    // The cache now has a success to fall back to on a later failure.
    let later = render_with_cache_fallback("claude", "timeout", &cache, 1786795500);
    assert_eq!(later.windows.len(), 1);
}

#[test]
fn postprocess_429_schedules_backoff_honoring_retry_after_else_five_minutes() {
    let mut with_header = QuotaResilienceCache::empty();
    postprocess_http_provider("claude", failed_429_card(Some(120)), &mut with_header, 1786795200);
    assert!(with_header.is_backed_off("claude", 1786795200 + 119));
    assert!(!with_header.is_backed_off("claude", 1786795200 + 121), "honors the header's 120s, not the 300s default");

    let mut without_header = QuotaResilienceCache::empty();
    postprocess_http_provider("claude", failed_429_card(None), &mut without_header, 1786795200);
    assert!(without_header.is_backed_off("claude", 1786795200 + 299));
    assert!(!without_header.is_backed_off("claude", 1786795200 + 301), "defaults to 5 minutes");
}

#[test]
fn postprocess_non_429_failure_never_schedules_a_backoff() {
    let mut cache = QuotaResilienceCache::empty();
    let timeout_card = QuotaCards {
        diagnostic: Some("quota request failed".to_owned()),
        ..failed_429_card(None)
    };
    postprocess_http_provider("claude", timeout_card, &mut cache, 1786795200);
    assert!(!cache.is_backed_off("claude", 1786795200), "a timeout isn't a rate-limit signal");
}

#[test]
fn precheck_backoff_wins_over_the_ttl_and_renders_the_stale_fallback() {
    let cache = QuotaResilienceCache::empty()
        .with_success("claude", fresh_claude_card(), 1786794000)
        .with_backoff("claude", 1786795500, "HTTP 429")
        .with_last_attempt("claude", 1786795000);
    let skip = precheck_http_provider("claude", false, &cache, 1786795100);
    let card = skip.expect("an active backoff must skip the network");
    assert_eq!(card.state, QuotaState::Stale);
    assert_eq!(card.windows.len(), 1, "still shows the cached bars while backed off");
}

#[test]
fn precheck_auto_tick_honors_the_300s_ttl_manual_bypasses_but_is_capped_at_60s() {
    let cache = QuotaResilienceCache::empty()
        .with_success("claude", fresh_claude_card(), 1786795000)
        .with_last_attempt("claude", 1786795000);

    // Automatic tick well inside the 300s TTL: served from cache, no fetch.
    let auto_hit = precheck_http_provider("claude", false, &cache, 1786795000 + 100);
    assert!(auto_hit.is_some(), "inside TTL: cache hit, no network");
    assert_eq!(auto_hit.unwrap().state, QuotaState::Fresh, "a TTL hit is not a stale render");

    // Automatic tick at/after the 300s TTL: must fetch again.
    let auto_expired = precheck_http_provider("claude", false, &cache, 1786795000 + CLAUDE_GROK_TTL_SECS);
    assert!(auto_expired.is_none(), "TTL expired: go fetch");

    // Manual click 30s after the last attempt: still rate-limited to 60s.
    let manual_too_soon = precheck_http_provider("claude", true, &cache, 1786795000 + 30);
    assert!(manual_too_soon.is_some(), "manual bypass is capped at 60s, not instant");

    // Manual click 60s after the last attempt: allowed through, even though
    // the 300s auto TTL hasn't expired.
    let manual_allowed = precheck_http_provider("claude", true, &cache, 1786795000 + MANUAL_REFRESH_MIN_INTERVAL_SECS);
    assert!(manual_allowed.is_none(), "manual bypass reaches the network at 60s");
}

#[test]
fn precheck_never_attempted_always_goes_to_the_network() {
    let cache = QuotaResilienceCache::empty();
    assert!(precheck_http_provider("claude", false, &cache, 1786795200).is_none());
    assert!(precheck_http_provider("claude", true, &cache, 1786795200).is_none());
}
