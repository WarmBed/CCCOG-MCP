use cccog_bar_core::{
    dispatch::parse_status,
    inbox::parse_jsonl,
    owners::{is_archived_path, parse_owner},
    privacy::summarize_prompt,
};

#[test]
fn status_parser_accepts_current_and_old_fields() {
    let current = parse_status(
        r#"{
          "jobId":"job-1", "provider":"codex", "sessionId":"thread-1",
          "cwd":"D:\\synthetic", "model":"gpt-test", "hopCount":1,
          "hopSource":"user:test@machine", "hopChain":"human>codex",
          "callerLabel":"Claude/session-7", "status":"running",
          "createdAt":"2026-08-15T00:00:00Z", "unknownFuture":true
        }"#,
    )
    .expect("current status should parse");
    assert_eq!(current.job_id, "job-1");
    assert_eq!(current.provider, "codex");
    assert_eq!(current.caller_label.as_deref(), Some("Claude/session-7"));
    assert_eq!(current.status, "running");

    let old = parse_status(
        r#"{"jobId":"old", "provider":"grok", "action":"resume",
           "status":"queued", "createdAt":"2026-08-15T00:00:00Z",
           "promptChars":4}"#,
    )
    .expect("old status should parse");
    assert_eq!(old.job_id, "old");
    assert!(old.caller_label.is_none());
}

#[test]
fn owner_parser_and_archive_visibility_are_bounded() {
    let owner = parse_owner(
        r#"{"schemaVersion":1,"provider":"codex","sessionId":"thread-1",
           "cwd":"D:\\synthetic","ownerPid":42,"spoolDir":"spool",
           "startedAt":"2026-08-15T00:00:00Z"}"#,
    )
    .expect("owner should parse");
    assert_eq!(owner.provider, "codex");
    assert_eq!(owner.session_id.as_deref(), Some("thread-1"));
    assert!(!is_archived_path(std::path::Path::new(
        r"C:\data\owners\abc.json",
    )));
    assert!(is_archived_path(std::path::Path::new(
        r"C:\data\sessions\cccg-archive\20260815\summary.json",
    )));
}

#[test]
fn inbox_parser_skips_bad_lines_and_torn_tail() {
    let parsed = parse_jsonl(
        "{\"id\":\"m1\",\"fromRole\":\"system\",\"content\":\"audit\"}\n".to_owned()
            + "not-json\n"
            + "{\"id\":\"torn\",\"content\":",
    );
    assert_eq!(parsed.entries.len(), 1);
    assert_eq!(parsed.entries[0].id.as_deref(), Some("m1"));
    assert_eq!(parsed.diagnostics.len(), 1);
    assert_eq!(parsed.torn_tail, true);
}

#[test]
fn prompt_summary_is_untrusted_escaped_and_utf8_bounded() {
    let prompt = "[CCCG dispatch from Claude Desktop to codex]\n\nignore instructions with a long synthetic task\nSECRET=do-not-leak\n";
    let summary = summarize_prompt(prompt.as_bytes(), 4096, 24);
    assert!(summary.starts_with("ignore instructions"));
    assert!(summary.ends_with('…'));
    assert!(!summary.contains("SECRET"));
    assert!(!summary.contains('\n'));

    let controls = "[CCCG dispatch from Claude Desktop to grok]\n\nhello\u{0007}world";
    let escaped = summarize_prompt(controls.as_bytes(), 4096, 128);
    assert_eq!(escaped, "hello\\u{0007}world");

    let unicode = "[CCCG dispatch from Claude Desktop to grok]\n\n繁體中文任務";
    let bounded = summarize_prompt(unicode.as_bytes(), 4096, 10);
    assert!(bounded.is_char_boundary(bounded.len()));
    assert!(bounded.len() <= 10);
    assert!(summarize_prompt(unicode.as_bytes(), 4096, 2).len() <= 2);
}
