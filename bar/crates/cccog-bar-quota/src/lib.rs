//! Provider quota cards with an injected, tightly bounded HTTP boundary.
//!
//! The crate never owns a network implementation.  Production callers (the
//! FFI layer) provide a read-only HTTP client; tests provide a
//! recorder/fake and therefore never contact provider endpoints.  This is a
//! consolidated, standalone crate (see the workspace `SYNC.md` for the
//! TokenBar-Windows derivation this grew from) so the CCCOG-Bar shell can
//! depend on quota polling without pulling in the flow/graph core.
//!
//! Scope, one per provider:
//! - Codex: local rollout `rate_limits` tail (file-only, no network).
//! - Claude: `GET api.anthropic.com/api/oauth/usage`, with a
//!   `platform.claude.com` OAuth token refresh when the local credential has
//!   expired; a `403` means the token is inference-only and the card
//!   reports `Unavailable`, never a probe of `/messages`.
//! - Grok: `GET cli-chat-proxy.grok.com/v1/billing?format=credits` using the
//!   `https://auth.x.ai::*` OIDC entry only; a foreign bearer token in the
//!   same JSON file is never sent.
//!
//! Failures always carry a concrete, short diagnostic string (e.g. `HTTP
//! 401`, `token refresh rejected`) — never a vague "unavailable".

