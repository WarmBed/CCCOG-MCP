//! Stable, allocation-owned C ABI for the CCCOG-Bar snapshot.
//!
//! The caller owns the returned string until it calls `ccog_bar_free_string`.
//! All parse failures are data-shaped JSON errors; they never cross the ABI as
//! a panic or process abort.

use cccog_bar_core::control::snapshot_at as control_snapshot_at;
use cccog_bar_core::dispatch::parse_status;
use cccog_bar_core::graph::GraphSnapshot;
use cccog_bar_core::owners::is_archived_path;
use cccog_bar_quota::{
    extract_human_reset_date, extract_reset_date, fetch_claude_quota, fetch_grok_quota,
    load_claude_credential, load_codex_quota_from_sessions, load_grok_token,
    looks_like_quota_limit_error, looks_like_quota_limit_output, HttpClient, HttpRequest,
    HttpResponse, PollGate, QuotaCards, QuotaState, QuotaWindow,
};
use chrono::{DateTime, Utc};
use serde::Serialize;
use serde_json::{json, Value};
use std::ffi::{c_char, CStr, CString};
use std::path::{Path, PathBuf};
use std::time::Duration;

pub const CCCOG_BAR_SCHEMA_VERSION: u64 = 1;

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct SnapshotEnvelope<'a> {
    schema_version: u64,
    ok: bool,
    snapshot: &'a Value,
    quota_cards: &'a Value,
    diagnostics: &'a Value,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ErrorEnvelope {
    schema_version: u64,
    ok: bool,
    error: ErrorBody,
}

#[derive(Debug, Serialize)]
struct ErrorBody {
    code: &'static str,
    message: String,
}

/// Build the wire envelope from JSON sections.  This safe entry point is used
/// by the debug CLI and keeps the C ABI's parsing policy in one place.
pub fn envelope_from_json(input: &str) -> String {
    let parsed = match serde_json::from_str::<Value>(input) {
        Ok(value) => value,
        Err(error) => return error_json(format!("input JSON is malformed: {error}")),
    };
    let Some(object) = parsed.as_object() else {
        return error_json("input must be a JSON object".to_owned());
    };
    let empty_array = Value::Array(Vec::new());
    let snapshot = object.get("snapshot").unwrap_or(&Value::Null);
    let quota_cards = object.get("quotaCards").unwrap_or(&empty_array);
    let diagnostics = object.get("diagnostics").unwrap_or(&empty_array);
    if !snapshot.is_object() || !quota_cards.is_array() || !diagnostics.is_array() {
        return error_json(
            "snapshot must be an object, quotaCards and diagnostics arrays".to_owned(),
        );
    }
    serde_json::to_string(&SnapshotEnvelope {
        schema_version: CCCOG_BAR_SCHEMA_VERSION,
        ok: true,
        snapshot,
        quota_cards,
        diagnostics,
    })
    .unwrap_or_else(|_| error_json("unable to serialize snapshot envelope".to_owned()))
}

/// Serialize typed core output into the same versioned envelope.
pub fn envelope_from_parts(
    snapshot: &GraphSnapshot,
    quota_cards: &[QuotaCards],
    diagnostics: &[String],
) -> String {
    let snapshot = match serde_json::to_value(snapshot) {
        Ok(value) => value,
        Err(error) => return error_json(format!("snapshot serialization failed: {error}")),
    };
    let quota_cards = match serde_json::to_value(quota_cards) {
        Ok(value) => value,
        Err(error) => return error_json(format!("quota serialization failed: {error}")),
    };
    let diagnostics = match serde_json::to_value(diagnostics) {
        Ok(value) => value,
        Err(error) => return error_json(format!("diagnostic serialization failed: {error}")),
    };
    let input = json!({
        "snapshot": snapshot,
        "quotaCards": quota_cards,
        "diagnostics": diagnostics,
    });
    envelope_from_json(&input.to_string())
}

