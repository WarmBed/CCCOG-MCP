use cccog_bar_core::usage::{
    dedupe_samples, parse_claude_jsonl, parse_codex_jsonl, parse_grok_jsonl, parse_incremental,
    Provider, UsageCursor,
};

#[test]
fn codex_usage_parses_input_output_and_cache_tokens() {
    let input = r#"{"type":"event_msg","timestamp":"2026-08-15T01:02:03Z","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":120,"output_tokens":30,"cached_input_tokens":40}}}}
{"type":"event_msg","payload":{"type":"rate_limits","rate_limits":{"used_percent":91}}}
"#;
    let parsed = parse_codex_jsonl(input, "thread-codex", "synthetic-rollout.jsonl");
    assert_eq!(parsed.samples.len(), 1);
    let sample = &parsed.samples[0];
    assert_eq!(sample.input_tokens, 120);
    assert_eq!(sample.output_tokens, 30);
    assert_eq!(sample.cache_read_tokens, 40);
    assert_eq!(sample.cache_write_tokens, 0);
}

#[test]
fn claude_usage_parses_assistant_message_variants() {
    let input = r#"{"type":"assistant","timestamp":"2026-08-15T01:02:03Z","sessionId":"claude-1","message":{"id":"msg-1","model":"claude-test","usage":{"input_tokens":100,"output_tokens":25,"cache_read_input_tokens":10,"cache_creation_input_tokens":3}}}
{"type":"assistant","message":{"id":"msg-2","model":"claude-test","usage":{"input_tokens":50,"output_tokens":5,"cacheReadInputTokens":2,"cacheCreationInputTokens":1}}}
"#;
    let parsed = parse_claude_jsonl(input, "claude-1", "synthetic-claude.jsonl");
    assert_eq!(parsed.samples.len(), 2);
    assert_eq!(parsed.samples[0].cache_write_tokens, 3);
    assert_eq!(parsed.samples[1].cache_read_tokens, 2);
}

#[test]
fn grok_usage_is_parsed_and_absent_usage_is_unknown() {
    let input = r#"{"ts":"2026-08-15T01:02:03Z","params":{"update":{"usage":{"inputTokens":80,"outputTokens":12,"cachedReadTokens":7,"cacheWriteTokens":2}}}}
{"type":"message","text":"no usage here"}
"#;
    let parsed = parse_grok_jsonl(input, "grok-1", "synthetic-grok.jsonl");
    assert_eq!(parsed.samples.len(), 1);
    assert_eq!(parsed.samples[0].input_tokens, 80);
    assert_eq!(parsed.samples[0].cache_read_tokens, 7);
    assert_eq!(parsed.samples[0].cache_write_tokens, 2);
}

#[test]
fn malformed_lines_are_skipped_and_dedup_is_stable() {
    let input = "not-json\n".to_owned()
        + r#"{"type":"assistant","message":{"id":"same","usage":{"input_tokens":4,"output_tokens":1}}}
"#;
    let first = parse_claude_jsonl(&input, "claude-1", "synthetic.jsonl");
    let repeated = parse_claude_jsonl(&input, "claude-1", "synthetic.jsonl");
    let mut all = first.samples;
    all.extend(repeated.samples);
    let unique = dedupe_samples(all);
    assert_eq!(unique.len(), 1);
    assert!(!first.diagnostics.is_empty());
}

#[test]
fn incremental_cursor_resets_after_truncate_and_reads_only_append() {
    let first = r#"{"type":"assistant","message":{"id":"one","usage":{"input_tokens":1,"output_tokens":1}}}
"#;
    let mut cursor = UsageCursor::default();
    let parsed = parse_incremental(
        Provider::Claude,
        first.as_bytes(),
        "claude-1",
        "synthetic.jsonl",
        &mut cursor,
    );
    assert_eq!(parsed.samples.len(), 1);
    let appended = format!(
        "{}{}",
        first,
        r#"{"type":"assistant","message":{"id":"two","usage":{"input_tokens":2,"output_tokens":1}}}
"#
    );
    let next = parse_incremental(
        Provider::Claude,
        appended.as_bytes(),
        "claude-1",
        "synthetic.jsonl",
        &mut cursor,
    );
    assert_eq!(next.samples.len(), 1);
    assert_eq!(next.samples[0].message_key, "two");
    let truncated = r#"{"type":"assistant","message":{"id":"reset","usage":{"input_tokens":8,"output_tokens":1}}}
"#;
    let reset = parse_incremental(
        Provider::Claude,
        truncated.as_bytes(),
        "claude-1",
        "synthetic.jsonl",
        &mut cursor,
    );
    assert_eq!(reset.samples[0].message_key, "reset");
}
