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
    format_window_line, load_claude_credential, load_codex_quota_from_sessions, load_grok_token,
    looks_like_quota_limit_error, looks_like_quota_limit_output, normalize_reset_display,
    HttpClient, HttpRequest, HttpResponse, PollGate, QuotaCards, QuotaState, QuotaWindow,
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
    let quota_cards = quota_cards_wire_json(quota_cards);
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

/// Serializes `QuotaCards` to the wire `Value` shape and stamps a
/// `displayLine` onto every window object — the single composed row
/// (`cccog_bar_quota::format_window_line`) the flyout shows per window, so
/// the C# side never re-assembles the label/percent/date text itself. Every
/// call site that emits `quotaCards` (this combined-envelope path and
/// `poll_remote_quotas_json`'s quota-only path) routes through here so both
/// carry the identical wire shape.
fn quota_cards_wire_json(cards: &[QuotaCards]) -> Value {
    let mut value = match serde_json::to_value(cards) {
        Ok(value) => value,
        Err(_) => return Value::Array(Vec::new()),
    };
    let Value::Array(card_array) = &mut value else {
        return value;
    };
    for card in card_array {
        let Some(windows) = card.get_mut("windows").and_then(Value::as_array_mut) else {
            continue;
        };
        for window in windows {
            let label = window
                .get("label")
                .and_then(Value::as_str)
                .unwrap_or_default()
                .to_owned();
            let used_percent = window
                .get("usedPercent")
                .and_then(Value::as_f64)
                .unwrap_or(0.0);
            let resets_at = window
                .get("resetsAt")
                .and_then(Value::as_str)
                .map(str::to_owned);
            let line = format_window_line(&label, used_percent, resets_at.as_deref());
            if let Value::Object(fields) = window {
                fields.insert("displayLine".to_owned(), Value::String(line));
            }
        }
    }
    value
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
        // Seconds form only ("Retry-After: 120"); the HTTP-date form parses
        // to None and the caller's own default backoff applies instead —
        // never a guess at what a date string means.
        let retry_after_seconds = response
            .headers()
            .get(reqwest::header::RETRY_AFTER)
            .and_then(|value| value.to_str().ok())
            .and_then(|text| text.trim().parse::<u64>().ok());
        let body = response
            .text()
            .map_err(|error| format!("HTTP body failed: {error}"))?;
        Ok(HttpResponse {
            status,
            body,
            retry_after_seconds,
        })
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
        diagnostic_detail: None,
        retry_after_seconds: None,
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
/// never a synthesized percentage — and a condensed one-line diagnostic
/// (operator direction, bar/SYNC.md: "at most ONE short status line" —
/// `"limit · 08/20 11:50"`, status word plus the unified bare reset date,
/// nothing else). The reset text goes through `normalize_reset_display`
/// (the single reset-time-format normalizer) so it matches the bare
/// `MM/DD HH:MM` format the regular per-window `resetsAt` field renders.
/// The fuller story this was condensed from — the dispatch failure's own
/// timestamp and the verbatim provider reset text — is never discarded:
/// it goes in `diagnostic_detail` for a hover tooltip instead.
fn apply_quota_limit_override(card: QuotaCards, failure: &QuotaLimitFailure) -> QuotaCards {
    let time_text = failure.at.format("%H:%M").to_string();
    let reset_display = failure
        .reset_hint
        .as_deref()
        .map(normalize_reset_display)
        .unwrap_or_else(|| "unknown".to_owned());
    let diagnostic = format!("limit · {reset_display}");
    let diagnostic_detail =
        format!("limit reached (dispatch failure {time_text}) · {reset_display}");
    QuotaCards {
        client_id: card.client_id,
        windows: vec![QuotaWindow {
            card_id: "limit".to_owned(),
            label: "Limit".to_owned(),
            used_percent: 100.0,
            remaining_percent: 0.0,
            resets_at: failure.reset_hint.as_deref().map(normalize_reset_display),
        }],
        state: QuotaState::Stale,
        observed_at: card.observed_at,
        diagnostic: Some(diagnostic),
        diagnostic_detail: Some(diagnostic_detail),
        retry_after_seconds: None,
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

// ── HTTP quota resilience: TTL cache, stale-on-failure fallback, 429 backoff ──
//
// Two problems observed on the operator's machine, both from claude/grok
// being polled live over HTTP on every refresh (every ~60s, including
// repeated app relaunches during testing):
//
// 1. A transient failure (HTTP 429) wiped the card down to a bare error —
//    the five-hour/seven-day bars vanished instead of staying on screen
//    with a "this might be stale" note. Fixed by caching the last
//    successful card per provider (`last_success`, persisted to
//    `%LOCALAPPDATA%\CCCG\bar-quota-cache.json` so even a fresh app launch
//    right after a 429 still shows real numbers) and rendering it labeled
//    stale on any non-Fresh result, instead of the bare failure — the bare
//    failure only ever shows when there has never been a successful fetch.
// 2. The 429 itself was self-inflicted: TokenBar-Windows (the reference
//    this shell was derived from) does not hit its quota HTTP endpoints on
//    every UI tick — `agent_usage.rs`'s `CLAUDE_HEADER_TTL_SECS` caches a
//    successful Claude fetch for 300 seconds, with the UI's own refresh
//    tick only re-reading local files in between. This module adopts the
//    same layering: claude/grok live fetches sit behind a 300s TTL
//    (`CLAUDE_GROK_TTL_SECS`); the manual Refresh button may bypass the
//    TTL but is itself rate-limited to once per 60s
//    (`MANUAL_REFRESH_MIN_INTERVAL_SECS`); an active 429 backoff always
//    wins over both. Codex is unaffected — it was already a local
//    file-tail read, not an HTTP call, so it stays on the 60s UI tick.

use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};

/// TokenBar's `CLAUDE_HEADER_TTL_SECS` (`agent_usage.rs`) — a UI refresh
/// inside this window serves the cached result, no network.
pub const CLAUDE_GROK_TTL_SECS: u64 = 300;
/// The manual Refresh button may bypass the TTL above, but never more than
/// once per minute.
pub const MANUAL_REFRESH_MIN_INTERVAL_SECS: u64 = 60;
/// Default 429 backoff when the response carried no `Retry-After` header.
pub const DEFAULT_QUOTA_BACKOFF_SECS: u64 = 5 * 60;

#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
struct CachedQuotaSuccess {
    card: QuotaCards,
    fetched_at: u64,
}

#[derive(Debug, Clone)]
struct QuotaBackoff {
    until: u64,
    reason: String,
}

/// All in-memory except `last_success`, which is also mirrored to disk (see
/// [`load_persisted_cache`]/[`save_persisted_cache`]) so a card that has
/// ever succeeded never regresses to a bare error again, even across a
/// fresh app launch.
#[derive(Default)]
pub struct QuotaResilienceCache {
    last_success: HashMap<String, CachedQuotaSuccess>,
    backoff: HashMap<String, QuotaBackoff>,
    /// Last time a REAL network attempt was made for this provider (success
    /// or failure) — what the TTL/manual-interval gate measures from. Never
    /// updated by a cache-hit (skipped) round.
    last_attempt_at: HashMap<String, u64>,
}

impl QuotaResilienceCache {
    /// Empty cache — production starts from [`load_persisted_cache`]
    /// instead; this is the test-fixture entry point (no disk access).
    pub fn empty() -> Self {
        Self::default()
    }

    pub fn with_success(mut self, client_id: &str, card: QuotaCards, fetched_at: u64) -> Self {
        self.last_success
            .insert(client_id.to_owned(), CachedQuotaSuccess { card, fetched_at });
        self
    }

    pub fn with_backoff(mut self, client_id: &str, until: u64, reason: &str) -> Self {
        self.backoff.insert(
            client_id.to_owned(),
            QuotaBackoff {
                until,
                reason: reason.to_owned(),
            },
        );
        self
    }

    pub fn with_last_attempt(mut self, client_id: &str, at: u64) -> Self {
        self.last_attempt_at.insert(client_id.to_owned(), at);
        self
    }

    pub fn is_backed_off(&self, client_id: &str, now: u64) -> bool {
        self.backoff.get(client_id).is_some_and(|backoff| now < backoff.until)
    }
}

fn quota_cache_path() -> Option<PathBuf> {
    std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .filter(|path| !path.as_os_str().is_empty())
        .map(|base| base.join("CCCG").join("bar-quota-cache.json"))
}

#[derive(Default, serde::Serialize, serde::Deserialize)]
struct PersistedQuotaCache {
    #[serde(default)]
    last_success: HashMap<String, CachedQuotaSuccess>,
}

fn load_persisted_cache() -> HashMap<String, CachedQuotaSuccess> {
    let Some(path) = quota_cache_path() else {
        return HashMap::new();
    };
    let Ok(raw) = std::fs::read_to_string(&path) else {
        return HashMap::new();
    };
    serde_json::from_str::<PersistedQuotaCache>(&raw)
        .map(|persisted| persisted.last_success)
        .unwrap_or_default()
}

fn save_persisted_cache(last_success: &HashMap<String, CachedQuotaSuccess>) {
    let Some(path) = quota_cache_path() else {
        return;
    };
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    if let Ok(json) = serde_json::to_string(&PersistedQuotaCache {
        last_success: last_success.clone(),
    }) {
        let _ = std::fs::write(path, json);
    }
}

fn quota_resilience_cache() -> &'static Mutex<QuotaResilienceCache> {
    static CACHE: OnceLock<Mutex<QuotaResilienceCache>> = OnceLock::new();
    CACHE.get_or_init(|| {
        Mutex::new(QuotaResilienceCache {
            last_success: load_persisted_cache(),
            ..QuotaResilienceCache::default()
        })
    })
}

fn is_http_429(card: &QuotaCards) -> bool {
    card.state != QuotaState::Fresh
        && card
            .diagnostic
            .as_deref()
            .is_some_and(|text| text.contains("HTTP 429"))
}

/// Stale-on-failure render: the cached bars/values unchanged, relabeled
/// stale with the failure reason and the cached fetch's own time — or, when
/// there has never been a successful fetch for this provider, the bare
/// failure (nothing to fall back to). Public so tests can exercise both
/// branches directly. The on-card diagnostic is condensed to "status ·
/// cause-code · time" (operator direction, bar/SYNC.md — no "data from"
/// prose); the full sentence survives in `diagnostic_detail` for a hover
/// tooltip.
pub fn render_with_cache_fallback(
    client_id: &str,
    reason: &str,
    cache: &QuotaResilienceCache,
    _now: u64,
) -> QuotaCards {
    match cache.last_success.get(client_id) {
        Some(cached) => {
            let time_text = DateTime::<Utc>::from_timestamp(cached.fetched_at as i64, 0)
                .map(|value| value.format("%H:%M").to_string())
                .unwrap_or_else(|| "unknown".to_owned());
            QuotaCards {
                client_id: client_id.to_owned(),
                windows: cached.card.windows.clone(),
                state: QuotaState::Stale,
                observed_at: cached.card.observed_at,
                diagnostic: Some(format!("stale · {reason} · {time_text}")),
                diagnostic_detail: Some(format!("stale · {reason} · data from {time_text}")),
                retry_after_seconds: None,
            }
        }
        None => QuotaCards {
            client_id: client_id.to_owned(),
            windows: Vec::new(),
            state: QuotaState::Unavailable,
            observed_at: None,
            diagnostic: Some(reason.to_owned()),
            diagnostic_detail: None,
            retry_after_seconds: None,
        },
    }
}

/// Before attempting a live fetch: `Some(card)` means skip the network this
/// round and render `card` instead (an active 429 backoff, or a quiet TTL
/// cache hit reusing the last successful card unchanged); `None` means go
/// ahead and fetch. Public so tests can exercise the TTL/manual-interval/
/// backoff gate directly, without a real HTTP client.
pub fn precheck_http_provider(
    client_id: &str,
    manual: bool,
    cache: &QuotaResilienceCache,
    now: u64,
) -> Option<QuotaCards> {
    if let Some(backoff) = cache.backoff.get(client_id) {
        if now < backoff.until {
            return Some(render_with_cache_fallback(client_id, &backoff.reason, cache, now));
        }
    }
    let min_interval = if manual {
        MANUAL_REFRESH_MIN_INTERVAL_SECS
    } else {
        CLAUDE_GROK_TTL_SECS
    };
    let within_ttl = cache
        .last_attempt_at
        .get(client_id)
        .is_some_and(|&last| now.saturating_sub(last) < min_interval);
    if within_ttl {
        // Quiet cache hit — no attempt was made and nothing failed, so this
        // is NOT a "stale" render, just TokenBar-style TTL reuse.
        return cache.last_success.get(client_id).map(|cached| cached.card.clone());
    }
    None
}

/// After a live fetch attempt: update the cache (success clears backoff and
/// records the new snapshot; an HTTP 429 schedules a backoff, honoring
/// `Retry-After` when the response carried one) and return the card to
/// render — the fresh card on success, a stale-on-failure render otherwise.
/// Public so tests can exercise backoff scheduling directly.
pub fn postprocess_http_provider(
    client_id: &str,
    card: QuotaCards,
    cache: &mut QuotaResilienceCache,
    now: u64,
) -> QuotaCards {
    cache.last_attempt_at.insert(client_id.to_owned(), now);
    if card.state == QuotaState::Fresh {
        cache.last_success.insert(
            client_id.to_owned(),
            CachedQuotaSuccess {
                card: card.clone(),
                fetched_at: now,
            },
        );
        cache.backoff.remove(client_id);
        return card;
    }
    if is_http_429(&card) {
        let wait = card
            .retry_after_seconds
            .filter(|&seconds| seconds > 0)
            .unwrap_or(DEFAULT_QUOTA_BACKOFF_SECS);
        cache.backoff.insert(
            client_id.to_owned(),
            QuotaBackoff {
                until: now + wait,
                reason: card.diagnostic.clone().unwrap_or_else(|| "HTTP 429".to_owned()),
            },
        );
    }
    let reason = card.diagnostic.clone().unwrap_or_else(|| "unavailable".to_owned());
    render_with_cache_fallback(client_id, &reason, cache, now)
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
    /// True only for the manual Refresh-button click — allowed to bypass
    /// the 300s TTL, but still rate-limited to once per 60s. Absent/false
    /// for the automatic timer tick (the common case).
    #[serde(default)]
    manual: bool,
    now: Option<u64>,
}

pub fn poll_remote_quotas_json(input: &str) -> String {
    let parsed = match serde_json::from_str::<QuotaPollInput>(input) {
        Ok(value) => value,
        Err(error) => return error_json(format!("quota input is malformed: {error}")),
    };
    let now = parsed.now.unwrap_or(0);
    let claude_path = parsed.claude_credential_path.as_deref().map(Path::new);
    let grok_path = parsed.grok_auth_path.as_deref().map(Path::new);

    let cache_lock = quota_resilience_cache();
    let mut cache = cache_lock.lock().unwrap_or_else(|poisoned| poisoned.into_inner());

    // Decide up front whether each HTTP-backed provider even attempts the
    // network this round (TTL/manual-interval/backoff gate); a decision here
    // means "skip — render this instead", `None` means "go fetch".
    let claude_precheck =
        claude_path.and_then(|_| precheck_http_provider("claude", parsed.manual, &cache, now));
    let grok_precheck =
        grok_path.and_then(|_| precheck_http_provider("grok", parsed.manual, &cache, now));

    let raw_cards = poll_remote_quotas_with_codex(
        parsed.codex_sessions_path.as_deref().map(Path::new),
        if claude_precheck.is_some() { None } else { claude_path },
        if grok_precheck.is_some() { None } else { grok_path },
        now,
    );

    let mut cards: Vec<QuotaCards> = Vec::with_capacity(raw_cards.len() + 2);
    for card in raw_cards {
        if card.client_id == "claude" || card.client_id == "grok" {
            let client_id = card.client_id.clone();
            cards.push(postprocess_http_provider(&client_id, card, &mut cache, now));
        } else {
            cards.push(card); // codex: file-based, untouched by this resilience layer
        }
    }
    cards.extend(claude_precheck);
    cards.extend(grok_precheck);

    save_persisted_cache(&cache.last_success);
    drop(cache);

    let cards = enrich_quota_cards(
        cards,
        parsed.dispatch_jobs_path.as_deref().map(Path::new),
        now,
    );
    serde_json::to_string(&json!({
        "schemaVersion": CCCOG_BAR_SCHEMA_VERSION,
        "ok": true,
        "quotaCards": quota_cards_wire_json(&cards),
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