fn error_json(message: String) -> String {
    let bounded = truncate_message(&message, 512);
    serde_json::to_string(&ErrorEnvelope {
        schema_version: CCCOG_BAR_SCHEMA_VERSION,
        ok: false,
        error: ErrorBody {
            code: "invalid_input",
            message: bounded,
        },
    })
    .unwrap_or_else(|_| {
        r#"{"schemaVersion":1,"ok":false,"error":{"code":"invalid_input","message":"serialization failure"}}"#.to_owned()
    })
}

fn truncate_message(message: &str, max_bytes: usize) -> String {
    if message.len() <= max_bytes {
        return message.to_owned();
    }
    let mut end = max_bytes;
    while end > 0 && !message.is_char_boundary(end) {
        end -= 1;
    }
    format!("{}...", &message[..end])
}

/// Production read-only HTTP adapter.  All provider-specific policy remains
/// in the core quota functions; this type only maps their bounded request
/// description to HTTPS and applies a short timeout.
pub struct ReqwestHttpClient {
    client: reqwest::blocking::Client,
}

impl ReqwestHttpClient {
    pub fn new() -> Result<Self, String> {
        reqwest::blocking::Client::builder()
            .timeout(Duration::from_secs(15))
            .build()
            .map(|client| Self { client })
            .map_err(|error| format!("HTTP client unavailable: {error}"))
    }
}

impl HttpClient for ReqwestHttpClient {
    fn send(&mut self, request: HttpRequest) -> Result<HttpResponse, String> {
        let method = reqwest::Method::from_bytes(request.method.as_bytes())
            .map_err(|error| format!("invalid HTTP method: {error}"))?;
        let mut builder = self.client.request(method, &request.url);
        if let Some(token) = request.bearer {
            builder = builder.bearer_auth(token);
        }
        if let Some(body) = request.body {
            builder = builder
                .header(
                    reqwest::header::CONTENT_TYPE,
                    "application/x-www-form-urlencoded",
                )
                .body(body);
        }
        let response = builder
            .send()
            .map_err(|error| format!("HTTP request failed: {error}"))?;
        let status = response.status().as_u16();
        let body = response
            .text()
            .map_err(|error| format!("HTTP body failed: {error}"))?;
        Ok(HttpResponse { status, body })
    }
}

/// Poll the approved provider quota endpoints.  Credential files are opened
/// read-only and callers can gate this function with `PollGate` (minimum 60s).
pub fn poll_remote_quotas(
    claude_credential_path: Option<&Path>,
    grok_auth_path: Option<&Path>,
    now: u64,
) -> Vec<QuotaCards> {
    poll_remote_quotas_with_codex(None, claude_credential_path, grok_auth_path, now)
}

pub fn poll_remote_quotas_with_codex(
    codex_sessions_path: Option<&Path>,
    claude_credential_path: Option<&Path>,
    grok_auth_path: Option<&Path>,
    now: u64,
) -> Vec<QuotaCards> {
    let mut cards = Vec::new();
    if let Some(path) = codex_sessions_path {
        cards.push(match load_codex_quota_from_sessions(path) {
            Ok(card) => card,
            Err(reason) => diagnostic_card("codex", QuotaState::Unavailable, reason),
        });
    }

    let wants_remote = claude_credential_path.is_some() || grok_auth_path.is_some();
    let mut client = wants_remote.then(|| ReqwestHttpClient::new());
    if let Some(path) = claude_credential_path {
        match load_claude_credential(path) {
            Ok(Some(credential)) => {
                if let Some(Ok(client)) = client.as_mut() {
                    if let Some(card) = fetch_claude_quota(client, &credential, now) {
                        cards.push(card);
                    }
                } else {
                    cards.push(diagnostic_card(
                        "claude",
                        QuotaState::Stale,
                        "Claude quota HTTP client unavailable".to_owned(),
                    ));
                }
            }
            Ok(None) => cards.push(diagnostic_card(
                "claude",
                QuotaState::Unavailable,
                "Claude OAuth credential not found".to_owned(),
            )),
            Err(reason) => cards.push(diagnostic_card("claude", QuotaState::Unavailable, reason)),
        }
    }
    if let Some(path) = grok_auth_path {
        match load_grok_token(path) {
            Ok(Some(token)) => {
                if let Some(Ok(client)) = client.as_mut() {
                    if let Some(card) = fetch_grok_quota(client, Some(&token)) {
                        cards.push(card);
                    }
                } else {
                    cards.push(diagnostic_card(
                        "grok",
                        QuotaState::Stale,
                        "Grok quota HTTP client unavailable".to_owned(),
                    ));
                }
            }
            Ok(None) => cards.push(diagnostic_card(
                "grok",
                QuotaState::Unavailable,
                "Grok auth credential not found".to_owned(),
            )),
            Err(reason) => cards.push(diagnostic_card("grok", QuotaState::Unavailable, reason)),
        }
    }
    cards
}

