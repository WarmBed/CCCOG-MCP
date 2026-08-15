//! Stable, allocation-owned C ABI for the CCCOG-Bar snapshot.
//!
//! The caller owns the returned string until it calls `ccog_bar_free_string`.
//! All parse failures are data-shaped JSON errors; they never cross the ABI as
//! a panic or process abort.

use cccog_bar_core::graph::GraphSnapshot;
use cccog_bar_core::quota::{
    fetch_claude_quota, fetch_grok_quota, load_claude_credential, load_grok_token, HttpClient,
    HttpRequest, HttpResponse, PollGate, QuotaCards,
};
use serde::Serialize;
use serde_json::{json, Value};
use std::ffi::{c_char, CStr, CString};
use std::path::Path;
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
    let Ok(mut client) = ReqwestHttpClient::new() else {
        return vec![];
    };
    let mut cards = Vec::new();
    if let Some(path) = claude_credential_path {
        if let Ok(Some(credential)) = load_claude_credential(path) {
            if let Some(card) = fetch_claude_quota(&mut client, &credential, now) {
                cards.push(card);
            }
        }
    }
    if let Some(path) = grok_auth_path {
        if let Ok(token) = load_grok_token(path) {
            if let Some(card) = fetch_grok_quota(&mut client, token.as_deref()) {
                cards.push(card);
            }
        }
    }
    cards
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
}

#[derive(Debug, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
struct QuotaPollInput {
    claude_credential_path: Option<String>,
    grok_auth_path: Option<String>,
    now: Option<u64>,
}

pub fn poll_remote_quotas_json(input: &str) -> String {
    let parsed = match serde_json::from_str::<QuotaPollInput>(input) {
        Ok(value) => value,
        Err(error) => return error_json(format!("quota input is malformed: {error}")),
    };
    let cards = poll_remote_quotas(
        parsed.claude_credential_path.as_deref().map(Path::new),
        parsed.grok_auth_path.as_deref().map(Path::new),
        parsed.now.unwrap_or(0),
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
