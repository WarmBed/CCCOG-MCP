//! Provider quota cards with an injected, tightly bounded HTTP boundary.
//!
//! The core never owns a network implementation.  Production will provide a
//! read-only client; tests provide a recorder/fake and therefore never contact
//! provider endpoints.

use serde_json::Value;
use std::path::Path;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum QuotaState {
    Fresh,
    Stale,
    Unavailable,
}

#[derive(Debug, Clone, PartialEq)]
pub struct QuotaWindow {
    pub card_id: String,
    pub label: String,
    pub used_percent: f64,
    pub remaining_percent: f64,
    pub resets_at: Option<String>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct QuotaCards {
    pub client_id: String,
    pub windows: Vec<QuotaWindow>,
    pub state: QuotaState,
    pub observed_at: Option<u64>,
    pub diagnostic: Option<String>,
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
}

impl HttpResponse {
    pub fn json(status: u16, body: &str) -> Self {
        Self {
            status,
            body: body.to_owned(),
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
    })
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
            let refresh = client.send(HttpRequest {
                method: "POST".to_owned(),
                url: "https://platform.claude.com/v1/oauth/token".to_owned(),
                bearer: None,
                body: Some(format!("refresh_token={}", refresh_token)),
            });
            let Ok(response) = refresh else {
                return Some(stale("claude", "OAuth refresh failed"));
            };
            if response.status != 200 {
                return Some(stale("claude", "OAuth refresh rejected"));
            }
            let Ok(body) = serde_json::from_str::<Value>(&response.body) else {
                return Some(stale("claude", "OAuth refresh response malformed"));
            };
            let Some(token) = body.get("access_token").and_then(Value::as_str) else {
                return Some(stale("claude", "OAuth refresh returned no access token"));
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
        return Some(stale("claude", format!("quota HTTP {}", response.status)));
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
        return Some(stale("grok", format!("quota HTTP {}", response.status)));
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
        resets_at: value
            .get("resets_at")
            .and_then(Value::as_str)
            .map(str::to_owned),
    }
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
    }
}

fn unavailable(client_id: &str, diagnostic: impl Into<String>) -> QuotaCards {
    QuotaCards {
        client_id: client_id.to_owned(),
        windows: Vec::new(),
        state: QuotaState::Unavailable,
        observed_at: None,
        diagnostic: Some(diagnostic.into()),
    }
}