fn diagnostic_card(client_id: &str, state: QuotaState, diagnostic: String) -> QuotaCards {
    QuotaCards {
        client_id: client_id.to_owned(),
        windows: Vec::new(),
        state,
        observed_at: None,
        diagnostic: Some(diagnostic),
    }
}

/// Snapshot age past which a card whose "freshness" only means "the local
/// parse succeeded" (never "the underlying provider data is recent") gets
/// flagged stale and annotated with its age. Exists because Codex's
/// `rate_limits` read is a file-tail read: it reports `Fresh` whenever it
/// can parse the newest event, even when that event is the last one written
/// before the account got blocked and every call since has failed silently
/// from this card's point of view (see `bar/SYNC.md`).
const SNAPSHOT_STALE_AFTER_SECS: i64 = 60 * 60;

/// How far back a dispatch job failure can be to still explain "why this
/// quota card looks wrong right now".
const JOB_FAILURE_LOOKBACK_SECS: i64 = 24 * 60 * 60;

/// Providers whose local/file-based quota read can go silently stale behind
/// a real block — the ones the job-failure cross-check applies to.
const JOB_FAILURE_OVERRIDE_PROVIDERS: &[&str] = &["codex", "grok"];

/// Flag a card stale and append its age when `observed_at` (ms epoch) is
/// older than [`SNAPSHOT_STALE_AFTER_SECS`]. `now` is Unix seconds, matching
/// every other `now` in this module; `observed_at` is milliseconds (it comes
/// from a file mtime) — the mismatch is real and handled here, not assumed
/// away.
fn annotate_stale_snapshot(mut card: QuotaCards, now_seconds: u64) -> QuotaCards {
    let Some(observed_at_ms) = card.observed_at else {
        return card;
    };
    let observed_at_seconds = observed_at_ms / 1_000;
    let age_seconds = now_seconds.saturating_sub(observed_at_seconds);
    if (age_seconds as i64) <= SNAPSHOT_STALE_AFTER_SECS {
        return card;
    }
    card.state = QuotaState::Stale;
    let age_text = format_age(age_seconds);
    card.diagnostic = Some(match card.diagnostic {
        Some(existing) if !existing.is_empty() => format!("{existing} · as of {age_text} ago"),
        _ => format!("as of {age_text} ago"),
    });
    card
}

fn format_age(seconds: u64) -> String {
    if seconds < 3600 {
        format!("{}m", (seconds / 60).max(1))
    } else if seconds < 86_400 {
        format!("{}h", seconds / 3600)
    } else {
        format!("{}d", seconds / 86_400)
    }
}

struct QuotaLimitFailure {
    at: DateTime<Utc>,
    reset_hint: Option<String>,
}

/// Walk `dispatch/jobs` (excluding archived branches, same convention as
/// `cccog_bar_core::control`) for the most recent failed job for `provider`
/// within [`JOB_FAILURE_LOOKBACK_SECS`] whose `error` text reads as a
/// quota/usage-limit rejection.
fn find_quota_limit_failure(
    jobs_root: &Path,
    provider: &str,
    now: DateTime<Utc>,
) -> Option<QuotaLimitFailure> {
    let mut found: Option<QuotaLimitFailure> = None;
    walk_jobs_for_quota_failure(jobs_root, provider, now, &mut found);
    found
}

/// Bounded tail read (last [`LOG_TAIL_MAX_BYTES`]) — the provider CLI's own
/// error message is emitted near the end of the log, so there's no need to
/// read the whole file (same convention `cccog-bar-quota`'s codex rollout
/// reader uses).
const LOG_TAIL_MAX_BYTES: u64 = 4 * 1024;

