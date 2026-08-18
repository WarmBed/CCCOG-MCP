use cccog_bar_quota::{
    fetch_claude_quota, fetch_grok_quota, load_claude_credential, load_claude_credential_with_store,
    load_codex_quota_from_sessions, load_grok_token, parse_codex_rate_limits, CredentialStore,
    HttpClient, HttpRequest, HttpResponse, OAuthCredential, PollGate, QuotaState,
};
use chrono::{Local, TimeZone, Utc};

#[derive(Default)]
struct FakeHttp {
    responses: Vec<Result<HttpResponse, String>>,
    requests: Vec<HttpRequest>,
}

impl FakeHttp {
    fn with(responses: Vec<HttpResponse>) -> Self {
        Self {
            responses: responses.into_iter().map(Ok).collect(),
            requests: Vec::new(),
        }
    }

    fn with_results(responses: Vec<Result<HttpResponse, String>>) -> Self {
        Self {
            responses,
            requests: Vec::new(),
        }
    }
}

impl HttpClient for FakeHttp {
    fn send(&mut self, request: HttpRequest) -> Result<HttpResponse, String> {
        self.requests.push(request);
        if self.responses.is_empty() {
            return Err("offline".to_owned());
        }
        self.responses.remove(0)
    }
}

#[test]
fn codex_local_rate_limits_map_windows_without_http() {
    let cards = parse_codex_rate_limits(
        r#"{"rate_limits":{"primary":{"used_percent":1.0,"window_minutes":300,"resets_at":"2026-08-15T05:00:00Z"},"secondary":{"used_percent":91,"window_minutes":10080,"resets_at":"2026-08-22T00:00:00Z"}}}"#,
    )
    .expect("valid synthetic rate limits");
    assert_eq!(cards.client_id, "codex");
    assert_eq!(cards.windows.len(), 2);
    assert_eq!(cards.windows[0].remaining_percent, 99.0);
    assert_eq!(cards.state, QuotaState::Fresh);
}

#[test]
fn codex_malformed_json_reports_a_concrete_reason() {
    let error = parse_codex_rate_limits("not-json").expect_err("malformed input must fail");
    assert_eq!(error, "invalid Codex rate_limits JSON");

    let empty = parse_codex_rate_limits("{}").expect_err("no windows must fail");
    assert_eq!(
        empty,
        "Codex response contained no usable rate limit windows"
    );
}

#[test]
fn claude_200_maps_cards_and_401_is_stale_with_concrete_http_reason() {
    let response = HttpResponse::json(
        200,
        r#"{"five_hour":{"utilization":1.0,"resets_at":"2026-08-15T05:00:00Z"},"seven_day":{"utilization":91.0}}"#,
    );
    let mut http = FakeHttp::with(vec![response]);
    let credential = OAuthCredential::new("access-token", None, None);
    let cards = fetch_claude_quota(&mut http, &credential, 1_000).expect("cards");
    assert_eq!(cards.windows.len(), 2);
    assert_eq!(cards.windows[0].used_percent, 1.0);
    assert_eq!(cards.windows[1].remaining_percent, 9.0);
    assert!(!http.requests[0].url.contains("prompt"));
    assert!(!http.requests[0].url.contains("session"));

    let mut unauthorized = FakeHttp::with(vec![HttpResponse::json(401, "{}")]);
    let stale = fetch_claude_quota(&mut unauthorized, &credential, 1_000).expect("stale cards");
    assert_eq!(stale.state, QuotaState::Stale);
    // Lessons-learned: failures must carry a concrete reason, never a vague
    // "unavailable" placeholder.
    assert_eq!(stale.diagnostic.as_deref(), Some("HTTP 401"));
}

