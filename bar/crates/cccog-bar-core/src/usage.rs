//! Bounded, provider-specific token usage parsers.
//!
//! These parsers deliberately consume only explicit usage counters.  They do
//! not estimate tokens from text and contain no pricing or money fields.

use serde_json::Value;
use std::collections::HashSet;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Provider {
    Codex,
    Claude,
    Grok,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UsageSample {
    pub provider: Provider,
    pub session_id: String,
    pub message_key: String,
    pub timestamp: Option<String>,
    pub model: Option<String>,
    pub input_tokens: u64,
    pub output_tokens: u64,
    pub cache_read_tokens: u64,
    pub cache_write_tokens: u64,
    pub source_path: String,
    pub source_offset: usize,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UsageParse {
    pub samples: Vec<UsageSample>,
    pub diagnostics: Vec<String>,
    pub next_offset: usize,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UsageCursor {
    pub byte_offset: usize,
    pub file_len: usize,
}

pub fn parse_codex_jsonl(input: &str, session_id: &str, source_path: &str) -> UsageParse {
    parse_text(Provider::Codex, input, session_id, source_path, 0)
}

pub fn parse_claude_jsonl(input: &str, session_id: &str, source_path: &str) -> UsageParse {
    parse_text(Provider::Claude, input, session_id, source_path, 0)
}

pub fn parse_grok_jsonl(input: &str, session_id: &str, source_path: &str) -> UsageParse {
    parse_text(Provider::Grok, input, session_id, source_path, 0)
}

/// Parse only bytes appended since the previous cursor.  A shorter file is a
/// rewrite/truncate and starts from byte zero.  The cursor is advanced even
/// when the final line is malformed, so a later poll does not repeatedly scan
/// a bad record.
pub fn parse_incremental(
    provider: Provider,
    bytes: &[u8],
    session_id: &str,
    source_path: &str,
    cursor: &mut UsageCursor,
) -> UsageParse {
    let start = if bytes.len() < cursor.file_len || cursor.byte_offset > bytes.len() {
        0
    } else {
        cursor.byte_offset
    };
    let mut offset = start;
    if start > 0 && start < bytes.len() && bytes[start - 1] != b'\n' {
        if let Some(relative) = bytes[start..].iter().position(|byte| *byte == b'\n') {
            offset = start + relative + 1;
        } else {
            cursor.byte_offset = bytes.len();
            cursor.file_len = bytes.len();
            return UsageParse {
                next_offset: bytes.len(),
                ..UsageParse::default()
            };
        }
    }

    let text = String::from_utf8_lossy(&bytes[offset..]);
    let parsed = parse_text(provider, &text, session_id, source_path, offset);
    cursor.byte_offset = bytes.len();
    cursor.file_len = bytes.len();
    parsed
}

pub fn dedupe_samples(samples: Vec<UsageSample>) -> Vec<UsageSample> {
    let mut seen = HashSet::new();
    samples
        .into_iter()
        .filter(|sample| {
            seen.insert(format!(
                "{:?}\u{1f}|{}\u{1f}{}\u{1f}{}",
                sample.provider, sample.session_id, sample.message_key, sample.source_path
            ))
        })
        .collect()
}

fn parse_text(
    provider: Provider,
    input: &str,
    session_id: &str,
    source_path: &str,
    base_offset: usize,
) -> UsageParse {
    let mut result = UsageParse {
        next_offset: base_offset + input.len(),
        ..UsageParse::default()
    };
    let mut byte_offset = base_offset;
    for line in input.split_inclusive('\n') {
        let line_without_newline = line.strip_suffix('\n').unwrap_or(line);
        let line_without_newline = line_without_newline
            .strip_suffix('\r')
            .unwrap_or(line_without_newline);
        if line_without_newline.trim().is_empty() {
            byte_offset += line.len();
            continue;
        }
        match serde_json::from_str::<Value>(line_without_newline) {
            Ok(value) => {
                if let Some(sample) =
                    sample_from_value(provider, &value, session_id, source_path, byte_offset)
                {
                    result.samples.push(sample);
                }
            }
            Err(_) => result.diagnostics.push(format!(
                "invalid {:?} usage JSON at byte {}",
                provider, byte_offset
            )),
        }
        byte_offset += line.len();
    }
    result
}

fn sample_from_value(
    provider: Provider,
    value: &Value,
    session_id: &str,
    source_path: &str,
    source_offset: usize,
) -> Option<UsageSample> {
    let usage = match provider {
        Provider::Codex => value
            .get("payload")
            .and_then(|payload| payload.get("info"))
            .and_then(|info| {
                info.get("last_token_usage")
                    .or_else(|| info.get("total_token_usage"))
            })
            .or_else(|| value.get("usage")),
        Provider::Claude => value
            .get("message")
            .and_then(|message| message.get("usage"))
            .or_else(|| value.get("usage")),
        Provider::Grok => value
            .get("params")
            .and_then(|params| params.get("update"))
            .and_then(|update| update.get("usage"))
            .or_else(|| value.get("usage")),
    }?;

    let input_tokens = number(usage, &["input_tokens", "inputTokens"]);
    let output_tokens = number(usage, &["output_tokens", "outputTokens"]);
    let cache_read_tokens = number(
        usage,
        &[
            "cached_input_tokens",
            "cache_read_input_tokens",
            "cacheReadInputTokens",
            "cachedReadTokens",
        ],
    );
    let cache_write_tokens = number(
        usage,
        &[
            "cache_creation_input_tokens",
            "cache_write_input_tokens",
            "cacheCreationInputTokens",
            "cacheWriteTokens",
        ],
    );
    if input_tokens == 0 && output_tokens == 0 && cache_read_tokens == 0 && cache_write_tokens == 0
    {
        return None;
    }

    let message = value.get("message");
    let payload = value.get("payload");
    let key = first_string(
        value,
        &[
            "id",
            "messageId",
            "message_id",
            "requestId",
            "request_id",
            "uuid",
        ],
    )
    .or_else(|| message.and_then(|m| first_string(m, &["id", "messageId", "requestId"])))
    .or_else(|| payload.and_then(|p| first_string(p, &["id", "turn_id", "turnId"])))
    .unwrap_or_else(|| format!("offset:{}", source_offset));
    let timestamp = first_string(value, &["timestamp", "ts", "createdAt", "created_at"]);
    let model = message
        .and_then(|m| first_string(m, &["model"]))
        .or_else(|| first_string(value, &["model", "model_name", "modelName"]));

    Some(UsageSample {
        provider,
        session_id: session_id.to_owned(),
        message_key: key,
        timestamp,
        model,
        input_tokens,
        output_tokens,
        cache_read_tokens,
        cache_write_tokens,
        source_path: source_path.to_owned(),
        source_offset,
    })
}

fn first_string(value: &Value, fields: &[&str]) -> Option<String> {
    fields
        .iter()
        .find_map(|field| value.get(*field).and_then(Value::as_str).map(str::to_owned))
}

fn number(value: &Value, fields: &[&str]) -> u64 {
    fields
        .iter()
        .find_map(|field| {
            value.get(*field).and_then(|value| {
                value
                    .as_u64()
                    .or_else(|| value.as_i64().and_then(|number| u64::try_from(number).ok()))
                    .or_else(|| value.as_str().and_then(|number| number.parse::<u64>().ok()))
            })
        })
        .unwrap_or(0)
}