fn read_log_tail(path: &Path) -> Option<String> {
    use std::io::{Read, Seek, SeekFrom};
    let mut file = std::fs::File::open(path).ok()?;
    let length = file.metadata().ok()?.len();
    let start = length.saturating_sub(LOG_TAIL_MAX_BYTES);
    file.seek(SeekFrom::Start(start)).ok()?;
    let mut bytes = Vec::new();
    file.read_to_end(&mut bytes).ok()?;
    Some(String::from_utf8_lossy(&bytes).into_owned())
}

/// The first quota-limit signal text for a failed job: its own `error`
/// field (generic marker check) first, then bounded tails of
/// `stdout.log`/`stderr.log` (provider-specific marker check). Needed
/// because the wrapper's `status.json` can record a generic "Provider
/// exited with code 1." while the provider CLI's real rejection message
/// only reached stdout — verified real case: job
/// `20260815T141347Z_ce298f32` (codex). See `bar/SYNC.md`.
fn quota_limit_signal_text(provider: &str, status_error: Option<&str>, job_dir: &Path) -> Option<String> {
    if let Some(error) = status_error {
        if looks_like_quota_limit_error(error) {
            return Some(error.to_owned());
        }
    }
    for filename in ["stdout.log", "stderr.log"] {
        let Some(tail) = read_log_tail(&job_dir.join(filename)) else {
            continue;
        };
        if looks_like_quota_limit_output(provider, &tail) {
            return Some(tail);
        }
    }
    None
}

fn walk_jobs_for_quota_failure(
    dir: &Path,
    provider: &str,
    now: DateTime<Utc>,
    found: &mut Option<QuotaLimitFailure>,
) {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if is_archived_path(&path) {
            continue;
        }
        if path.is_dir() {
            walk_jobs_for_quota_failure(&path, provider, now, found);
            continue;
        }
        if path.file_name().and_then(|name| name.to_str()) != Some("status.json") {
            continue;
        }
        let Ok(raw) = std::fs::read_to_string(&path) else {
            continue;
        };
        let Ok(status) = parse_status(&raw) else {
            continue;
        };
        if !status.status.eq_ignore_ascii_case("failed") {
            continue;
        }
        if !status.provider.eq_ignore_ascii_case(provider) {
            continue;
        }
        let Some(at) = status
            .finished_at
            .as_deref()
            .or(status.started_at.as_deref())
            .or(status.created_at.as_deref())
            .and_then(|value| DateTime::parse_from_rfc3339(value.trim()).ok())
            .map(|value| value.with_timezone(&Utc))
        else {
            continue; // never fabricate a time for an unparseable/missing one
        };
        let age = (now - at).num_seconds();
        if !(0..=JOB_FAILURE_LOOKBACK_SECS).contains(&age) {
            continue;
        }
        let Some(job_dir) = path.parent() else {
            continue;
        };
        let Some(signal_text) =
            quota_limit_signal_text(provider, status.error.as_deref(), job_dir)
        else {
            continue;
        };
        let candidate = QuotaLimitFailure {
            at,
            reset_hint: extract_human_reset_date(&signal_text)
                .or_else(|| extract_reset_date(&signal_text)),
        };
        let is_newer = found.as_ref().is_none_or(|existing| candidate.at > existing.at);
        if is_newer {
            *found = Some(candidate);
        }
    }
}

/// Overrides a card to an honest "we saw this happen" state: a single 100%
/// window — the red-bar color communicates blocked-ness as a status color,
/// never a synthesized percentage — and a diagnostic naming the dispatch
/// failure's own timestamp plus the reset date literally found in its
/// message, or the word "unknown" when none was found (never invented).
fn apply_quota_limit_override(card: QuotaCards, failure: &QuotaLimitFailure) -> QuotaCards {
    let time_text = failure.at.format("%H:%M").to_string();
    let reset_text = failure.reset_hint.as_deref().unwrap_or("unknown");
    let diagnostic = format!("limit reached (dispatch failure {time_text}) · resets {reset_text}");
    QuotaCards {
        client_id: card.client_id,
        windows: vec![QuotaWindow {
            card_id: "limit".to_owned(),
            label: "Limit".to_owned(),
            used_percent: 100.0,
            remaining_percent: 0.0,
            resets_at: failure.reset_hint.clone(),
        }],
        state: QuotaState::Stale,
        observed_at: card.observed_at,
        diagnostic: Some(diagnostic),
    }
}

