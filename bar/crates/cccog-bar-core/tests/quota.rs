use cccog_bar_core::quota::{
    fetch_claude_quota, fetch_grok_quota, load_claude_credential, load_codex_quota_from_sessions,
    load_grok_token, parse_codex_rate_limits, HttpClient, HttpRequest, HttpResponse,
    OAuthCredential, PollGate, QuotaState,
};

#[derive(Default)]
struct FakeHttp {
    responses: Vec<HttpResponse>,
    requests: Vec<HttpRequest>,
}

impl FakeHttp {
    fn with(responses: Vec<HttpResponse>) -> Self {
        Self {
            responses,
            requests: Vec::new(),
        }
    }
}

impl HttpClient for FakeHttp {
    fn send(&mut self, request: HttpRequest) -> Result<HttpResponse, String> {
        self.requests.push(request);
        self.responses
            .first()
            .cloned()
            .ok_or_else(|| "offline".to_owned())
            .map(|response| {
                self.responses.remove(0);
                response
            })
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
fn claude_200_maps_cards_and_401_is_stale_without_data_leak() {
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
    assert_eq!(refresh.requests[1].method, "GET");
    assert_eq!(refresh.requests[1].bearer.as_deref(), Some("refreshed"));
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
    assert_eq!(cards.windows[0].resets_at.as_deref(), Some("1787197821"));
}