use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum QuotaState {
    Fresh,
    Stale,
    Unavailable,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct QuotaWindow {
    pub card_id: String,
    pub label: String,
    pub used_percent: f64,
    pub remaining_percent: f64,
    pub resets_at: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct QuotaCards {
    pub client_id: String,
    pub windows: Vec<QuotaWindow>,
    pub state: QuotaState,
    pub observed_at: Option<u64>,
    pub diagnostic: Option<String>,
    /// Set only on an HTTP 429 that carried a `Retry-After` header (seconds
    /// form) — the caller's backoff scheduling honors this over its own
    /// default when present.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub retry_after_seconds: Option<u64>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HttpRequest {
    pub method: String,
    pub url: String,
    pub bearer: Option<String>,
    pub body: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HttpResponse {
    pub status: u16,
    pub body: String,
    /// Parsed `Retry-After` response header, seconds form only (an
    /// HTTP-date form parses to `None` — the caller's own default backoff
    /// applies instead, never a guess at the date's meaning).
    pub retry_after_seconds: Option<u64>,
}

impl HttpResponse {
    pub fn json(status: u16, body: &str) -> Self {
        Self {
            status,
            body: body.to_owned(),
            retry_after_seconds: None,
        }
    }

    pub fn json_with_retry_after(status: u16, body: &str, retry_after_seconds: u64) -> Self {
        Self {
            status,
            body: body.to_owned(),
            retry_after_seconds: Some(retry_after_seconds),
        }
    }
}

pub trait HttpClient {
    fn send(&mut self, request: HttpRequest) -> Result<HttpResponse, String>;
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OAuthCredential {
    pub access_token: String,
    pub refresh_token: Option<String>,
    pub expires_at: Option<u64>,
}

impl OAuthCredential {
    pub fn new(access_token: &str, refresh_token: Option<&str>, expires_at: Option<u64>) -> Self {
        Self {
            access_token: access_token.to_owned(),
            refresh_token: refresh_token.map(str::to_owned),
            expires_at,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PollGate {
    minimum_seconds: u64,
    last_poll: Option<u64>,
}

/// Same client id TokenBar-Windows sends on Claude's `platform.claude.com`
/// refresh call; the endpoint has been observed to require it. See SYNC.md.
const CLAUDE_REFRESH_CLIENT_ID: &str = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

/// A dispatch/session directory component that marks an archived branch; the
/// same convention `cccog-bar-core::owners::is_archived_path` uses.  Kept as
/// a tiny local copy so this crate has no dependency on the flow/graph core.
fn is_archived_path(path: &Path) -> bool {
    path.to_string_lossy()
        .split(['\\', '/'])
        .any(|component| component.eq_ignore_ascii_case("cccg-archive"))
}

/// Read Claude's local OAuth JSON without modifying it.  `expiresAt` is the
/// CLI's millisecond timestamp and is normalized to seconds for the poller.
pub fn load_claude_credential(path: &Path) -> Result<Option<OAuthCredential>, String> {
    if !path.is_file() {
        return Ok(None);
    }
    let raw =
        std::fs::read_to_string(path).map_err(|_| "cannot read Claude OAuth file".to_owned())?;
    let value: Value =
        serde_json::from_str(&raw).map_err(|_| "Claude OAuth JSON malformed".to_owned())?;
    let oauth = value
        .get("claudeAiOauth")
        .ok_or_else(|| "Claude OAuth entry missing".to_owned())?;
    let access = oauth
        .get("accessToken")
        .or_else(|| oauth.get("access_token"))
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|token| !token.is_empty())
        .ok_or_else(|| "Claude OAuth access token missing".to_owned())?;
    let refresh = oauth
        .get("refreshToken")
        .or_else(|| oauth.get("refresh_token"))
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|token| !token.is_empty());
    let expires_at = oauth
        .get("expiresAt")
        .or_else(|| oauth.get("expires_at"))
        .and_then(|value| {
            value
                .as_u64()
                .or_else(|| value.as_f64().map(|value| value as u64))
        })
        .map(|millis| millis / 1_000);
    Ok(Some(OAuthCredential::new(access, refresh, expires_at)))
}

/// Read only the genuine `auth.x.ai::<client>` OIDC entry.  A foreign bearer
/// token in the same JSON object is never sent to the Grok billing endpoint.
pub fn load_grok_token(path: &Path) -> Result<Option<String>, String> {
    if !path.is_file() {
        return Ok(None);
    }
    let raw = std::fs::read_to_string(path).map_err(|_| "cannot read Grok auth file".to_owned())?;
    let value: Value =
        serde_json::from_str(&raw).map_err(|_| "Grok auth JSON malformed".to_owned())?;
    let Some(object) = value.as_object() else {
        return Err("Grok auth JSON is not an object".to_owned());
    };
    for (key, entry) in object {
        if !key.starts_with("https://auth.x.ai::") {
            continue;
        }
        let token = entry
            .get("key")
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|token| !token.is_empty());
        if token.is_some() {
            return Ok(token.map(str::to_owned));
        }
    }
    Ok(None)
}

impl PollGate {
    pub fn new(minimum_seconds: u64) -> Self {
        Self {
            minimum_seconds: minimum_seconds.max(60),
            last_poll: None,
        }
    }

    pub fn due(&self, now: u64) -> bool {
        self.last_poll
            .is_none_or(|last| now.saturating_sub(last) >= self.minimum_seconds)
    }

    pub fn mark(&mut self, now: u64) {
        self.last_poll = Some(now);
    }
}

pub fn parse_codex_rate_limits(input: &str) -> Result<QuotaCards, String> {
    let value: Value =
        serde_json::from_str(input).map_err(|_| "invalid Codex rate_limits JSON".to_owned())?;
    let root = value
        .get("rate_limits")
        .or_else(|| value.get("rate_limit"))
        .or_else(|| {
            value
                .get("payload")
                .and_then(|payload| payload.get("rate_limits"))
        })
        .or_else(|| {
            value
                .get("payload")
                .and_then(|payload| payload.get("info"))
                .and_then(|info| info.get("rate_limits"))
        })
        .unwrap_or(&value);
    let mut windows = Vec::new();
    if let Some(used) = percent(root, "used_percent") {
        windows.push(window("primary", "Primary", used, root));
    } else if let Some(object) = root.as_object() {
        for (id, child) in object {
            if let Some(used) = percent(child, "used_percent") {
                windows.push(window(id, &label_for(id, child), used, child));
            }
        }
    }
    if windows.is_empty() {
        return Err("Codex response contained no usable rate limit windows".to_owned());
    }
    Ok(QuotaCards {
        client_id: "codex".to_owned(),
        windows,
        state: QuotaState::Fresh,
        observed_at: None,
        diagnostic: None,
        retry_after_seconds: None,
    })
}

/// Read only the newest bounded tail of Codex rollout files.  `rate_limits`
/// is emitted as an `event_msg` near the end of each JSONL rollout, so there
/// is no need to reread the full transcript or inspect prompt content.
pub fn load_codex_quota_from_sessions(root: &Path) -> Result<QuotaCards, String> {
    let mut files = Vec::new();
    collect_rollouts(root, &mut files);
    files.sort_by(|left, right| {
        modified_time(right)
            .cmp(&modified_time(left))
            .then_with(|| right.cmp(left))
    });
    for path in files {
        let Ok(tail) = read_tail(&path, 256 * 1024) else {
            continue;
        };
        for line in tail.lines().rev() {
            if !line.contains("\"rate_limits\"") {
                continue;
            }
            if let Ok(mut cards) = parse_codex_rate_limits(line) {
                cards.observed_at = modified_time(&path).map(|time| time as u64);
                return Ok(cards);
            }
        }
    }
    Err("Codex rollout rate_limits not found in bounded tails".to_owned())
}

fn collect_rollouts(root: &Path, files: &mut Vec<PathBuf>) {
    let Ok(entries) = std::fs::read_dir(root) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if is_archived_path(&path) {
            continue;
        }
        if path.is_dir() {
            collect_rollouts(&path, files);
        } else if path
            .file_name()
            .and_then(|name| name.to_str())
            .is_some_and(|name| name.starts_with("rollout-") && name.ends_with(".jsonl"))
        {
            files.push(path);
        }
    }
}

fn read_tail(path: &Path, max_bytes: u64) -> Result<String, String> {
    let mut file = std::fs::File::open(path).map_err(|_| "Codex rollout unavailable".to_owned())?;
    let length = file
        .metadata()
        .map_err(|_| "Codex rollout metadata unavailable".to_owned())?
        .len();
    let start = length.saturating_sub(max_bytes);
    file.seek(SeekFrom::Start(start))
        .map_err(|_| "Codex rollout seek failed".to_owned())?;
    let mut bytes = Vec::with_capacity((length - start) as usize);
    file.read_to_end(&mut bytes)
        .map_err(|_| "Codex rollout tail read failed".to_owned())?;
    Ok(String::from_utf8_lossy(&bytes).into_owned())
}

fn modified_time(path: &Path) -> Option<u128> {
    std::fs::metadata(path)
        .ok()?
        .modified()
        .ok()?
        .duration_since(std::time::UNIX_EPOCH)
        .ok()
        .map(|duration| duration.as_millis() as u128)
}

/// Percent-encode a small fixed set of form fields.  Values here are always
/// opaque bearer/refresh tokens or a constant client id, so a conservative
/// unreserved-character allowlist is sufficient without pulling in a URL crate.
fn form_urlencode(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    for byte in value.bytes() {
        match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                out.push(byte as char)
            }
            _ => out.push_str(&format!("%{byte:02X}")),
        }
    }
    out
}

pub fn fetch_claude_quota<C: HttpClient>(
    client: &mut C,
    credential: &OAuthCredential,
    now: u64,
) -> Option<QuotaCards> {
    let mut access_token = credential.access_token.clone();
    if credential
        .expires_at
        .is_some_and(|expires_at| expires_at <= now)
    {
        if let Some(refresh_token) = credential.refresh_token.as_deref() {
            let body = format!(
                "grant_type=refresh_token&refresh_token={}&client_id={}",
                form_urlencode(refresh_token),
                form_urlencode(CLAUDE_REFRESH_CLIENT_ID)
            );
            let refresh = client.send(HttpRequest {
                method: "POST".to_owned(),
                url: "https://platform.claude.com/v1/oauth/token".to_owned(),
                bearer: None,
                body: Some(body),
            });
            let Ok(response) = refresh else {
                return Some(stale("claude", "token refresh failed"));
            };
            if response.status != 200 {
                return Some(stale("claude", "token refresh rejected"));
            }
            let Ok(body) = serde_json::from_str::<Value>(&response.body) else {
                return Some(stale("claude", "token refresh response malformed"));
            };
            let Some(token) = body.get("access_token").and_then(Value::as_str) else {
                return Some(stale("claude", "token refresh returned no access token"));
            };
            access_token = token.to_owned();
        }
    }

    let response = client.send(HttpRequest {
        method: "GET".to_owned(),
        url: "https://api.anthropic.com/api/oauth/usage".to_owned(),
        bearer: Some(access_token),
        body: None,
    });
    let Ok(response) = response else {
        return Some(stale("claude", "quota request failed"));
    };
    if response.status == 403 {
        return Some(unavailable("claude", "OAuth token is inference-only"));
    }
    if response.status != 200 {
        return Some(stale_rate_limited("claude", response.status, response.retry_after_seconds));
    }
    let Ok(value) = serde_json::from_str::<Value>(&response.body) else {
        return Some(stale("claude", "quota response malformed"));
    };
    let windows = parse_named_windows(&value, &["five_hour", "seven_day", "seven_day_oauth_apps"]);
    if windows.is_empty() {
        return Some(stale("claude", "quota response contained no windows"));
    }
    Some(QuotaCards {
        client_id: "claude".to_owned(),
        windows,
        state: QuotaState::Fresh,
        observed_at: None,
        diagnostic: None,
        retry_after_seconds: None,
    })
}

pub fn fetch_grok_quota<C: HttpClient>(client: &mut C, token: Option<&str>) -> Option<QuotaCards> {
    let token = token?;
    let response = client.send(HttpRequest {
        method: "GET".to_owned(),
        url: "https://cli-chat-proxy.grok.com/v1/billing?format=credits".to_owned(),
        bearer: Some(token.to_owned()),
        body: None,
    });
    let Ok(response) = response else {
        return Some(stale("grok", "quota request failed"));
    };
    if response.status != 200 {
        return Some(stale_rate_limited("grok", response.status, response.retry_after_seconds));
    }
    let Ok(value) = serde_json::from_str::<Value>(&response.body) else {
        return Some(stale("grok", "quota response malformed"));
    };
    let Some(used) = value.get("creditUsagePercent").and_then(as_percent) else {
        return Some(stale("grok", "quota response contained no credit usage"));
    };
    Some(QuotaCards {
        client_id: "grok".to_owned(),
        windows: vec![window("credits", "Credits", used, &value)],
        state: QuotaState::Fresh,
        observed_at: None,
        diagnostic: None,
        retry_after_seconds: None,
    })
}

fn parse_named_windows(value: &Value, names: &[&str]) -> Vec<QuotaWindow> {
    names
        .iter()
        .filter_map(|name| {
            value
                .get(*name)
                .and_then(|window| percent(window, "utilization").map(|used| (*name, window, used)))
        })
        .map(|(name, value, used)| window(name, &label_for(name, value), used, value))
        .collect()
}

fn window(id: &str, label: &str, used: f64, value: &Value) -> QuotaWindow {
    let used = clamp_percent(used);
    QuotaWindow {
        card_id: id.to_owned(),
        label: label.to_owned(),
        used_percent: used,
        remaining_percent: (100.0 - used).max(0.0),
        resets_at: value.get("resets_at").and_then(reset_value),
    }
}

fn reset_value(value: &Value) -> Option<String> {
    value
        .as_str()
        .map(str::to_owned)
        .or_else(|| value.as_u64().map(|seconds| seconds.to_string()))
        .or_else(|| value.as_f64().map(|seconds| (seconds as u64).to_string()))
}

fn label_for(id: &str, value: &Value) -> String {
    value
        .get("label")
        .and_then(Value::as_str)
        .map(str::to_owned)
        .unwrap_or_else(|| id.replace('_', " "))
}

fn percent(value: &Value, key: &str) -> Option<f64> {
    value.get(key).and_then(as_percent)
}

fn as_percent(value: &Value) -> Option<f64> {
    let number = value
        .as_f64()
        .or_else(|| value.as_str()?.parse::<f64>().ok())?;
    number.is_finite().then(|| clamp_percent(number))
}

fn clamp_percent(value: f64) -> f64 {
    value.clamp(0.0, 100.0)
}

fn stale(client_id: &str, diagnostic: impl Into<String>) -> QuotaCards {
    QuotaCards {
        client_id: client_id.to_owned(),
        windows: Vec::new(),
        state: QuotaState::Stale,
        observed_at: None,
        diagnostic: Some(diagnostic.into()),
        retry_after_seconds: None,
    }
}

/// Same as `stale`, but carries the response's `Retry-After` (when the
/// caller sent one) so a backoff scheduler downstream can honor it instead
/// of guessing a default wait — only meaningful for the caller when
/// `status == 429`, but harmless to attach for any non-200.
fn stale_rate_limited(client_id: &str, status: u16, retry_after_seconds: Option<u64>) -> QuotaCards {
    QuotaCards {
        retry_after_seconds,
        ..stale(client_id, format!("HTTP {status}"))
    }
}

fn unavailable(client_id: &str, diagnostic: impl Into<String>) -> QuotaCards {
    QuotaCards {
        client_id: client_id.to_owned(),
        windows: Vec::new(),
        state: QuotaState::Unavailable,
        observed_at: None,
        diagnostic: Some(diagnostic.into()),
        retry_after_seconds: None,
    }
}

/// Text-pattern check only — it never estimates a percentage. A dispatch
/// job's `error` field is the provider process's own failure message
/// (surfaced to CCCG as a generic non-zero exit); when that message itself
/// reads as a quota/usage-limit rejection, the caller can use this as a
/// signal to override an otherwise-stale local snapshot with an honest "we
/// saw this happen" card instead of a frozen pre-block percentage. See
/// `cccog-bar-ffi`'s job-failure cross-check for where this is applied.
pub fn looks_like_quota_limit_error(error: &str) -> bool {
    let lower = error.to_lowercase();
    const MARKERS: &[&str] = &[
        "429",
        "402",
        "rate limit",
        "rate_limit",
        "usage limit",
        "quota",
        "credit",
        "exhaust",
        "too many requests",
        "insufficient",
        "billing",
        "resource_exhausted",
        "limit reached",
        "limit exceeded",
    ];
    MARKERS.iter().any(|marker| lower.contains(marker))
}

/// Best-effort reset-date extraction from free-text error content: the
/// first `YYYY-MM-DD` occurrence, optionally followed by a time component,
/// anywhere in the string. Returns `None` — never a guess — when nothing
/// date-shaped is present, per the "raw and honest, never invented"
/// requirement this exists under.
pub fn extract_reset_date(error: &str) -> Option<String> {
    let bytes = error.as_bytes();
    if bytes.len() < 10 {
        return None;
    }
    for start in 0..=(bytes.len() - 10) {
        if !is_iso_date_start(&bytes[start..start + 10]) {
            continue;
        }
        if !error.is_char_boundary(start) {
            continue;
        }
        let date_end = start + 10;
        // Extend to cover a trailing "THH:MM" or " HH:MM" time component
        // when present, so "resets 2026-08-20T11:50:00Z" reads whole.
        let mut end = date_end;
        let rest = &error[date_end..];
        if let Some(time_len) = time_suffix_len(rest) {
            end += time_len;
        }
        return Some(error[start..end].to_owned());
    }
    None
}

fn is_iso_date_start(bytes: &[u8]) -> bool {
    bytes.len() == 10
        && bytes[0..4].iter().all(u8::is_ascii_digit)
        && bytes[4] == b'-'
        && bytes[5..7].iter().all(u8::is_ascii_digit)
        && bytes[7] == b'-'
        && bytes[8..10].iter().all(u8::is_ascii_digit)
}

/// Length of a leading `T` or space followed by `HH:MM` (and an optional
/// `:SS` / trailing `Z`) at the start of `rest`, or 0 when there is none.
fn time_suffix_len(rest: &str) -> Option<usize> {
    let bytes = rest.as_bytes();
    if bytes.len() < 6 || (bytes[0] != b'T' && bytes[0] != b' ') {
        return None;
    }
    let digits_colon_digits = |b: &[u8]| -> bool {
        b.len() >= 5
            && b[0].is_ascii_digit()
            && b[1].is_ascii_digit()
            && b[2] == b':'
            && b[3].is_ascii_digit()
            && b[4].is_ascii_digit()
    };
    if !digits_colon_digits(&bytes[1..]) {
        return None;
    }
    let mut end = 6; // "T12:34"
    if bytes.len() > end && bytes[end] == b':' && bytes.len() >= end + 3 {
        let seconds = &bytes[end + 1..end + 3];
        if seconds.len() == 2 && seconds.iter().all(u8::is_ascii_digit) {
            end += 3;
        }
    }
    if bytes.len() > end && bytes[end] == b'Z' {
        end += 1;
    }
    Some(end)
}

/// Provider-specific markers checked against a failed job's stdout/stderr
/// tail — used when the wrapper's `status.json` `error` field is a generic
/// "exited with code 1" but the provider CLI's own message (its real quota
/// rejection) landed in the process output instead. Real case: job
/// `20260815T141347Z_ce298f32` (codex) had `error: "Provider exited with
/// code 1."` while `stdout.log`'s tail carried the actual
/// `{"type":"error","message":"You've hit your usage limit. ..."}` event.
pub fn looks_like_quota_limit_output(provider: &str, text: &str) -> bool {
    let lower = text.to_lowercase();
    match provider.to_lowercase().as_str() {
        "codex" => lower.contains("hit your usage limit") || lower.contains("usage_limit"),
        "grok" => lower.contains("402") || lower.contains("credits"),
        _ => looks_like_quota_limit_error(text),
    }
}

/// Best-effort human-readable reset-date extraction (e.g. "Aug 20th, 2026
/// 11:50 AM" out of "...or try again at Aug 20th, 2026 11:50 AM."). Returns
/// the exact matched substring — never a reformatted or invented date — or
/// `None` when nothing date-shaped is present.
pub fn extract_human_reset_date(text: &str) -> Option<String> {
    static PATTERN: &str = r"(?i)\b(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:tember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\.?\s+\d{1,2}(?:st|nd|rd|th)?,?\s+\d{4}(?:,?\s+\d{1,2}:\d{2}\s*(?:am|pm))?";
    let re = regex::Regex::new(PATTERN).ok()?;
    re.find(text).map(|found| found.as_str().to_owned())
}

#[cfg(test)]
mod quota_limit_error_tests {
    use super::*;

    #[test]
    fn recognizes_common_quota_limit_phrasings_and_ignores_unrelated_failures() {
        assert!(looks_like_quota_limit_error("HTTP 429 Too Many Requests"));
        assert!(looks_like_quota_limit_error("HTTP 402 Payment Required"));
        assert!(looks_like_quota_limit_error("weekly usage limit reached"));
        assert!(looks_like_quota_limit_error("RESOURCE_EXHAUSTED: quota exceeded"));
        assert!(looks_like_quota_limit_error("insufficient credits remaining"));
        assert!(!looks_like_quota_limit_error("Provider exited with code 1."));
        assert!(!looks_like_quota_limit_error(
            "An error occurred trying to start process 'codex.exe'"
        ));
    }

    /// Verified real message (job 20260815T141347Z_ce298f32, codex,
    /// stdout.log tail) — see bar/SYNC.md.
    const REAL_CODEX_USAGE_LIMIT_STDOUT: &str = r#"{"type":"thread.started","thread_id":"019fe5e1-ce57-7b83-9235-6ace526d929d"}
{"type":"turn.started"}
{"type":"error","message":"You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at Aug 20th, 2026 11:50 AM."}
{"type":"turn.failed","error":{"message":"You've hit your usage limit. Upgrade to Pro (https://chatgpt.com/explore/pro), visit https://chatgpt.com/codex/settings/usage to purchase more credits or try again at Aug 20th, 2026 11:50 AM."}}
"#;

    #[test]
    fn recognizes_the_real_codex_stdout_usage_limit_message_and_ignores_unrelated_output() {
        assert!(looks_like_quota_limit_output("codex", REAL_CODEX_USAGE_LIMIT_STDOUT));
        assert!(looks_like_quota_limit_output("grok", "HTTP 402 Payment Required"));
        assert!(looks_like_quota_limit_output("grok", "insufficient credits"));
        assert!(!looks_like_quota_limit_output(
            "codex",
            "failed to refresh available models: unexpected status 401 Unauthorized"
        ));
        assert!(!looks_like_quota_limit_output("grok", "Provider exited with code 1."));
    }

    #[test]
    fn extracts_the_human_reset_date_from_the_real_codex_message() {
        assert_eq!(
            extract_human_reset_date(REAL_CODEX_USAGE_LIMIT_STDOUT),
            Some("Aug 20th, 2026 11:50 AM".to_owned())
        );
        assert_eq!(
            extract_human_reset_date("resets January 3, 2027"),
            Some("January 3, 2027".to_owned())
        );
        assert_eq!(extract_human_reset_date("no date shape at all here"), None);
    }

    #[test]
    fn extracts_the_first_iso_date_with_an_optional_time_and_nothing_otherwise() {
        assert_eq!(
            extract_reset_date("usage limit reached; resets 2026-08-20"),
            Some("2026-08-20".to_owned())
        );
        assert_eq!(
            extract_reset_date("blocked until 2026-08-20T11:50:00Z per policy"),
            Some("2026-08-20T11:50:00Z".to_owned())
        );
        assert_eq!(
            extract_reset_date("blocked until 2026-08-20 11:50 per policy"),
            Some("2026-08-20 11:50".to_owned())
        );
        assert_eq!(extract_reset_date("Provider exited with code 1."), None);
        assert_eq!(extract_reset_date("no date shape at all here"), None);
    }
}