#[test]
fn claude_403_is_unavailable_without_messages_probe_and_expired_refresh_is_post_then_get() {
    let mut forbidden = FakeHttp::with(vec![HttpResponse::json(403, "{}")]);
    let credential = OAuthCredential::new("inference-only", Some("refresh"), Some(2_000));
    let unavailable = fetch_claude_quota(&mut forbidden, &credential, 1_000).expect("card state");
    assert_eq!(unavailable.state, QuotaState::Unavailable);
    assert_eq!(forbidden.requests.len(), 1);
    assert!(!forbidden
        .requests
        .iter()
        .any(|request| request.url.contains("messages")));

    let expired = OAuthCredential::new("expired", Some("refresh"), Some(1));
    let mut refresh = FakeHttp::with(vec![
        HttpResponse::json(200, r#"{"access_token":"refreshed","expires_in":3600}"#),
        HttpResponse::json(200, r#"{"five_hour":{"utilization":20}}"#),
    ]);
    let fresh = fetch_claude_quota(&mut refresh, &expired, 1_000).expect("refreshed cards");
    assert_eq!(fresh.state, QuotaState::Fresh);
    assert_eq!(refresh.requests[0].method, "POST");
    assert_eq!(refresh.requests[0].url, "https://platform.claude.com/v1/oauth/token");
    // The refresh body must carry the same client_id TokenBar-Windows sends;
    // some deployments of the endpoint reject a refresh without it.
    let refresh_body = refresh.requests[0].body.as_deref().unwrap_or_default();
    assert!(refresh_body.contains("grant_type=refresh_token"));
    assert!(refresh_body.contains("refresh_token=refresh"));
    assert!(refresh_body.contains("client_id=9d1c250a-e61b-44d9-88ed-5944d1962f5e"));
    assert_eq!(refresh.requests[1].method, "GET");
    assert_eq!(refresh.requests[1].bearer.as_deref(), Some("refreshed"));
}

#[test]
fn claude_refresh_rejected_and_transport_failure_report_exact_diagnostics() {
    let expired = OAuthCredential::new("expired", Some("refresh"), Some(1));

    let mut rejected = FakeHttp::with(vec![HttpResponse::json(400, r#"{"error":"invalid_grant"}"#)]);
    let stale = fetch_claude_quota(&mut rejected, &expired, 1_000).expect("stale cards");
    assert_eq!(stale.state, QuotaState::Stale);
    assert_eq!(stale.diagnostic.as_deref(), Some("token refresh rejected"));

    let mut offline = FakeHttp::with_results(vec![Err("connection reset".to_owned())]);
    let failed = fetch_claude_quota(&mut offline, &expired, 1_000).expect("stale cards");
    assert_eq!(failed.state, QuotaState::Stale);
    assert_eq!(failed.diagnostic.as_deref(), Some("token refresh failed"));

    let mut malformed_refresh = FakeHttp::with(vec![HttpResponse::json(200, "not-json")]);
    let malformed = fetch_claude_quota(&mut malformed_refresh, &expired, 1_000).expect("stale cards");
    assert_eq!(malformed.diagnostic.as_deref(), Some("token refresh response malformed"));

    let mut no_token = FakeHttp::with(vec![HttpResponse::json(200, r#"{"expires_in":3600}"#)]);
    let missing_token = fetch_claude_quota(&mut no_token, &expired, 1_000).expect("stale cards");
    assert_eq!(
        missing_token.diagnostic.as_deref(),
        Some("token refresh returned no access token")
    );
}

#[test]
fn claude_transport_failure_and_malformed_body_after_success_are_stale() {
    let credential = OAuthCredential::new("access-token", None, None);

    let mut offline = FakeHttp::with_results(vec![Err("timed out".to_owned())]);
    let stale = fetch_claude_quota(&mut offline, &credential, 1_000).expect("stale cards");
    assert_eq!(stale.state, QuotaState::Stale);
    assert_eq!(stale.diagnostic.as_deref(), Some("quota request failed"));

    let mut malformed = FakeHttp::with(vec![HttpResponse::json(200, "not-json")]);
    let malformed_cards = fetch_claude_quota(&mut malformed, &credential, 1_000).expect("stale cards");
    assert_eq!(malformed_cards.diagnostic.as_deref(), Some("quota response malformed"));

    let mut empty_windows = FakeHttp::with(vec![HttpResponse::json(200, "{}")]);
    let empty = fetch_claude_quota(&mut empty_windows, &credential, 1_000).expect("stale cards");
    // Condensed on-card line (operator direction, bar/SYNC.md: "status ·
    // optional-time" grammar, never a sentence); the full sentence survives
    // in diagnostic_detail for a hover tooltip.
    assert_eq!(empty.diagnostic.as_deref(), Some("no quota data"));
    assert_eq!(
        empty.diagnostic_detail.as_deref(),
        Some("quota response contained no windows")
    );
}

#[test]
fn grok_missing_auth_hides_card_and_valid_response_maps_credit_usage() {
    let mut missing = FakeHttp::default();
    assert!(fetch_grok_quota(&mut missing, None).is_none());
    assert!(missing.requests.is_empty());

    let mut http = FakeHttp::with(vec![HttpResponse::json(
        200,
        r#"{"creditUsagePercent":68}"#,
    )]);
    let cards = fetch_grok_quota(&mut http, Some("grok-token")).expect("grok cards");
    assert_eq!(cards.windows[0].used_percent, 68.0);
    assert_eq!(cards.windows[0].remaining_percent, 32.0);
    assert_eq!(http.requests[0].bearer.as_deref(), Some("grok-token"));
}

#[test]
fn grok_transport_failure_and_http_status_report_exact_diagnostics() {
    let mut offline = FakeHttp::with_results(vec![Err("dns failure".to_owned())]);
    let stale = fetch_grok_quota(&mut offline, Some("token")).expect("stale cards");
    assert_eq!(stale.diagnostic.as_deref(), Some("quota request failed"));

    let mut unauthorized = FakeHttp::with(vec![HttpResponse::json(401, "{}")]);
    let unauthorized_cards = fetch_grok_quota(&mut unauthorized, Some("token")).expect("stale cards");
    assert_eq!(unauthorized_cards.diagnostic.as_deref(), Some("HTTP 401"));

    let mut no_credits = FakeHttp::with(vec![HttpResponse::json(200, "{}")]);
    let no_credits_cards = fetch_grok_quota(&mut no_credits, Some("token")).expect("stale cards");
    // Condensed on-card line (operator direction, bar/SYNC.md); the full
    // sentence survives in diagnostic_detail for a hover tooltip.
    assert_eq!(no_credits_cards.diagnostic.as_deref(), Some("no credit data"));
    assert_eq!(
        no_credits_cards.diagnostic_detail.as_deref(),
        Some("quota response contained no credit usage")
    );
}

#[test]
fn malformed_timeout_and_bounds_are_stale_or_unavailable_and_polling_is_gated() {
    let mut malformed = FakeHttp::with(vec![HttpResponse::json(200, "not-json")]);
    let cards = fetch_grok_quota(&mut malformed, Some("token")).expect("state card");
    assert_eq!(cards.state, QuotaState::Stale);

    let mut gate = PollGate::new(60);
    assert!(gate.due(1_000));
    gate.mark(1_000);
    assert!(!gate.due(1_059));
    assert!(gate.due(1_060));
}

#[test]
fn poll_gate_clamps_any_requested_minimum_to_the_sixty_second_floor() {
    let mut fast_requested = PollGate::new(1);
    assert!(fast_requested.due(0));
    fast_requested.mark(0);
    assert!(!fast_requested.due(30));
    assert!(fast_requested.due(60));
}

#[test]
fn credential_loaders_are_read_only_and_restrict_grok_issuer() {
    let directory = tempfile::tempdir().expect("temp fixture");
    let claude_path = directory.path().join(".claude.json");
    std::fs::write(
        &claude_path,
        r#"{"claudeAiOauth":{"accessToken":"claude-access","refreshToken":"claude-refresh","expiresAt":2000000}}"#,
    )
    .expect("write synthetic credential");
    let before = std::fs::read(&claude_path).unwrap();
    let claude = load_claude_credential(&claude_path)
        .expect("parse Claude credential")
        .expect("present");
    assert_eq!(claude.access_token, "claude-access");
    assert_eq!(claude.refresh_token.as_deref(), Some("claude-refresh"));
    assert_eq!(std::fs::read(&claude_path).unwrap(), before);

    let grok_path = directory.path().join("auth.json");
    std::fs::write(
        &grok_path,
        r#"{"https://foreign.example::id":{"key":"wrong"},"https://auth.x.ai::client":{"key":"grok-access"}}"#,
    )
    .expect("write synthetic Grok credential");
    let grok_before = std::fs::read(&grok_path).unwrap();
    assert_eq!(
        load_grok_token(&grok_path)
            .expect("parse Grok credential")
            .as_deref(),
        Some("grok-access")
    );
    assert_eq!(std::fs::read(&grok_path).unwrap(), grok_before);
}

#[test]
fn missing_credential_files_are_none_not_errors() {
    let directory = tempfile::tempdir().expect("temp fixture");
    let claude_path = directory.path().join("missing-claude.json");
    let grok_path = directory.path().join("missing-grok.json");
    assert_eq!(load_claude_credential(&claude_path).expect("ok"), None);
    assert_eq!(load_grok_token(&grok_path).expect("ok"), None);
}

/// Task 18 (2026-08-18): a fake `CredentialStore` so these fixtures never
/// touch the real Windows Credential Manager — the same injection
/// discipline `FakeHttp` already gives the network boundary.
struct FakeCredentialStore {
    result: Result<Option<String>, String>,
}

impl CredentialStore for FakeCredentialStore {
    fn read_generic_credential(&self, _target_name: &str) -> Result<Option<String>, String> {
        self.result.clone()
    }
}

const CLAUDE_OAUTH_BLOB: &str =
    r#"{"claudeAiOauth":{"accessToken":"cm-access","refreshToken":"cm-refresh","expiresAt":3000000}}"#;
const CLAUDE_OAUTH_BLOB_LOGGED_OUT: &str = r#"{"claudeAiOauth":{"scopes":["user:inference"]}}"#;

/// The Windows Credential Manager blob is parsed with the IDENTICAL shape
/// the file already uses — same field names, same millisecond-to-second
/// normalization — and, when it hits, the file is never even opened
/// (`missing-claude.json` doesn't exist).
#[test]
fn credential_manager_blob_parses_the_same_shape_as_the_file_and_wins_over_it() {
    let directory = tempfile::tempdir().expect("temp fixture");
    let claude_path = directory.path().join("missing-claude.json");
    let store = FakeCredentialStore {
        result: Ok(Some(CLAUDE_OAUTH_BLOB.to_owned())),
    };
    let credential = load_claude_credential_with_store(&store, &claude_path)
        .expect("parse ok")
        .expect("credential present");
    assert_eq!(credential.access_token, "cm-access");
    assert_eq!(credential.refresh_token.as_deref(), Some("cm-refresh"));
    assert_eq!(credential.expires_at, Some(3000));
}

/// A blob that's present in the store but logged out (`claudeAiOauth`
/// exists with no `accessToken` — TokenBar's own documented #26
/// daily-logout state) is skipped, not treated as a failure: the file,
/// which DOES have a usable credential here, must still be reached.
#[test]
fn credential_manager_logged_out_blob_falls_through_to_the_file() {
    let directory = tempfile::tempdir().expect("temp fixture");
    let claude_path = directory.path().join("claude.json");
    std::fs::write(
        &claude_path,
        r#"{"claudeAiOauth":{"accessToken":"file-access","refreshToken":"file-refresh","expiresAt":2000000}}"#,
    )
    .expect("write file fixture");
    let store = FakeCredentialStore {
        result: Ok(Some(CLAUDE_OAUTH_BLOB_LOGGED_OUT.to_owned())),
    };
    let credential = load_claude_credential_with_store(&store, &claude_path)
        .expect("parse ok")
        .expect("credential present from the file");
    assert_eq!(credential.access_token, "file-access");
}

/// The store reporting "no such credential" (`Ok(None)` — the common case,
/// most installs never write one) falls through to the file the same way.
#[test]
fn credential_manager_miss_falls_through_to_the_file() {
    let directory = tempfile::tempdir().expect("temp fixture");
    let claude_path = directory.path().join("claude.json");
    std::fs::write(
        &claude_path,
        r#"{"claudeAiOauth":{"accessToken":"file-access","refreshToken":"file-refresh","expiresAt":2000000}}"#,
    )
    .expect("write file fixture");
    let store = FakeCredentialStore { result: Ok(None) };
    let credential = load_claude_credential_with_store(&store, &claude_path)
        .expect("parse ok")
        .expect("credential present from the file");
    assert_eq!(credential.access_token, "file-access");
}

/// A genuine Credential-Manager-layer read error is never more actionable
/// than the file fallback, so it degrades the same way a miss does —
/// never surfaced as this call's own hard failure while the file still
/// has a usable credential.
#[test]
fn credential_manager_read_error_falls_through_to_the_file() {
    let directory = tempfile::tempdir().expect("temp fixture");
    let claude_path = directory.path().join("claude.json");
    std::fs::write(
        &claude_path,
        r#"{"claudeAiOauth":{"accessToken":"file-access","refreshToken":"file-refresh","expiresAt":2000000}}"#,
    )
    .expect("write file fixture");
    let store = FakeCredentialStore {
        result: Err("simulated Credential Manager failure".to_owned()),
    };
    let credential = load_claude_credential_with_store(&store, &claude_path)
        .expect("parse ok")
        .expect("credential present from the file");
    assert_eq!(credential.access_token, "file-access");
}

/// Mirrors `missing_credential_files_are_none_not_errors`, but through the
/// injectable entry point with a genuinely empty store AND a missing file
/// — the fully-absent case must still resolve to `Ok(None)`, not an error.
#[test]
fn credential_manager_miss_and_missing_file_together_are_none_not_an_error() {
    let directory = tempfile::tempdir().expect("temp fixture");
    let claude_path = directory.path().join("missing-claude.json");
    let store = FakeCredentialStore { result: Ok(None) };
    assert_eq!(
        load_claude_credential_with_store(&store, &claude_path).expect("ok"),
        None
    );
}

#[test]
fn codex_rollout_tail_reads_real_event_msg_rate_limits_and_numeric_reset() {
    let root = tempfile::tempdir().expect("sessions fixture");
    let nested = root.path().join("2026").join("08").join("15");
    std::fs::create_dir_all(&nested).unwrap();
    let path = nested.join("rollout-fixture.jsonl");
    std::fs::write(
        &path,
        format!(
            "{{\"timestamp\":\"2026-08-15T13:04:28Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\"}}}}\n{{\"timestamp\":\"2026-08-15T13:04:28Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"rate_limits\":{{\"primary\":{{\"used_percent\":37.0,\"window_minutes\":10080,\"resets_at\":1787197821}},\"secondary\":null}}}}}}}}\n"
        ),
    )
    .unwrap();
    let cards = load_codex_quota_from_sessions(root.path()).expect("rate limits");
    assert_eq!(cards.client_id, "codex");
    assert_eq!(cards.windows[0].used_percent, 37.0);
    // window_minutes:10080 == 7 days -> the abbreviated "7d" label (see
    // cccog_bar_quota::resolve_window_label / bar/SYNC.md), never the
    // spelled-out id text.
    assert_eq!(cards.windows[0].label, "7d");
    // resets_at now carries the unified bare "MM/DD HH:MM" local-time
    // display, not the raw epoch (see cccog_bar_quota::normalize_reset_display
    // / bar/SYNC.md).
    let want = Utc.timestamp_opt(1787197821, 0).unwrap();
    let want_reset = want.with_timezone(&Local).format("%m/%d %H:%M").to_string();
    assert_eq!(cards.windows[0].resets_at.as_deref(), Some(want_reset.as_str()));
}

#[test]
fn codex_rollout_scan_skips_archived_directories_and_missing_root() {
    let missing = load_codex_quota_from_sessions(std::path::Path::new(
        "Z:\\definitely-not-a-real-cccog-bar-quota-path",
    ));
    assert!(missing.is_err());

    let root = tempfile::tempdir().expect("sessions fixture");
    let archived = root.path().join("cccg-archive").join("2026");
    std::fs::create_dir_all(&archived).unwrap();
    std::fs::write(
        archived.join("rollout-archived.jsonl"),
        "{\"type\":\"event_msg\",\"payload\":{\"info\":{\"rate_limits\":{\"primary\":{\"used_percent\":5.0}}}}}\n",
    )
    .unwrap();
    let only_archived = load_codex_quota_from_sessions(root.path());
    assert!(only_archived.is_err());
}
