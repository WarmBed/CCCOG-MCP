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

use chrono::{DateTime, Local, NaiveDate, NaiveDateTime, TimeZone, Utc};
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
    /// Short, one-line status text meant to sit under a card's window rows
    /// (operator direction, bar/SYNC.md: "at most ONE short status line" —
    /// `"stale · HTTP 429 · 14:13"`, `"limit · 08/20 11:50"`). Never a
    /// sentence.
    pub diagnostic: Option<String>,
    /// The fuller explanation `diagnostic` was condensed from (evidence
    /// source, the dispatch-failure's own time, the verbatim provider
    /// reset text) — never deleted, just moved out of the compact card
    /// line into here for a hover tooltip. `None` when `diagnostic` is
    /// already the whole story (nothing was trimmed off it).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub diagnostic_detail: Option<String>,
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

/// Task 18 (2026-08-18): abstraction over "read the raw JSON blob for a
/// named Windows Credential Manager generic credential" — injected so
/// fixtures never touch the real OS credential store, the same discipline
/// [`HttpClient`] already uses for the network boundary. `Ok(None)` means
/// no such credential exists in the store (the common case, and NOT an
/// error — most installs never write one); `Err` is a genuine read
/// failure. Implementations must be read-only: never write or delete a
/// credential, never log its contents.
pub trait CredentialStore {
    fn read_generic_credential(&self, target_name: &str) -> Result<Option<String>, String>;
}

/// The generic-credential service name some Windows Claude Code installs
/// (ones using a Node `keytar`-style native credential module rather than a
/// plain file) write their LIVE `claudeAiOauth` blob under — identical JSON
/// shape to `~/.claude/.credentials.json`. Reading it first, file second,
/// means a rotating refresh token that's kept current in the OS store isn't
/// silently shadowed by a stale on-disk snapshot the way task 17's
/// diagnosis found. NOT confirmed present on every Windows install — this
/// machine's own Claude Code install (a native binary under
/// `~/.local/bin`, not the npm/keytar package) has no such entry, verified
/// read-only via `cmdkey /list` while diagnosing this task; the file
/// fallback below is not a rare edge case; it's this machine's actual live
/// path today.
const CLAUDE_CODE_CREDENTIAL_SERVICE: &str = "Claude Code-credentials";

#[cfg(windows)]
pub struct WindowsCredentialManager;

#[cfg(windows)]
impl CredentialStore for WindowsCredentialManager {
    fn read_generic_credential(&self, target_name: &str) -> Result<Option<String>, String> {
        windows_credential_manager::read_generic_credential(target_name)
    }
}

/// A portability stub — this workspace only ships for Windows, but keeping
/// `cargo check` possible on any host (matching this crate's own existing
/// "no dependency on the flow/graph core" portability stance) costs nothing
/// here: there is no Credential Manager off Windows, so `Ok(None)` is
/// simply correct, not a placeholder pretending to work.
#[cfg(not(windows))]
pub struct WindowsCredentialManager;

#[cfg(not(windows))]
impl CredentialStore for WindowsCredentialManager {
    fn read_generic_credential(&self, _target_name: &str) -> Result<Option<String>, String> {
        Ok(None)
    }
}

/// Parses the `claudeAiOauth` JSON blob shape shared by BOTH sources (the
/// Windows Credential Manager entry and the on-disk file). A
/// present-but-logged-out blob — valid JSON, but no `accessToken` — returns
/// `Ok(None)`, not an error: TokenBar's own documented behavior for this
/// exact daily-logout state (a stored blob surviving after `claude logout`
/// or a session expiry with no token left in it), so the caller keeps
/// trying its next source instead of treating a present-but-empty blob as
/// "credentials broken."
fn parse_claude_oauth_blob(raw: &str) -> Result<Option<OAuthCredential>, String> {
    let value: Value =
        serde_json::from_str(raw).map_err(|_| "Claude OAuth JSON malformed".to_owned())?;
    let oauth = value
        .get("claudeAiOauth")
        .ok_or_else(|| "Claude OAuth entry missing".to_owned())?;
    let Some(access) = oauth
        .get("accessToken")
        .or_else(|| oauth.get("access_token"))
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|token| !token.is_empty())
    else {
        return Ok(None); // present but logged out — not an error
    };
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