/// Post-process polled cards: flag/annotate stale local snapshots, then
/// override with a dispatch-failure-backed "limit reached" card wherever
/// the jobs history has a recent, matching failure — the override always
/// wins over the age annotation since it is strictly more informative (a
/// real observed block beats "this data might be old"). Public so tests can
/// exercise it directly with synthetic `QuotaCards`/dispatch-root fixtures,
/// without needing network access (no card here ever comes from a live
/// HTTP call in a test).
pub fn enrich_quota_cards(
    cards: Vec<QuotaCards>,
    dispatch_jobs_root: Option<&Path>,
    now_seconds: u64,
) -> Vec<QuotaCards> {
    let now = DateTime::<Utc>::from_timestamp(now_seconds as i64, 0).unwrap_or_else(Utc::now);
    cards
        .into_iter()
        .map(|card| {
            let annotated = annotate_stale_snapshot(card, now_seconds);
            if !JOB_FAILURE_OVERRIDE_PROVIDERS.contains(&annotated.client_id.as_str()) {
                return annotated;
            }
            let Some(jobs_root) = dispatch_jobs_root else {
                return annotated;
            };
            match find_quota_limit_failure(jobs_root, &annotated.client_id, now) {
                Some(failure) => apply_quota_limit_override(annotated, &failure),
                None => annotated,
            }
        })
        .collect()
}

/// Background-safe coordinator for the shell.  The minimum is clamped by the
/// core `PollGate` to 60 seconds; a call inside the window returns `None` and
/// leaves the last successfully published cards untouched by the caller.
pub struct BackgroundQuotaPoller {
    gate: PollGate,
    last_cards: Vec<QuotaCards>,
}

impl BackgroundQuotaPoller {
    pub fn new(minimum_seconds: u64) -> Self {
        Self {
            gate: PollGate::new(minimum_seconds),
            last_cards: Vec::new(),
        }
    }

    pub fn poll(
        &mut self,
        now: u64,
        claude_credential_path: Option<&Path>,
        grok_auth_path: Option<&Path>,
    ) -> Option<Vec<QuotaCards>> {
        if !self.gate.due(now) {
            return None;
        }
        self.gate.mark(now);
        self.last_cards = poll_remote_quotas(claude_credential_path, grok_auth_path, now);
        Some(self.last_cards.clone())
    }

    pub fn poll_with_codex(
        &mut self,
        now: u64,
        codex_sessions_path: Option<&Path>,
        claude_credential_path: Option<&Path>,
        grok_auth_path: Option<&Path>,
    ) -> Option<Vec<QuotaCards>> {
        if !self.gate.due(now) {
            return None;
        }
        self.gate.mark(now);
        self.last_cards = poll_remote_quotas_with_codex(
            codex_sessions_path,
            claude_credential_path,
            grok_auth_path,
            now,
        );
        Some(self.last_cards.clone())
    }
}

#[derive(Debug, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
struct ControlSnapshotInput {
    dispatch_root: Option<String>,
    /// Unix seconds; `None`/absent uses the real current time (production
    /// default) — tests pass an explicit value for determinism.
    now: Option<i64>,
}

/// The windowed, aggregated Flow view: Active + last-2h terminal window,
/// same-path `×N` aggregation, resolved target titles. Replaces the shell's
/// previous from-scratch dispatch-root scan — see
/// `cccog_bar_core::control` for the windowing rules and `bar/SYNC.md` for
/// why this now lives in Rust instead of C#.
pub fn control_snapshot_json(input: &str) -> String {
    let parsed = match serde_json::from_str::<ControlSnapshotInput>(input) {
        Ok(value) => value,
        Err(error) => return error_json(format!("control input is malformed: {error}")),
    };
    let now = parsed
        .now
        .and_then(|seconds| DateTime::<Utc>::from_timestamp(seconds, 0))
        .unwrap_or_else(Utc::now);
    let root = parsed.dispatch_root.map(PathBuf::from);
    let payload = control_snapshot_at(root.as_deref(), now);
    serde_json::to_string(&json!({
        "schemaVersion": CCCOG_BAR_SCHEMA_VERSION,
        "ok": true,
        "control": payload,
    }))
    .unwrap_or_else(|error| error_json(format!("control serialization failed: {error}")))
}

#[no_mangle]
pub unsafe extern "C" fn cccog_bar_control_snapshot(input_json: *const c_char) -> *mut c_char {
    let result = std::panic::catch_unwind(|| {
        if input_json.is_null() {
            return error_json("input pointer is null".to_owned());
        }
        let input = CStr::from_ptr(input_json).to_string_lossy();
        control_snapshot_json(&input)
    })
    .unwrap_or_else(|_| error_json("control request failed safely".to_owned()));
    CString::new(result)
        .unwrap_or_else(|_| CString::new(error_json("NUL in response".to_owned())).unwrap())
        .into_raw()
}

#[derive(Debug, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
struct QuotaPollInput {
    codex_sessions_path: Option<String>,
    claude_credential_path: Option<String>,
    grok_auth_path: Option<String>,
    /// `%LOCALAPPDATA%\CCCG\dispatch\jobs` — enables the stale-snapshot age
    /// annotation and the job-failure quota-limit cross-check. Optional and
    /// backward compatible: omitting it just skips both enrichments (same
    /// output as before they existed).
    dispatch_jobs_path: Option<String>,
    now: Option<u64>,
}

pub fn poll_remote_quotas_json(input: &str) -> String {
    let parsed = match serde_json::from_str::<QuotaPollInput>(input) {
        Ok(value) => value,
        Err(error) => return error_json(format!("quota input is malformed: {error}")),
    };
    let now = parsed.now.unwrap_or(0);
    let cards = poll_remote_quotas_with_codex(
        parsed.codex_sessions_path.as_deref().map(Path::new),
        parsed.claude_credential_path.as_deref().map(Path::new),
        parsed.grok_auth_path.as_deref().map(Path::new),
        now,
    );
    let cards = enrich_quota_cards(
        cards,
        parsed.dispatch_jobs_path.as_deref().map(Path::new),
        now,
    );
    serde_json::to_string(&json!({
        "schemaVersion": CCCOG_BAR_SCHEMA_VERSION,
        "ok": true,
        "quotaCards": cards,
        "diagnostics": [],
    }))
    .unwrap_or_else(|error| error_json(format!("quota serialization failed: {error}")))
}

#[no_mangle]
pub unsafe extern "C" fn cccog_bar_snapshot_json(input_json: *const c_char) -> *mut c_char {
    let result = std::panic::catch_unwind(|| {
        if input_json.is_null() {
            return error_json("input pointer is null".to_owned());
        }
        let input = CStr::from_ptr(input_json).to_string_lossy();
        envelope_from_json(&input)
    })
    .unwrap_or_else(|_| error_json("snapshot request failed safely".to_owned()));
    CString::new(result)
        .unwrap_or_else(|_| CString::new(error_json("NUL in response".to_owned())).unwrap())
        .into_raw()
}

#[no_mangle]
pub unsafe extern "C" fn cccog_bar_free_string(value: *mut c_char) {
    if !value.is_null() {
        drop(CString::from_raw(value));
    }
}

#[no_mangle]
pub unsafe extern "C" fn cccog_bar_poll_quotas(input_json: *const c_char) -> *mut c_char {
    let result = std::panic::catch_unwind(|| {
        if input_json.is_null() {
            return error_json("input pointer is null".to_owned());
        }
        let input = CStr::from_ptr(input_json).to_string_lossy();
        poll_remote_quotas_json(&input)
    })
    .unwrap_or_else(|_| error_json("quota request failed safely".to_owned()));
    CString::new(result)
        .unwrap_or_else(|_| CString::new(error_json("NUL in response".to_owned())).unwrap())
        .into_raw()
}