fn load_claude_credential_from_file(path: &Path) -> Result<Option<OAuthCredential>, String> {
    if !path.is_file() {
        return Ok(None);
    }
    let raw =
        std::fs::read_to_string(path).map_err(|_| "cannot read Claude OAuth file".to_owned())?;
    parse_claude_oauth_blob(&raw)
}

/// Task 18: Windows Credential Manager first, the on-disk file second — the
/// injectable form, so fixtures supply a fake [`CredentialStore`] and never
/// touch the real OS store. Any Credential-Manager-layer outcome other than
/// "found a usable, logged-in blob" (absent, logged-out, malformed, or a
/// genuine read error) falls through to the file rather than failing —
/// the file is a real, independently-working fallback, so a raw
/// Credential-Manager hiccup is never more actionable to the caller than
/// just trying it.
pub fn load_claude_credential_with_store(
    store: &impl CredentialStore,
    path: &Path,
) -> Result<Option<OAuthCredential>, String> {
    if let Ok(Some(raw)) = store.read_generic_credential(CLAUDE_CODE_CREDENTIAL_SERVICE) {
        if let Ok(Some(credential)) = parse_claude_oauth_blob(&raw) {
            return Ok(Some(credential));
        }
    }
    load_claude_credential_from_file(path)
}

/// Read Claude's OAuth credential without modifying anything: Windows
/// Credential Manager first, `~/.claude/.credentials.json` second (see
/// [`load_claude_credential_with_store`] for the injectable/testable form
/// this delegates to). `expiresAt` is the CLI's millisecond timestamp and
/// is normalized to seconds for the poller.
pub fn load_claude_credential(path: &Path) -> Result<Option<OAuthCredential>, String> {
    load_claude_credential_with_store(&WindowsCredentialManager, path)
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
        windows.push(window("primary", &resolve_window_label(root, "Primary"), used, root));
    } else if let Some(object) = root.as_object() {
        for (id, child) in object {
            if let Some(used) = percent(child, "used_percent") {
                windows.push(window(id, &resolve_window_label(child, &label_for(id, child)), used, child));
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
        diagnostic_detail: None,
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
        return Some(stale_with_detail(
            "claude",
            "no quota data",
            Some("quota response contained no windows".to_owned()),
        ));
    }
    Some(QuotaCards {
        client_id: "claude".to_owned(),
        windows,
        state: QuotaState::Fresh,
        observed_at: None,
        diagnostic: None,
        diagnostic_detail: None,
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
        return Some(stale_with_detail(
            "grok",
            "no credit data",
            Some("quota response contained no credit usage".to_owned()),
        ));
    };
    Some(QuotaCards {
        client_id: "grok".to_owned(),
        windows: vec![window("credits", &resolve_window_label(&value, "Credits"), used, &value)],
        state: QuotaState::Fresh,
        observed_at: None,
        diagnostic: None,
        diagnostic_detail: None,
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
        .map(|(name, value, used)| {
            window(name, &resolve_window_label(value, &label_for(name, value)), used, value)
        })
        .collect()
}

fn window(id: &str, label: &str, used: f64, value: &Value) -> QuotaWindow {
    let used = clamp_percent(used);
    QuotaWindow {
        card_id: id.to_owned(),
        label: label.to_owned(),
        used_percent: used,
        remaining_percent: (100.0 - used).max(0.0),
        resets_at: value
            .get("resets_at")
            .and_then(reset_value)
            .map(|raw| normalize_reset_display(&raw)),
    }
}

// ── Window-label abbreviation: "five hour" -> "5h", never a spelled-out word ──
//
// The three quota cards previously showed each provider's own label text
// verbatim ("five hour", "seven day", "Primary") — never abbreviated, and
// inconsistent with each other. `resolve_window_label` is the single place
// that turns a genuine duration-shaped window label into `"Nh"`/`"Nd"` (hours
// under a day, days at or above it); a label that isn't duration-shaped at
// all (`"Credits"`, `"Limit"`) is left exactly as it was — abbreviating a
// non-duration word would just lose meaning, not compact it.
fn resolve_window_label(value: &Value, raw_label: &str) -> String {
    if let Some(minutes) = value.get("window_minutes").and_then(Value::as_f64) {
        if let Some(abbreviated) = abbreviate_duration_minutes(minutes) {
            return abbreviated;
        }
    }
    abbreviate_duration_text(raw_label).unwrap_or_else(|| raw_label.to_owned())
}

/// The reliable path: Codex's rate_limits JSON carries the window's exact
/// length in minutes, so the abbreviation comes straight from that number
/// instead of parsing whatever English text happens to be attached to it.
fn abbreviate_duration_minutes(minutes: f64) -> Option<String> {
    if !minutes.is_finite() || minutes <= 0.0 {
        return None;
    }
    let hours = minutes / 60.0;
    Some(if hours < 24.0 {
        format!("{}h", (hours.round().max(1.0)) as i64)
    } else {
        format!("{}d", ((hours / 24.0).round().max(1.0)) as i64)
    })
}

/// Fallback for providers with no `window_minutes` field (Claude's
/// `five_hour`/`seven_day` labels arrive as English text only): matches a
/// number word or digit followed by "hour(s)"/"day(s)" anywhere in the
/// label and abbreviates it. Returns `None` — leaving the original label
/// untouched — when the label doesn't read as a duration at all.
fn abbreviate_duration_text(label: &str) -> Option<String> {
    let lower = label.to_lowercase();
    let re = regex::Regex::new(
        r"(?i)\b(one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|\d+)[\s-]*(hour|day)s?\b",
    )
    .ok()?;
    let caps = re.captures(&lower)?;
    let number = match caps.get(1)?.as_str() {
        "one" => 1,
        "two" => 2,
        "three" => 3,
        "four" => 4,
        "five" => 5,
        "six" => 6,
        "seven" => 7,
        "eight" => 8,
        "nine" => 9,
        "ten" => 10,
        "eleven" => 11,
        "twelve" => 12,
        digits => digits.parse().ok()?,
    };
    let unit = if caps.get(2)?.as_str() == "hour" { "h" } else { "d" };
    Some(format!("{number}{unit}"))
}

fn reset_value(value: &Value) -> Option<String> {
    value
        .as_str()
        .map(str::to_owned)
        .or_else(|| value.as_u64().map(|seconds| seconds.to_string()))
        .or_else(|| value.as_f64().map(|seconds| (seconds as u64).to_string()))
}

// ── Per-window display line: the single row TokenBar's agent card shows ────
//
// Operator direction (bar/SYNC.md): each quota window renders as one row,
// `"<abbreviated window>  <NN%>  <date>"` — e.g. `"5h  7%  08/20 11:50"`,
// `"7d  32%  08/22"` — with the gauge bar for that window drawn alongside.
// `format_window_line` is the single place that composes it so the C# view
// stays display-only (no string-building on that side): `label` is expected
// already abbreviated (`resolve_window_label`) and `resets_display` already
// normalized (`normalize_reset_display`) — this function only composes the
// three pieces, it does not itself abbreviate or parse anything.
pub fn format_window_line(label: &str, used_percent: f64, resets_display: Option<&str>) -> String {
    let percent = format_percent_compact(used_percent);
    let date = resets_display
        .filter(|value| !value.is_empty())
        .unwrap_or("Reset time unavailable");
    format!("{label}  {percent}  {date}")
}

/// Whole percent when it rounds cleanly (`"7%"`), one decimal otherwise
/// (`"6.3%"`) — the same trimming `{:0.#}` gives on the C# side, done once
/// here instead of twice.
fn format_percent_compact(value: f64) -> String {
    let rounded = (value * 10.0).round() / 10.0;
    if rounded.fract().abs() < 1e-9 {
        format!("{}%", rounded as i64)
    } else {
        format!("{rounded:.1}%")
    }
}

// ── Reset-time normalization: one display format for every provider ────────
//
// Codex (epoch seconds, from its rate_limits JSON), Claude (ISO 8601 with a
// `Z` offset, from the oauth/usage response), and the job-failure override's
// free-text extraction (`extract_reset_date`'s ISO substrings,
// `extract_human_reset_date`'s English "Aug 20th, 2026 11:50 AM" shape) each
// arrive in a different shape. Before this normalizer they were displayed
// verbatim per shape, so the three quota cards showed visibly inconsistent
// reset times. `normalize_reset_display` is the single place that parses all
// of them and renders one local-time string: bare `"MM/DD HH:MM"` (24h,
// no leading word — the card's own layout already labels this as the reset
// time), or bare `"MM/DD"` when the source carried no time-of-day. A shape
// not covered below is returned completely unchanged — never a reformatted
// guess, matching the "raw and honest" rule the rest of this crate already
// follows for reset dates (see `extract_reset_date`/`extract_human_reset_date`
// above).
//
// A source with no explicit UTC offset (a bare epoch, or a naive
// "YYYY-MM-DD[T ]HH:MM[:SS]") is treated as UTC before converting to the
// display's local time — the epoch case is unambiguous, and the naive-ISO
// case carries no offset information to do otherwise with; this is a
// deliberate, documented assumption, not a silent guess.
//
// The English "Mon D, YYYY H:MM AM/PM" shape (`extract_human_reset_date`'s
// output) is the ONE exception: it is treated as ALREADY LOCAL, format-only,
// never shifted — see `parse_reset_instant`'s own doc comment for why
// (Codex CLI emits that exact shape in the host's own local time, not UTC;
// live-confirmed 2026-08-16/17 after a real reset rendered 8 hours off).
pub fn normalize_reset_display(raw: &str) -> String {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return raw.to_owned();
    }
    if let Some(instant) = parse_reset_instant(trimmed) {
        return instant.with_timezone(&Local).format("%m/%d %H:%M").to_string();
    }
    if let Some(date) = parse_reset_date_only(trimmed) {
        return date.format("%m/%d").to_string();
    }
    raw.to_owned()
}

/// Every shape that carries a time-of-day: RFC3339/ISO-8601 (with offset or
/// `Z`), a bare epoch-seconds numeral, a naive `YYYY-MM-DD[T ]HH:MM[:SS]`
/// (assumed UTC — see the module comment above), and the English
/// "Mon D[st|nd|rd|th], YYYY H:MM AM/PM" shape `extract_human_reset_date`
/// extracts.
///
/// **Zone handling differs by shape, and this is deliberate, not an
/// oversight**: RFC3339/epoch carry an explicit or unambiguous absolute
/// instant, converted to local for display as always. The naive
/// `YYYY-MM-DD[T ]HH:MM[:SS]` shape keeps the existing UTC assumption
/// (unchanged). The English "Mon D, YYYY H:MM AM/PM" shape is different:
/// it is treated as ALREADY LOCAL, not UTC — live-confirmed 2026-08-16/17
/// (jobs `20260816T162448Z_07d41b5f`/`...17e490df`): Codex CLI's own
/// usage-limit message ("...try again at Aug 20th, 2026 11:50 AM") is
/// emitted in the machine's own local time, not UTC. The old
/// UTC-then-convert-to-local handling shifted a real "11:50 AM" reset into
/// a wrong "19:50" on this +8h machine. Public so `cccog-bar-ffi` can
/// derive a real, correctly-timezoned `DateTime<Utc>` from the SAME parse
/// this function's callers use for display — one correct instant, used
/// consistently for both "what time does this show" and "has this instant
/// passed yet".
pub fn parse_reset_instant(raw: &str) -> Option<DateTime<Utc>> {
    if let Ok(dt) = DateTime::parse_from_rfc3339(raw) {
        return Some(dt.with_timezone(&Utc));
    }
    if let Ok(seconds) = raw.parse::<i64>() {
        if let Some(dt) = DateTime::<Utc>::from_timestamp(seconds, 0) {
            return Some(dt);
        }
    }
    for format in [
        "%Y-%m-%dT%H:%M:%S",
        "%Y-%m-%d %H:%M:%S",
        "%Y-%m-%dT%H:%M",
        "%Y-%m-%d %H:%M",
    ] {
        if let Ok(naive) = NaiveDateTime::parse_from_str(raw, format) {
            return Some(Utc.from_utc_datetime(&naive));
        }
    }
    let stripped = strip_ordinal_suffix(raw);
    for format in ["%b %d, %Y %I:%M %p", "%B %d, %Y %I:%M %p"] {
        if let Ok(naive) = NaiveDateTime::parse_from_str(&stripped, format) {
            // Interpret as LOCAL wall-clock time (not UTC) so the eventual
            // `.with_timezone(&Local)` display round-trips back to the
            // exact same numbers instead of shifting by the host's UTC
            // offset. `.earliest()` resolves the ordinary (Single) and
            // DST-Ambiguous cases identically to picking the
            // earlier-occurring instant; only the theoretically possible
            // DST spring-forward Gap case (this exact naive time never
            // occurs locally) falls through to a UTC-equivalent instant as
            // a defensive, never-panic default — real quota reset times
            // essentially never land in that narrow gap.
            let local = Local
                .from_local_datetime(&naive)
                .earliest()
                .unwrap_or_else(|| DateTime::<Utc>::from_naive_utc_and_offset(naive, Utc).with_timezone(&Local));
            return Some(local.with_timezone(&Utc));
        }
    }
    None
}

/// Date-only shapes: a bare `YYYY-MM-DD`, or an English date with no time
/// component ("January 3, 2027"). Deliberately never assumes midnight to
/// synthesize a hh:mm — a date-only source stays date-only in the display.
fn parse_reset_date_only(raw: &str) -> Option<NaiveDate> {
    if let Ok(date) = NaiveDate::parse_from_str(raw, "%Y-%m-%d") {
        return Some(date);
    }
    let stripped = strip_ordinal_suffix(raw);
    for format in ["%b %d, %Y", "%B %d, %Y"] {
        if let Ok(date) = NaiveDate::parse_from_str(&stripped, format) {
            return Some(date);
        }
    }
    None
}

/// Strips an ordinal suffix ("20th" -> "20") so chrono's `%d` can match the
/// English shape `extract_human_reset_date` returns verbatim.
fn strip_ordinal_suffix(raw: &str) -> String {
    let Ok(re) = regex::Regex::new(r"(?i)(\d+)(st|nd|rd|th)\b") else {
        return raw.to_owned();
    };
    re.replace_all(raw, "$1").into_owned()
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
    stale_with_detail(client_id, diagnostic, None)
}

/// Same as `stale`, but the on-card line and its fuller explanation are
/// given separately: `diagnostic` is the short one-liner (operator
/// direction, bar/SYNC.md — "status · optional-time" grammar, never a
/// sentence), `detail` is the full sentence it was condensed from, kept for
/// a hover tooltip instead of being dropped.
fn stale_with_detail(
    client_id: &str,
    diagnostic: impl Into<String>,
    detail: Option<String>,
) -> QuotaCards {
    QuotaCards {
        client_id: client_id.to_owned(),
        windows: Vec::new(),
        state: QuotaState::Stale,
        observed_at: None,
        diagnostic: Some(diagnostic.into()),
        diagnostic_detail: detail,
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
        diagnostic_detail: None,
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

/// Task 18 (2026-08-18): the real `CredReadW` boundary — kept in its own
/// module so the unsafe FFI surface is small and reviewable in one place.
/// Read-only: only ever calls `CredReadW`/`CredFree`; never `CredWriteW`/
/// `CredDeleteW`. Never logs the blob it reads (only the parsed
/// `claudeAiOauth` structure flows further, and even that never through a
/// log line — same discipline the file-based path already followed).
#[cfg(windows)]
mod windows_credential_manager {
    use windows_sys::Win32::Security::Credentials::{
        CredFree, CredReadW, CRED_TYPE_GENERIC, CREDENTIALW,
    };

    pub(super) fn read_generic_credential(target_name: &str) -> Result<Option<String>, String> {
        let wide_target: Vec<u16> = target_name.encode_utf16().chain(std::iter::once(0)).collect();
        let mut credential_ptr: *mut CREDENTIALW = std::ptr::null_mut();
        // SAFETY: `wide_target` is a valid null-terminated UTF-16 string,
        // live for the duration of this call. `credential_ptr` is a valid
        // out-pointer. On success `CredReadW` allocates its own buffer,
        // freed below via `CredFree` before this function returns.
        let ok = unsafe { CredReadW(wide_target.as_ptr(), CRED_TYPE_GENERIC, 0, &mut credential_ptr) };
        if ok == 0 {
            // The overwhelmingly common case is ERROR_NOT_FOUND (no such
            // credential). Every other CredReadW failure is treated the
            // same way — Ok(None) — since the caller always has a working
            // file fallback; a raw OS error code here is never more
            // actionable than just trying that fallback.
            return Ok(None);
        }
        // SAFETY: `ok != 0` guarantees `credential_ptr` is a valid,
        // CredReadW-allocated `CREDENTIALW` until the `CredFree` call below.
        let blob = unsafe {
            let credential = &*credential_ptr;
            decode_credential_blob(credential.CredentialBlob, credential.CredentialBlobSize)
        };
        // SAFETY: `credential_ptr` was allocated by the `CredReadW` call
        // above and has not been freed yet.
        unsafe { CredFree(credential_ptr as *const _) };
        Ok(blob)
    }

    /// `CredentialBlob` carries no encoding metadata at the Win32 API level.
    /// Node's `keytar` (the common path for a JS app to write a Windows
    /// Credential Manager entry) stores the value as UTF-16LE, treating it
    /// as a wide "password" string — tried first; a raw UTF-8 byte
    /// interpretation is the fallback, since nothing in the API itself pins
    /// the encoding and this reader can't assume which native module (if
    /// any) actually wrote the entry.
    fn decode_credential_blob(blob: *mut u8, size: u32) -> Option<String> {
        if blob.is_null() || size == 0 {
            return None;
        }
        // SAFETY: `blob` points to `size` valid bytes owned by the
        // `CREDENTIALW` this was read from, for the lifetime of this call
        // (before the caller's `CredFree`).
        let bytes = unsafe { std::slice::from_raw_parts(blob, size as usize) };
        if size % 2 == 0 {
            let utf16: Vec<u16> = bytes
                .chunks_exact(2)
                .map(|pair| u16::from_le_bytes([pair[0], pair[1]]))
                .collect();
            if let Ok(text) = String::from_utf16(&utf16) {
                let trimmed = text.trim_end_matches('\0').to_owned();
                if !trimmed.is_empty() {
                    return Some(trimmed);
                }
            }
        }
        std::str::from_utf8(bytes)
            .ok()
            .map(str::to_owned)
            .filter(|text| !text.is_empty())
    }
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

#[cfg(test)]
mod reset_display_tests {
    use super::*;

    /// Builds the same string the implementation would, for a UTC instant —
    /// deterministic on any machine (both sides run through the same
    /// `Local` conversion) without hardcoding a host timezone offset.
    fn expected(utc: DateTime<Utc>) -> String {
        utc.with_timezone(&Local).format("%m/%d %H:%M").to_string()
    }

    /// Direct coverage of `parse_reset_instant` (not just through the
    /// display formatter above) — this is the function `cccog-bar-ffi`
    /// calls to get a real, comparable `DateTime<Utc>` for the
    /// persist-until-T quota-limit logic, so its zone handling has to be
    /// right independent of how `normalize_reset_display` happens to
    /// render it.
    #[test]
    fn parse_reset_instant_treats_iso_as_utc_and_english_as_local() {
        let iso = parse_reset_instant("2026-08-20T11:50:00Z").unwrap();
        assert_eq!(iso, Utc.with_ymd_and_hms(2026, 8, 20, 11, 50, 0).unwrap());

        let english = parse_reset_instant("Aug 20th, 2026 11:50 AM").unwrap();
        // The English shape is local-time; converting it BACK to local for
        // display must reproduce the exact same wall-clock numbers.
        let local_display = english.with_timezone(&Local).format("%Y-%m-%d %H:%M").to_string();
        assert_eq!(local_display, "2026-08-20 11:50");
    }

    #[test]
    fn normalizes_iso8601_with_and_without_z() {
        let want = expected(Utc.with_ymd_and_hms(2026, 8, 20, 11, 50, 0).unwrap());
        assert_eq!(normalize_reset_display("2026-08-20T11:50:00Z"), want);
        assert_eq!(normalize_reset_display("2026-08-20T11:50:00"), want);
        assert_eq!(normalize_reset_display("2026-08-20 11:50"), want);
    }

    #[test]
    fn normalizes_epoch_seconds() {
        let want = expected(DateTime::<Utc>::from_timestamp(1787197821, 0).unwrap());
        assert_eq!(normalize_reset_display("1787197821"), want);
    }

    #[test]
    fn normalizes_english_verbatim_dates_with_and_without_a_time() {
        // Unlike every other case in this module, this shape is NOT run
        // through the UTC->Local conversion `expected()` above applies —
        // it is treated as already-local and rendered verbatim, so the
        // expected output is the exact same wall-clock numbers regardless
        // of the test machine's own timezone (task 6 addendum,
        // 2026-08-16/17: Codex CLI emits this exact shape in local time;
        // the old UTC-then-convert behavior shifted a real "11:50 AM"
        // reset into "19:50" on a +8h machine).
        assert_eq!(normalize_reset_display("Aug 20th, 2026 11:50 AM"), "08/20 11:50");
        assert_eq!(normalize_reset_display("January 3, 2027"), "01/03");
    }

    #[test]
    fn normalizes_a_bare_date_to_date_only_never_inventing_a_time() {
        assert_eq!(normalize_reset_display("2026-08-20"), "08/20");
    }

    #[test]
    fn genuinely_unparseable_input_passes_through_verbatim() {
        assert_eq!(normalize_reset_display("unknown"), "unknown");
        assert_eq!(
            normalize_reset_display("no date shape at all here"),
            "no date shape at all here"
        );
        assert_eq!(normalize_reset_display(""), "");
    }
}

#[cfg(test)]
mod window_label_tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn abbreviates_from_window_minutes_when_present() {
        let five_hour = json!({"window_minutes": 300});
        assert_eq!(resolve_window_label(&five_hour, "whatever text"), "5h");
        let seven_day = json!({"window_minutes": 10080});
        assert_eq!(resolve_window_label(&seven_day, "whatever text"), "7d");
        let one_hour = json!({"window_minutes": 60});
        assert_eq!(resolve_window_label(&one_hour, "one hour"), "1h");
    }

    #[test]
    fn abbreviates_english_duration_text_when_no_window_minutes() {
        let empty = json!({});
        assert_eq!(resolve_window_label(&empty, "five hour"), "5h");
        assert_eq!(resolve_window_label(&empty, "seven day"), "7d");
        assert_eq!(resolve_window_label(&empty, "seven day oauth apps"), "7d");
        assert_eq!(resolve_window_label(&empty, "10 hour"), "10h");
    }

    #[test]
    fn leaves_non_duration_labels_untouched() {
        let empty = json!({});
        assert_eq!(resolve_window_label(&empty, "Credits"), "Credits");
        assert_eq!(resolve_window_label(&empty, "Limit"), "Limit");
    }
}

#[cfg(test)]
mod window_line_tests {
    use super::*;

    #[test]
    fn composes_the_locked_row_order_with_double_spaces() {
        assert_eq!(
            format_window_line("5h", 7.0, Some("08/20 11:50")),
            "5h  7%  08/20 11:50"
        );
        assert_eq!(format_window_line("7d", 32.0, Some("08/22")), "7d  32%  08/22");
    }

    #[test]
    fn keeps_a_fractional_percent_to_one_decimal() {
        assert_eq!(
            format_window_line("5h", 6.3, Some("08/20 11:50")),
            "5h  6.3%  08/20 11:50"
        );
    }

    #[test]
    fn missing_reset_time_renders_the_honest_placeholder_never_a_blank() {
        assert_eq!(
            format_window_line("Credits", 10.0, None),
            "Credits  10%  Reset time unavailable"
        );
        assert_eq!(
            format_window_line("Credits", 10.0, Some("")),
            "Credits  10%  Reset time unavailable"
        );
    }
}
