//! Live local Claude Code sessions (and their subagents) as a second Flow
//! data source, alongside CCCG dispatch jobs.
//!
//! Mechanism ported from TokenBar-Windows `crates/tb_core_ffi/src/usage_tail.rs`
//! (read-only reference, not modified): a stat sweep finds candidate
//! transcript files, only files whose mtime falls inside the alive window
//! plus a small margin are actually re-parsed, and liveness is judged from
//! EVENT TIMESTAMPS inside the transcript (`timestamp` field on each JSONL
//! line), not file mtime alone. `ClaudeSessionTailer` (bottom of this file)
//! adds the skip-parse shortcut: if the newest source mtime across the whole
//! `projects/` tree hasn't moved since the last tick, the tick reuses the
//! previous snapshot-replaced result untouched — mirroring
//! `UsageTailer::last_source_token`.
//!
//! ## What real transcripts on this machine actually look like
//!
//! Investigated three live sessions under
//! `C:\Users\mike2\.claude\projects\<project-slug>\<session-uuid>.jsonl`
//! (project-slug is the cwd with `/`/`\`/`:` replaced by `-`, e.g.
//! `d--code-openruterati`). Every line is one JSON object; ~40 distinct
//! `type` values were observed (`message`, `assistant`, `user`, `text`,
//! `tool_use`, `tool_result`, `attachment`, `queue-operation`,
//! `custom-title`, `agent-name`, `mode`, `system`, …). This module only
//! keys on a handful of fields that are present across that whole zoo (via
//! `#[serde(default)]` — unknown fields/types are silently ignored, exactly
//! like `dispatch::DispatchStatus`'s "forward compatibility" convention):
//!
//! - `timestamp`: RFC3339 with `Z`, e.g. `"2026-08-16T07:45:19.106Z"` — present
//!   on essentially every substantive line (`user`/`assistant`/`attachment`
//!   entries all carry it; some bookkeeping lines like `queue-operation` do
//!   too). This is the liveness signal, never file mtime alone.
//! - `isSidechain` (bool): true on lines belonging to a Task-tool subagent's
//!   OWN transcript file (see below) — never observed true in a top-level
//!   session transcript on this machine, so it is recorded but the
//!   session/agent split in practice comes from *which file* a line is in,
//!   not this flag; kept because the task spec calls it out explicitly and a
//!   future transcript shape may rely on it.
//! - `message.model` (string, e.g. `"claude-sonnet-5"`): present on
//!   `assistant` lines only (no `model` on `user` lines).
//! - `type":"custom-title"` + `customTitle` (string): the session's
//!   user-facing title, re-emitted verbatim on (apparently) every turn — the
//!   *last* occurrence in the file is the current title. This turned out to
//!   BE the title store the task asked to look for: there is no separate
//!   sqlite/json title index anywhere under `~/.claude` (checked: no `*.db`/
//!   `*.sqlite*`, `history.jsonl` only has prompt text keyed by session id,
//!   `sessions/*.json` are PID-lock files). The title lives inline in the
//!   transcript itself, so no second file needs to be read.
//! - `message.content` (String, for a `user` line): the raw text of an
//!   inbound cross-session message looks like `<cross-session-message
//!   from="local_<uuid>" name="<title>" encoded="1">...</cross-session-message>`
//!   — `name` is the SENDING session's own title text (confirmed against
//!   real transcripts: `from`'s `local_<uuid>` does NOT match any
//!   session-uuid `.jsonl` filename on this machine, it's a separate
//!   peer-id namespace: matching has to go through `name` against another
//!   session's resolved title, not `from`'s id). **A second, structurally
//!   identical tag name also exists in the wild**:
//!   `<desktop-session-message from="..." name="..." ...>` — used when the
//!   sender is a Claude Desktop session (`entrypoint":"claude-desktop"`)
//!   rather than a CCD-managed one; found the hard way (2026-08-16
//!   live-run task 3: "Doc1 主管" → "doc5 Api文件檢查" rendered nowhere in
//!   the tree because the extractor only recognized the first tag name).
//!   `extract_cross_session_name` therefore matches on the shared
//!   `session-message` substring and walks back to its enclosing tag,
//!   rather than hardcoding one literal name — the JSON string's `\"`
//!   escapes are already fully decoded by the time that function sees the
//!   text (`serde_json` parses the whole line first; `message.content`
//!   arrives as a plain `Value::String` with real `"` characters, not
//!   escaped ones — there is no raw/undecoded text path here to regress
//!   into).
//!
//! Subagents: `<project-slug>/<session-uuid>/subagents/agent-<id>.jsonl`,
//! each with a sibling `agent-<id>.meta.json` (`{"agentType":...,
//! "description":...,"toolUseId":...,"spawnDepth":1,"model":"sonnet"}`).
//! Confirmed present and actively growing on three different real sessions
//! (including this very run — `agent-a53a...jsonl`'s tail is this
//! conversation, mid-write, at the time this was checked).

use crate::privacy::summarize_prompt;
use chrono::{DateTime, Utc};
use serde::Deserialize;
use serde_json::Value;
use std::collections::HashMap;
use std::path::Path;
use std::sync::Mutex;

/// A session (or agent) with no event in this many seconds is not "alive" —
/// the operator's own threshold ("A session is ALIVE if it has transcript
/// events within the last 10 minutes").
pub const CLAUDE_ALIVE_WINDOW_SECS: i64 = 600;

/// TokenBar's `usage_tail.rs` margin (300s) on top of the window, for clock
/// skew / write latency between "event timestamp" and "file mtime" — the
/// stat-sweep candidate filter uses `window + margin`, never the bare
/// window, so a file that just barely still matters is never skipped.
const MTIME_MARGIN_SECS: i64 = 300;

/// A very recent event (this fresh) reads as "actively doing a turn right
/// now" (執行中); anything older but still inside the alive window reads as
/// "open, idle" (閒置). Deliberately the same threshold the presence-file
/// resolver uses for its own "fresh" activity event, so the C-layer and the
/// A/B (presence) layer never disagree about what "fresh" means.
pub const RUNNING_FRESH_SECS: i64 = 60;

/// Bounded tail read per transcript file — bounds parse cost per file
/// regardless of total session length (some observed transcripts are
/// several MB / tens of thousands of lines); `custom-title` and activity
/// lines both recur near the end of a live file in every transcript
/// inspected, so a tail read does not lose the signal this module needs.
const TRANSCRIPT_TAIL_MAX_BYTES: u64 = 256 * 1024;

pub const RUNNING_STATE: &str = "執行中";
pub const IDLE_STATE: &str = "閒置";

// ── Pure parsing: one JSONL line → the handful of fields this module uses ──

#[derive(Debug, Clone, Default)]
pub struct ParsedEvent {
    pub timestamp: Option<DateTime<Utc>>,
    pub is_sidechain: bool,
    pub model: Option<String>,
    /// Only `Some` for a `type":"custom-title"` line.
    pub custom_title: Option<String>,
    /// The `name` attribute of an inbound `<cross-session-message>` /
    /// `<desktop-session-message>` tag found in this line's message
    /// content, if any.
    pub cross_session_from_name: Option<String>,
    /// True when the tag's BODY (the text right after its own `>`) starts
    /// with a dispatch marker — see `body_has_dispatch_marker`. Meaningless
    /// when `cross_session_from_name` is `None`; always `false` in that
    /// case.
    pub cross_session_is_dispatch_marker: bool,
}

#[derive(Debug, Deserialize, Default)]
struct RawLine {
    #[serde(default)]
    timestamp: Option<String>,
    #[serde(default, rename = "isSidechain")]
    is_sidechain: bool,
    #[serde(default)]
    message: Option<RawMessage>,
    #[serde(default, rename = "type")]
    line_type: Option<String>,
    #[serde(default, rename = "customTitle")]
    custom_title: Option<String>,
}

#[derive(Debug, Deserialize, Default)]
struct RawMessage {
    #[serde(default)]
    model: Option<String>,
    #[serde(default)]
    content: Option<Value>,
}

/// Parse one JSONL line. `None` for a blank line or invalid JSON — the
/// caller skips it and keeps going; one torn line never aborts the whole
/// transcript (same "bad entry degrades, doesn't propagate" policy as
/// `dispatch::parse_status` callers in `control.rs`).
pub fn parse_line(line: &str) -> Option<ParsedEvent> {
    let trimmed = line.trim();
    if trimmed.is_empty() {
        return None;
    }
    let raw: RawLine = serde_json::from_str(trimmed).ok()?;
    let timestamp = raw.timestamp.as_deref().and_then(parse_rfc3339);
    let model = raw.message.as_ref().and_then(|message| message.model.clone());
    let cross_session_tag = raw
        .message
        .as_ref()
        .and_then(|message| message.content.as_ref())
        .and_then(extract_cross_session_tag);
    let (cross_session_from_name, cross_session_is_dispatch_marker) = match cross_session_tag {
        Some((name, marker)) => (Some(name), marker),
        None => (None, false),
    };
    let custom_title = if raw.line_type.as_deref() == Some("custom-title") {
        raw.custom_title.filter(|title| !title.trim().is_empty())
    } else {
        None
    };
    Some(ParsedEvent {
        timestamp,
        is_sidechain: raw.is_sidechain,
        model,
        custom_title,
        cross_session_from_name,
        cross_session_is_dispatch_marker,
    })
}

/// Parse a whole transcript (or transcript tail). Malformed/blank lines are
/// silently skipped, never fatal to the rest of the file.
pub fn parse_transcript(content: &str) -> Vec<ParsedEvent> {
    content.lines().filter_map(parse_line).collect()
}

fn parse_rfc3339(value: &str) -> Option<DateTime<Utc>> {
    DateTime::parse_from_rfc3339(value.trim())
        .ok()
        .map(|time| time.with_timezone(&Utc))
}

/// `<cross-session-message from="local_<uuid>" name="<title>" ...>` — only
/// `content` shaped as a plain string carries this (every real occurrence
/// found on this machine was a bare `user` message string, never inside a
/// content-block array), so array-shaped `content` is not scanned.
/// Recognizes `<cross-session-message from="..." name="..." ...>`
/// (session-to-session `send_message` delivery) AND
/// `<desktop-session-message from="..." name="..." ...>` (a Claude
/// Desktop coordinator's delivery into a CCD-managed session — a real,
/// live example: "Doc1 主管" (`entrypoint":"claude-desktop"`) dispatching
/// to "doc3 redies調查"/"doc5 Api文件檢查" on 2026-08-16 used this exact
/// tag, not `cross-session-message` — confirmed by grepping the real
/// recipient transcripts after the tree silently failed to nest them).
/// Both tags carry the identical `from`/`name` attribute shape for the
/// identical purpose (an inbound message naming its sender's own title),
/// so this matches on the shared `session-message` marker and walks back
/// to its enclosing `<...>` open tag rather than hardcoding one exact tag
/// name — forward-compatible with any future `<*session-message>` variant
/// without another silent-miss bug like this one.
///
/// Returns `(name, is_dispatch_marker)` — `is_dispatch_marker` is whether
/// the tag's own BODY (the text right after this opening tag's `>`) starts
/// with one of [`DISPATCH_MARKERS`], the signal `tree::build_tree`'s
/// marker-only edge rule keys on (task 4, 2026-08-16: "message direction
/// alone cannot distinguish dispatch from reply/report").
fn extract_cross_session_tag(content: &Value) -> Option<(String, bool)> {
    let text = content.as_str()?;
    let marker_pos = text.find("session-message")?;
    let tag_start = text[..marker_pos].rfind('<')?;
    let tag_end = text[tag_start..].find('>')? + tag_start;
    let tag = &text[tag_start..=tag_end];
    let name = extract_attr(tag, "name")?;
    let body = &text[tag_end + 1..];
    Some((name, body_has_dispatch_marker(body)))
}

fn extract_attr(tag: &str, attr: &str) -> Option<String> {
    let needle = format!("{attr}=\"");
    let attr_start = tag.find(&needle)? + needle.len();
    let rest = &tag[attr_start..];
    let end = rest.find('"')?;
    Some(rest[..end].to_owned())
}

/// Real examples confirmed on this machine: `[派工|來自 CCCG開發
/// session,使用者指示] 調查...` IS a dispatch; `[撤令|來自 CCCG開發] 使用者指示
/// ...` (a recall, not a new assignment) and `[edge-test|CCCG開發] 樹狀 Flow
/// 邊渲染終驗用訊息` (a connectivity ping) are NOT — confirming the marker
/// check must be an exact prefix match, not a substring/keyword search
/// (`[撤令`/`[edge-test` share the `[` but not the marker text itself, so a
/// prefix check already excludes them correctly without any extra
/// exclusion list). ASCII markers (`[assign`, `[dispatch`) match
/// case-insensitively; the CJK ones have no case to normalize. Checked
/// against the body with leading whitespace/newlines trimmed (the real
/// shape is `<tag ...>\n[派工|...`, a newline before the bracket).
const DISPATCH_MARKERS: &[&str] = &["[派工", "【任務", "[任務", "[assign", "[dispatch", "【派工"];

/// Task 11 (2026-08-17, live-verified against Doc1 主管's real dispatch
/// bodies): the coordinator's ACTUAL convention is never one of
/// [`DISPATCH_MARKERS`] — every real dispatch found on this machine reads
/// `doc1 → doc7:歡迎加入...`, `doc1 → doc2:**發車令生效...**`, an explicit
/// `<sender> <arrow> <recipient>:` prefix. This is a STRONGER signal than
/// the bracket markers (it names both ends of the edge, not just "this is a
/// dispatch"), so it gets its own recognizer alongside them, not a
/// replacement. Three arrow spellings accepted: the Unicode arrow `→`
/// (U+2192, what every real example above actually uses), the ASCII `->`
/// fallback, and the CJK fullwidth punctuation spelling `－＞` (fullwidth
/// hyphen-minus + fullwidth greater-than, U+FF0D U+FF1E — plausible from a
/// fullwidth IME) some clients might emit instead of the Unicode arrow
/// glyph.
const ARROW_MARKERS: &[&str] = &["→", "->", "－＞"];

fn body_has_dispatch_marker(body: &str) -> bool {
    let trimmed = body.trim_start();
    if DISPATCH_MARKERS.iter().any(|marker| starts_with_marker(trimmed, marker)) {
        return true;
    }
    let first_line = trimmed.lines().next().unwrap_or("");
    first_line_has_arrow_prefix(first_line)
}

fn starts_with_marker(text: &str, marker: &str) -> bool {
    if marker.is_ascii() {
        text.get(..marker.len()).is_some_and(|prefix| prefix.eq_ignore_ascii_case(marker))
    } else {
        text.starts_with(marker)
    }
}

/// Matches `<word> <arrow> <word>:` anchored at the START of the line —
/// `word` is a short bare token (letters/digits/`_`/`-`, no spaces or
/// punctuation, e.g. `doc1`/`doc7`), so a line like `研究一下 -> 做完` (prose
/// that merely contains an arrow) never qualifies: `研究一下` fails the
/// short-token check the same way a real sentence would. What follows the
/// colon (the dispatch body itself — possibly markdown-bold, possibly CJK
/// prose) is never inspected; only the `sender arrow recipient:` prefix
/// shape matters.
fn first_line_has_arrow_prefix(first_line: &str) -> bool {
    ARROW_MARKERS.iter().any(|arrow| {
        let Some(idx) = first_line.find(arrow) else {
            return false;
        };
        let sender = first_line[..idx].trim();
        let after = &first_line[idx + arrow.len()..];
        let Some(colon_idx) = after.find(':') else {
            return false;
        };
        let recipient = after[..colon_idx].trim();
        is_short_name_token(sender) && is_short_name_token(recipient)
    })
}

/// `doc1`, `doc7`, `Doc1_主管`-style short names — plain identifiers, never
/// containing whitespace or the arrow/colon delimiters themselves. Kept
/// deliberately loose (no length cap on legitimate names, just "no
/// delimiter characters") since the real sender/recipient tokens observed
/// on this machine are short but this isn't a validated ID format.
fn is_short_name_token(token: &str) -> bool {
    !token.is_empty() && !token.contains(char::is_whitespace) && !token.contains(':') && ARROW_MARKERS.iter().all(|arrow| !token.contains(arrow))
}

/// `"claude-sonnet-5"` → `"sonnet-5"` — the `claude-` prefix is redundant
/// once the row is already tagged with the claude provider color/dot.
pub fn short_model_name(model: &str) -> String {
    model.strip_prefix("claude-").unwrap_or(model).to_owned()
}

// ── Session/agent summaries: pure, built from already-read strings ──────────

#[derive(Debug, Clone)]
pub struct InboundEdge {
    pub from_name: String,
    pub at: DateTime<Utc>,
    /// See `ParsedEvent::cross_session_is_dispatch_marker`.
    pub is_dispatch_marker: bool,
}

#[derive(Debug, Clone)]
pub struct AgentSummary {
    pub agent_id: String,
    pub model: Option<String>,
    pub last_event_at: DateTime<Utc>,
    /// Best-effort short phrase — from the sidecar `.meta.json`'s
    /// `description` field when present, else `agent-{index}` (1-based,
    /// caller-assigned so it is stable/deterministic per scan).
    pub label: String,
}

#[derive(Debug, Clone)]
pub struct SessionSummary {
    pub session_id: String,
    pub project_slug: String,
    /// Resolved title (last `custom-title` in the transcript) or the honest
    /// `{slug}·{first-8-chars-of-uuid}` fallback when the session was never
    /// titled.
    pub title: String,
    /// True when `title` is the honest fallback (no real title found) —
    /// mirrors `control::ControlRow::source_is_unknown`'s "render this
    /// muted" convention for the shell.
    pub title_is_fallback: bool,
    /// Lowercased leading run of ASCII alphanumerics from `title`, used to
    /// best-effort match a dispatch job's `callerLabel` (e.g. `"doc1"`)
    /// against a session titled `"Doc1 主管"` — see `tree::build_tree`.
    pub title_key: String,
    pub model: Option<String>,
    pub last_event_at: DateTime<Utc>,
    pub agents: Vec<AgentSummary>,
    pub inbound_edges: Vec<InboundEdge>,
}

/// One subagent transcript, ready to summarize — `meta_json` is the sidecar
/// `.meta.json` raw content, if it was found and read.
pub struct SubagentInput<'a> {
    pub agent_id: &'a str,
    pub content: &'a str,
    pub meta_json: Option<&'a str>,
}

#[derive(Debug, Deserialize, Default)]
struct AgentMeta {
    #[serde(default)]
    description: Option<String>,
}

/// Operator follow-up (2026-08-16): the row's own text must stay the
/// SINGLE source of truth for both the compact on-row display AND its
/// hover-tooltip full text — a Rust-side hard truncation short enough to
/// visibly bite (the original 24-byte cap did) would mean the tooltip
/// could only ever show the same already-cut string. Widened generously;
/// this is now a wire-size safety net, not the thing that actually decides
/// what's visible — the shell's own `TextTrimming.CharacterEllipsis`
/// handles the VISUAL clipping per available width, off this full string.
const AGENT_LABEL_MAX_BYTES: usize = 200;

/// Build one session's summary from already-read transcript text. Returns
/// `None` when the session isn't alive by EITHER measure below.
///
/// Aliveness is `max(own newest event, every subagent's own newest event)`,
/// not just the parent transcript's own newest event: a controller session
/// routinely sits idle (waiting on a dispatched Task-tool subagent to
/// finish) for well over the alive window while that subagent is still
/// actively writing to its OWN transcript file the whole time — this is
/// this exact task's own real running shape (verified against this
/// session's live files while investigating: the top-level
/// `70d73719-....jsonl` transcript's last event was its own dispatch of
/// this subagent, long before the alive window, while
/// `70d73719-.../subagents/agent-a53....jsonl` kept growing every single
/// tool call). Gating solely on the parent's own last event would make a
/// session with a very-much-alive agent silently disappear — the opposite
/// of what this module exists to show.
pub fn build_session_summary(
    project_slug: &str,
    session_id: &str,
    main_content: &str,
    subagents: &[SubagentInput<'_>],
    now: DateTime<Utc>,
    window_secs: i64,
) -> Option<SessionSummary> {
    let events = parse_transcript(main_content);
    let own_last_event_at = events.iter().filter_map(|event| event.timestamp).max();

    struct AgentEvents<'a> {
        subagent: &'a SubagentInput<'a>,
        events: Vec<ParsedEvent>,
        last_event_at: Option<DateTime<Utc>>,
    }
    let agent_events: Vec<AgentEvents> = subagents
        .iter()
        .map(|subagent| {
            let events = parse_transcript(subagent.content);
            let last_event_at = events.iter().filter_map(|event| event.timestamp).max();
            AgentEvents { subagent, events, last_event_at }
        })
        .collect();

    let last_event_at = [own_last_event_at]
        .into_iter()
        .chain(agent_events.iter().map(|a| a.last_event_at))
        .flatten()
        .max()?;
    if (now - last_event_at).num_seconds() > window_secs {
        return None; // neither the session itself nor any of its agents is alive
    }

    let title = events
        .iter()
        .rev()
        .find_map(|event| event.custom_title.clone());
    let (title, title_is_fallback) = match title {
        Some(title) => (title, false),
        None => (
            format!("{project_slug}·{}", session_id.chars().take(8).collect::<String>()),
            true,
        ),
    };
    let title_key = leading_key(&title);

    let model = events.iter().rev().find_map(|event| event.model.clone());

    let inbound_edges = events
        .iter()
        .filter_map(|event| {
            let name = event.cross_session_from_name.clone()?;
            let at = event.timestamp?;
            if (now - at).num_seconds() > window_secs {
                return None;
            }
            Some(InboundEdge { from_name: name, at, is_dispatch_marker: event.cross_session_is_dispatch_marker })
        })
        .collect();

    let mut agents = Vec::new();
    for (index, agent) in agent_events.iter().enumerate() {
        let Some(agent_last_event) = agent.last_event_at else {
            continue;
        };
        if (now - agent_last_event).num_seconds() > window_secs {
            continue; // this particular agent isn't alive — invisible, even though the session itself is
        }
        let agent_model = agent.events.iter().rev().find_map(|event| event.model.clone());
        let label = agent
            .subagent
            .meta_json
            .and_then(|raw| serde_json::from_str::<AgentMeta>(raw).ok())
            .and_then(|meta| meta.description)
            .map(|description| summarize_prompt(description.as_bytes(), 4 * 1024, AGENT_LABEL_MAX_BYTES))
            .filter(|label| !label.is_empty())
            .unwrap_or_else(|| format!("agent-{}", index + 1));
        agents.push(AgentSummary {
            agent_id: agent.subagent.agent_id.to_owned(),
            model: agent_model,
            last_event_at: agent_last_event,
            label,
        });
    }

    Some(SessionSummary {
        session_id: session_id.to_owned(),
        project_slug: project_slug.to_owned(),
        title,
        title_is_fallback,
        title_key,
        model,
        last_event_at,
        agents,
        inbound_edges,
    })
}

/// Lowercased leading run of ASCII alphanumerics — `"Doc1 主管"` → `"doc1"`,
/// `"CCCG開發"` → `"cccg"`. Used only for the best-effort caller-label ↔
/// session-title match in `tree.rs`; documented there as a heuristic, not an
/// identity guarantee.
pub fn leading_key(title: &str) -> String {
    title
        .chars()
        .take_while(|character| character.is_ascii_alphanumeric())
        .flat_map(char::to_lowercase)
        .collect()
}

// ── Filesystem scanning: the stat sweep + bounded tail reads ───────────────

/// `~/.claude/projects` resolved at runtime from the environment — never a
/// hardcoded user path. `USERPROFILE` is the Windows convention this whole
/// app already uses elsewhere (see `cccog-bar-cli`); `HOME` is accepted too
/// so the same code path is exercisable on a non-Windows CI runner.
pub fn default_projects_root() -> Option<std::path::PathBuf> {
    std::env::var_os("USERPROFILE")
        .or_else(|| std::env::var_os("HOME"))
        .map(std::path::PathBuf::from)
        .map(|home| home.join(".claude").join("projects"))
}

fn tail_read(path: &Path, max_bytes: u64) -> Option<String> {
    use std::io::{Read, Seek, SeekFrom};
    let mut file = std::fs::File::open(path).ok()?;
    let length = file.metadata().ok()?.len();
    let start = length.saturating_sub(max_bytes);
    file.seek(SeekFrom::Start(start)).ok()?;
    let mut bytes = Vec::new();
    file.read_to_end(&mut bytes).ok()?;
    let text = String::from_utf8_lossy(&bytes).into_owned();
    if start == 0 {
        return Some(text);
    }
    // The read almost certainly starts mid-line; drop the first (partial)
    // line so it doesn't get misread as a torn-but-plausible JSON object.
    match text.find('\n') {
        Some(index) => Some(text[index + 1..].to_owned()),
        None => Some(String::new()),
    }
}

fn mtime_ms(path: &Path) -> Option<u64> {
    let modified = std::fs::metadata(path).ok()?.modified().ok()?;
    modified
        .duration_since(std::time::UNIX_EPOCH)
        .ok()
        .map(|duration| duration.as_millis() as u64)
}

/// Stat-sweep candidate filter: a file whose mtime already predates the
/// alive window (plus margin) cannot contain an in-window event — transcript
/// files are append-only, so mtime is always >= the newest event's time.
fn is_candidate(path: &Path, cutoff_ms: u64) -> bool {
    mtime_ms(path).is_some_and(|mtime| mtime >= cutoff_ms)
}

/// Full stat-sweep + bounded-tail-read scan of `root` (`~/.claude/projects`).
/// Only files that pass the mtime prefilter are opened at all; this is the
/// "parse only the handful of active files" half of the mechanism. The
/// skip-parse-entirely shortcut (item 4) lives one layer up, in
/// [`ClaudeSessionTailer`], because it needs to persist a token across
/// calls.
pub fn collect_claude_sessions(root: &Path, now: DateTime<Utc>) -> Vec<SessionSummary> {
    let cutoff_ms = now
        .timestamp_millis()
        .saturating_sub((CLAUDE_ALIVE_WINDOW_SECS + MTIME_MARGIN_SECS) * 1000)
        .max(0) as u64;

    let mut out = Vec::new();
    let Ok(project_entries) = std::fs::read_dir(root) else {
        return out;
    };
    for project_entry in project_entries.flatten() {
        let project_path = project_entry.path();
        if !project_path.is_dir() {
            continue;
        }
        let Some(slug) = project_path.file_name().and_then(|name| name.to_str()) else {
            continue;
        };
        let Ok(session_files) = std::fs::read_dir(&project_path) else {
            continue;
        };
        for session_entry in session_files.flatten() {
            let session_path = session_entry.path();
            if session_path.extension().and_then(|extension| extension.to_str()) != Some("jsonl") {
                continue;
            }
            let Some(session_id) = session_path.file_stem().and_then(|stem| stem.to_str()) else {
                continue;
            };
            let subagents_dir = project_path.join(session_id).join("subagents");
            // Candidate = the parent transcript's OWN mtime is fresh, OR any
            // of its subagents' is. A controller session routinely goes
            // quiet (waiting on a dispatched subagent) for well past the
            // window while that subagent keeps writing the whole time —
            // gating on the parent file's mtime alone would skip the whole
            // session (and its very-much-alive agent) before its subagents
            // dir was ever even looked at. See
            // `build_session_summary`'s doc comment for the same reasoning
            // applied to the content-level aliveness check.
            if !is_candidate(&session_path, cutoff_ms) && !any_subagent_candidate(&subagents_dir, cutoff_ms) {
                continue;
            }
            let Some(main_content) = tail_read(&session_path, TRANSCRIPT_TAIL_MAX_BYTES) else {
                continue;
            };

            let subagent_files = collect_subagent_files(&subagents_dir, cutoff_ms);
            let subagents: Vec<SubagentInput> = subagent_files
                .iter()
                .map(|(agent_id, content, meta)| SubagentInput {
                    agent_id,
                    content,
                    meta_json: meta.as_deref(),
                })
                .collect();

            if let Some(summary) =
                build_session_summary(slug, session_id, &main_content, &subagents, now, CLAUDE_ALIVE_WINDOW_SECS)
            {
                out.push(summary);
            }
        }
    }
    out
}

/// Cheap stat-only check (no reads): does any `*.jsonl` directly under
/// `dir` (a session's `subagents/` folder) have a fresh-enough mtime to be
/// worth parsing? Used to keep a session's mtime candidacy from depending
/// solely on its own (possibly long-idle) parent transcript file.
fn any_subagent_candidate(dir: &Path, cutoff_ms: u64) -> bool {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return false;
    };
    entries.flatten().any(|entry| {
        let path = entry.path();
        path.extension().and_then(|extension| extension.to_str()) == Some("jsonl") && is_candidate(&path, cutoff_ms)
    })
}

/// Returns `(agent_id, content, meta_json)` for every subagent transcript
/// that passes the same mtime prefilter as a top-level session file.
fn collect_subagent_files(dir: &Path, cutoff_ms: u64) -> Vec<(String, String, Option<String>)> {
    let mut out = Vec::new();
    let Ok(entries) = std::fs::read_dir(dir) else {
        return out;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.extension().and_then(|extension| extension.to_str()) != Some("jsonl") {
            continue;
        }
        if !is_candidate(&path, cutoff_ms) {
            continue;
        }
        let Some(stem) = path.file_stem().and_then(|stem| stem.to_str()) else {
            continue;
        };
        let Some(agent_id) = stem.strip_prefix("agent-") else {
            continue;
        };
        let Some(content) = tail_read(&path, TRANSCRIPT_TAIL_MAX_BYTES) else {
            continue;
        };
        let meta_path = path.with_extension("meta.json");
        let meta_json = std::fs::read_to_string(&meta_path).ok();
        out.push((agent_id.to_owned(), content, meta_json));
    }
    out
}

/// Cheap stat-only sweep (no file reads) of the newest mtime across every
/// transcript file under `root` — what [`ClaudeSessionTailer`] compares tick
/// to tick to decide whether a full rescan is even needed. Mirrors
/// `tokscale_core::latest_source_mtime_ms`.
fn newest_transcript_mtime_ms(root: &Path) -> Option<u64> {
    let mut newest: Option<u64> = None;
    let Ok(project_entries) = std::fs::read_dir(root) else {
        return None;
    };
    for project_entry in project_entries.flatten() {
        let project_path = project_entry.path();
        if !project_path.is_dir() {
            continue;
        }
        let Ok(session_files) = std::fs::read_dir(&project_path) else {
            continue;
        };
        for session_entry in session_files.flatten() {
            let session_path = session_entry.path();
            if session_path.extension().and_then(|extension| extension.to_str()) == Some("jsonl") {
                if let Some(mtime) = mtime_ms(&session_path) {
                    newest = Some(newest.map_or(mtime, |current| current.max(mtime)));
                }
                let subagents_dir = project_path
                    .join(session_path.file_stem().and_then(|stem| stem.to_str()).unwrap_or(""))
                    .join("subagents");
                if let Ok(subagent_entries) = std::fs::read_dir(&subagents_dir) {
                    for subagent_entry in subagent_entries.flatten() {
                        let subagent_path = subagent_entry.path();
                        if subagent_path.extension().and_then(|extension| extension.to_str()) == Some("jsonl") {
                            if let Some(mtime) = mtime_ms(&subagent_path) {
                                newest = Some(newest.map_or(mtime, |current| current.max(mtime)));
                            }
                        }
                    }
                }
            }
        }
    }
    newest
}

/// Snapshot-replaced per tick, skip-parse shortcut when nothing changed —
/// same shape as TokenBar's `UsageTailer`, adapted to this module's output
/// type. A process-wide static instance lives in `cccog-bar-ffi` (the FFI
/// boundary is stateless per call, so the tailer itself has to be the
/// long-lived thing, same pattern as that crate's `quota_resilience_cache`).
pub struct ClaudeSessionTailer {
    last_newest_mtime_ms: Mutex<Option<u64>>,
    last_result: Mutex<Vec<SessionSummary>>,
}

impl Default for ClaudeSessionTailer {
    fn default() -> Self {
        Self::new()
    }
}

impl ClaudeSessionTailer {
    pub fn new() -> Self {
        Self {
            last_newest_mtime_ms: Mutex::new(None),
            last_result: Mutex::new(Vec::new()),
        }
    }

    pub fn tick(&self, root: &Path, now: DateTime<Utc>) -> Vec<SessionSummary> {
        let newest = newest_transcript_mtime_ms(root);
        let mut last_token = self
            .last_newest_mtime_ms
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if let (Some(current), Some(previous)) = (newest, *last_token) {
            if current == previous {
                return self
                    .last_result
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner())
                    .clone();
            }
        }
        let result = collect_claude_sessions(root, now);
        *last_token = newest;
        drop(last_token);
        *self
            .last_result
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner()) = result.clone();
        result
    }
}

/// Alive agents only, most-recent-event first — what `tree.rs` renders as
/// this session's children.
pub fn alive_agents(summary: &SessionSummary) -> Vec<&AgentSummary> {
    let mut agents: Vec<&AgentSummary> = summary.agents.iter().collect();
    agents.sort_by(|a, b| b.last_event_at.cmp(&a.last_event_at));
    agents
}

/// Best-effort session-title lookup by peer name (used to resolve a
/// cross-session-message `name` or a dispatch job's `callerLabel` to a live
/// session) — exact title match first, then the `title_key` heuristic.
pub fn find_controller<'a>(sessions: &'a [SessionSummary], name: &str) -> Option<&'a SessionSummary> {
    if let Some(exact) = sessions.iter().find(|session| session.title == name) {
        return Some(exact);
    }
    let key = leading_key(name);
    if key.is_empty() {
        return None;
    }
    sessions.iter().find(|session| session.title_key == key)
}

/// Index sessions by id for quick lookup while building the tree.
pub fn index_by_session_id(sessions: &[SessionSummary]) -> HashMap<&str, &SessionSummary> {
    sessions
        .iter()
        .map(|session| (session.session_id.as_str(), session))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn line(json: &str) -> String {
        json.to_owned()
    }

    #[test]
    fn parse_line_skips_malformed_and_blank_lines() {
        assert!(parse_line("").is_none());
        assert!(parse_line("   ").is_none());
        assert!(parse_line("{not json").is_none());
        assert!(parse_line(r#"{"jobId":}"#).is_none());
    }

    #[test]
    fn parse_line_extracts_timestamp_model_and_title() {
        let event = parse_line(&line(
            r#"{"type":"assistant","timestamp":"2026-08-16T07:45:19.106Z","isSidechain":false,"message":{"model":"claude-sonnet-5","content":[{"type":"text","text":"hi"}]}}"#,
        ))
        .unwrap();
        assert_eq!(event.timestamp.unwrap().to_rfc3339(), "2026-08-16T07:45:19.106+00:00");
        assert_eq!(event.model.as_deref(), Some("claude-sonnet-5"));
        assert!(!event.is_sidechain);
        assert!(event.custom_title.is_none());

        let title_event = parse_line(&line(
            r#"{"type":"custom-title","customTitle":"CCCG開發","sessionId":"s1"}"#,
        ))
        .unwrap();
        assert_eq!(title_event.custom_title.as_deref(), Some("CCCG開發"));
    }

    #[test]
    fn parse_line_extracts_cross_session_message_name() {
        // The `\"` here IS the real on-disk shape (the outer line is one
        // JSON object; `content` is itself a JSON string, so its embedded
        // `"` characters must be escaped) — by the time `serde_json` hands
        // this back as a `Value::String`, those escapes are already fully
        // decoded to plain `"` characters, same as any other JSON string
        // field. There is no raw/undecoded text path in this parser to
        // regress into.
        let event = parse_line(&line(
            r#"{"type":"user","timestamp":"2026-08-16T07:00:00Z","message":{"role":"user","content":"<cross-session-message from=\"local_95c9c014-1694-4e27-97de-392ddee6a50c\" name=\"doc1主管\" encoded=\"1\">\nhello\n</cross-session-message>"}}"#,
        ))
        .unwrap();
        assert_eq!(event.cross_session_from_name.as_deref(), Some("doc1主管"));
    }

    /// Regression for the real 2026-08-16 live-run failure (task 3): a
    /// Claude Desktop coordinator's delivery uses `<desktop-session-message>`,
    /// not `<cross-session-message>` — confirmed against the real recipient
    /// transcripts ("doc5 Api文件檢查" received `<desktop-session-message
    /// from="local_ae65a2fa-..." name="Doc1 主管" ...>`). Both tag names
    /// must resolve to an edge.
    #[test]
    fn parse_line_extracts_name_from_the_desktop_session_message_tag_too() {
        let event = parse_line(&line(
            r#"{"type":"user","timestamp":"2026-08-16T10:03:28.278Z","message":{"role":"user","content":"<desktop-session-message from=\"local_ae65a2fa-329e-42af-84de-7b22b8bcd8ac\" name=\"Doc1 主管\" encoded=\"1\">\ndoc1 → doc5: hi\n</desktop-session-message>"}}"#,
        ))
        .unwrap();
        assert_eq!(event.cross_session_from_name.as_deref(), Some("Doc1 主管"));
    }

    /// Task 4 (2026-08-16): "message direction alone cannot distinguish
    /// dispatch from reply/report" — real bodies confirmed on this
    /// machine: `[派工|...]` is a genuine dispatch; `[撤令|...]` (a recall)
    /// and `[edge-test|...]` (a connectivity ping) share the `[` but are
    /// NOT dispatches, and a status report like `【調查完工】...` isn't
    /// either. A prefix (not substring/keyword) match already separates
    /// all of these correctly with no extra exclusion list needed.
    #[test]
    fn parse_line_flags_real_dispatch_marker_bodies_and_rejects_lookalikes() {
        let dispatch = parse_line(&line(
            r#"{"type":"user","timestamp":"2026-08-16T10:00:00Z","message":{"content":"<desktop-session-message from=\"x\" name=\"CCCG開發\">\n[派工|來自 CCCG開發 session,使用者指示] 調查一下\n</desktop-session-message>"}}"#,
        ))
        .unwrap();
        assert!(dispatch.cross_session_is_dispatch_marker);

        let recall = parse_line(&line(
            r#"{"type":"user","timestamp":"2026-08-16T10:00:00Z","message":{"content":"<desktop-session-message from=\"x\" name=\"CCCG開發\">\n[撤令|來自 CCCG開發] 立即停止\n</desktop-session-message>"}}"#,
        ))
        .unwrap();
        assert!(!recall.cross_session_is_dispatch_marker, "[撤令 is a recall, not a dispatch");

        let ping = parse_line(&line(
            r#"{"type":"user","timestamp":"2026-08-16T10:00:00Z","message":{"content":"<desktop-session-message from=\"x\" name=\"CCCG開發\">\n[edge-test|CCCG開發] 樹狀 Flow 邊渲染終驗用訊息\n</desktop-session-message>"}}"#,
        ))
        .unwrap();
        assert!(!ping.cross_session_is_dispatch_marker, "[edge-test is a connectivity ping, not a dispatch");

        let report = parse_line(&line(
            r#"{"type":"user","timestamp":"2026-08-16T10:00:00Z","message":{"content":"<cross-session-message from=\"x\" name=\"doc3\">\n【調查完工】結果如下\n</cross-session-message>"}}"#,
        ))
        .unwrap();
        assert!(!report.cross_session_is_dispatch_marker, "a completion report is not a dispatch");

        let ack = parse_line(&line(
            r#"{"type":"user","timestamp":"2026-08-16T10:00:00Z","message":{"content":"<cross-session-message from=\"x\" name=\"doc3\">\n收到,馬上處理\n</cross-session-message>"}}"#,
        ))
        .unwrap();
        assert!(!ack.cross_session_is_dispatch_marker, "a bare acknowledgement is not a dispatch");
    }

    #[test]
    fn dispatch_marker_ascii_variants_match_case_insensitively_cjk_ones_do_not_need_to() {
        assert!(body_has_dispatch_marker("[Dispatch] do the thing"));
        assert!(body_has_dispatch_marker("[ASSIGN] do the thing"));
        assert!(body_has_dispatch_marker("  \n [assign] leading whitespace is trimmed"));
        assert!(body_has_dispatch_marker("【任務】中文任務"));
        assert!(body_has_dispatch_marker("【派工】中文派工"));
        assert!(!body_has_dispatch_marker("assign without the bracket"));
        assert!(!body_has_dispatch_marker(""));
    }

    /// Task 11 (2026-08-17): Doc1 主管's REAL dispatch convention, verified
    /// against the live transcripts — never one of the bracket markers
    /// above, always an explicit `sender -> recipient:` prefix.
    #[test]
    fn arrow_prefix_dispatch_marker_recognizes_the_real_doc1_convention() {
        // The exact real bodies (bar/SYNC.md task-11 section).
        assert!(body_has_dispatch_marker("doc1 → doc7:歡迎加入..."));
        assert!(body_has_dispatch_marker("doc1 → doc2:**發車令生效...**"), "bold-wrapped text right after the colon must not block the prefix match");
        assert!(body_has_dispatch_marker("doc1 → doc4:..."));
        assert!(body_has_dispatch_marker("doc1 → doc5:..."));
        // ASCII arrow fallback and the fullwidth punctuation spelling.
        assert!(body_has_dispatch_marker("doc1 -> doc7: hello"));
        assert!(body_has_dispatch_marker("doc1－＞doc7:hello"));
        // Leading whitespace/newline before the arrow line is trimmed, same
        // as the bracket markers.
        assert!(body_has_dispatch_marker("  \n doc1 → doc7: hi"));
        // Only the FIRST line is inspected — an arrow appearing later in a
        // multi-line body (e.g. inside the dispatch's own prose) must not
        // retroactively mark an unrelated first line as a dispatch.
        assert!(!body_has_dispatch_marker("收到,馬上處理\ndoc1 → doc7: this arrow is not on the first line"));
        // Prose that merely contains an arrow is not a dispatch: the
        // "sender"/"recipient" tokens must be short, space-free, colon-free
        // identifiers, not sentence fragments.
        assert!(!body_has_dispatch_marker("研究一下 -> 這個問題 應該怎麼做: 先看文件"));
        assert!(!body_has_dispatch_marker("plain text with no arrow at all"));
        assert!(!body_has_dispatch_marker(""));
    }

    #[test]
    fn short_model_name_strips_claude_prefix_only() {
        assert_eq!(short_model_name("claude-sonnet-5"), "sonnet-5");
        assert_eq!(short_model_name("gpt-5.6-luna"), "gpt-5.6-luna");
    }

    fn at(value: &str) -> DateTime<Utc> {
        DateTime::parse_from_rfc3339(value).unwrap().with_timezone(&Utc)
    }

    const NOW: &str = "2026-08-16T12:00:00Z";

    fn transcript(lines: &[&str]) -> String {
        lines.join("\n")
    }

    #[test]
    fn alive_session_within_window_produces_a_summary() {
        let content = transcript(&[
            r#"{"type":"user","timestamp":"2026-08-16T11:55:00Z","message":{"role":"user","content":"hi"}}"#,
            r#"{"type":"custom-title","customTitle":"CCCG開發","sessionId":"s1"}"#,
            r#"{"type":"assistant","timestamp":"2026-08-16T11:56:00Z","message":{"model":"claude-sonnet-5","content":[]}}"#,
        ]);
        let summary = build_session_summary("d--code-CCCG", "s1", &content, &[], at(NOW), CLAUDE_ALIVE_WINDOW_SECS)
            .expect("session must be alive: last event 4m ago, well inside the 10m window");
        assert_eq!(summary.title, "CCCG開發");
        assert!(!summary.title_is_fallback);
        assert_eq!(summary.model.as_deref(), Some("claude-sonnet-5"));
        assert_eq!(summary.last_event_at, at("2026-08-16T11:56:00Z"));
    }

    #[test]
    fn session_with_no_events_within_window_is_not_alive() {
        let content = transcript(&[
            r#"{"type":"user","timestamp":"2026-08-16T11:00:00Z","message":{"role":"user","content":"hi"}}"#,
        ]);
        // Last event was 1h ago — outside the 10-minute alive window.
        assert!(build_session_summary("slug", "s1", &content, &[], at(NOW), CLAUDE_ALIVE_WINDOW_SECS).is_none());
    }

    #[test]
    fn session_with_no_parseable_events_at_all_is_not_alive() {
        let content = transcript(&["not json", "", "   "]);
        assert!(build_session_summary("slug", "s1", &content, &[], at(NOW), CLAUDE_ALIVE_WINDOW_SECS).is_none());
    }

    #[test]
    fn untitled_session_falls_back_to_slug_and_short_uuid() {
        let content = transcript(&[
            r#"{"type":"user","timestamp":"2026-08-16T11:59:00Z","message":{"role":"user","content":"hi"}}"#,
        ]);
        let summary =
            build_session_summary("d--code-openruterati", "70d73719-5bda-49fa-b40c-121c76ac29e7", &content, &[], at(NOW), CLAUDE_ALIVE_WINDOW_SECS)
                .unwrap();
        assert_eq!(summary.title, "d--code-openruterati·70d73719");
        assert!(summary.title_is_fallback);
    }

    #[test]
    fn malformed_lines_in_an_otherwise_valid_transcript_are_skipped_not_fatal() {
        let content = transcript(&[
            "{not valid json at all",
            r#"{"type":"custom-title","customTitle":"Doc1 主管","sessionId":"s1"}"#,
            "",
            r#"{"type":"user","timestamp":"2026-08-16T11:58:00Z","message":{"role":"user","content":"hi"}}"#,
        ]);
        let summary = build_session_summary("slug", "s1", &content, &[], at(NOW), CLAUDE_ALIVE_WINDOW_SECS).unwrap();
        assert_eq!(summary.title, "Doc1 主管");
        assert_eq!(summary.last_event_at, at("2026-08-16T11:58:00Z"));
    }

    #[test]
    fn sidechain_agent_counting_only_counts_agents_alive_within_window() {
        let content = transcript(&[
            r#"{"type":"assistant","timestamp":"2026-08-16T11:59:00Z","message":{"model":"claude-sonnet-5"}}"#,
        ]);
        let alive_agent = transcript(&[
            r#"{"type":"assistant","isSidechain":true,"timestamp":"2026-08-16T11:58:30Z","message":{"model":"claude-sonnet-5"}}"#,
        ]);
        let stale_agent = transcript(&[
            r#"{"type":"assistant","isSidechain":true,"timestamp":"2026-08-16T09:00:00Z","message":{"model":"claude-sonnet-5"}}"#,
        ]);
        let subagents = vec![
            SubagentInput { agent_id: "alive1", content: &alive_agent, meta_json: Some(r#"{"description":"Add liveness"}"#) },
            SubagentInput { agent_id: "stale1", content: &stale_agent, meta_json: None },
        ];
        let summary = build_session_summary("slug", "s1", &content, &subagents, at(NOW), CLAUDE_ALIVE_WINDOW_SECS).unwrap();
        assert_eq!(summary.agents.len(), 1, "the stale-past-window agent must not count");
        assert_eq!(summary.agents[0].agent_id, "alive1");
        assert_eq!(summary.agents[0].label, "Add liveness");
    }

    /// The real, live shape this task itself ran under while investigating:
    /// a controller session whose own newest event is well outside the
    /// alive window (it dispatched a subagent and has been waiting ever
    /// since), while that subagent's own transcript kept growing the whole
    /// time. The session must still count as alive — via the agent, not its
    /// own idle top-level activity — or a genuinely-working session with a
    /// genuinely-working agent would silently vanish from Flow.
    #[test]
    fn session_stays_alive_via_an_active_agent_even_when_its_own_activity_is_stale() {
        let content = transcript(&[
            r#"{"type":"custom-title","customTitle":"CCCG開發","sessionId":"s1"}"#,
            // Parent's own last event: 40 minutes before NOW -- outside the
            // 10-minute window on its own.
            r#"{"type":"assistant","timestamp":"2026-08-16T11:20:00Z","message":{"model":"claude-sonnet-5"}}"#,
        ]);
        let agent = transcript(&[
            // Agent's own last event: 30 seconds before NOW -- very much alive.
            r#"{"type":"assistant","isSidechain":true,"timestamp":"2026-08-16T11:59:30Z","message":{"model":"claude-sonnet-5"}}"#,
        ]);
        let subagents = vec![SubagentInput { agent_id: "a1", content: &agent, meta_json: None }];
        let summary = build_session_summary("slug", "s1", &content, &subagents, at(NOW), CLAUDE_ALIVE_WINDOW_SECS)
            .expect("session must be alive via its agent even though its own last event is 40m old");
        assert_eq!(summary.title, "CCCG開發");
        assert_eq!(summary.agents.len(), 1);
        // The session's own reported last_event_at reflects the freshest
        // activity anywhere in the tree (parent OR agent), not just the
        // parent's — this is what the tree's "elapsed" reads from.
        assert_eq!(summary.last_event_at, at("2026-08-16T11:59:30Z"));
    }

    #[test]
    fn agent_without_meta_description_falls_back_to_agent_n_label() {
        let content = transcript(&[
            r#"{"type":"assistant","timestamp":"2026-08-16T11:59:00Z","message":{"model":"claude-sonnet-5"}}"#,
        ]);
        let agent = transcript(&[
            r#"{"type":"assistant","isSidechain":true,"timestamp":"2026-08-16T11:58:30Z","message":{}}"#,
        ]);
        let subagents = vec![SubagentInput { agent_id: "a1", content: &agent, meta_json: None }];
        let summary = build_session_summary("slug", "s1", &content, &subagents, at(NOW), CLAUDE_ALIVE_WINDOW_SECS).unwrap();
        assert_eq!(summary.agents[0].label, "agent-1");
    }

    #[test]
    fn cross_session_inbound_edges_are_collected_within_window() {
        let content = transcript(&[
            r#"{"type":"user","timestamp":"2026-08-16T11:59:00Z","message":{"content":"<cross-session-message from=\"local_x\" name=\"doc1主管\">hi</cross-session-message>"}}"#,
        ]);
        let summary = build_session_summary("slug", "s1", &content, &[], at(NOW), CLAUDE_ALIVE_WINDOW_SECS).unwrap();
        assert_eq!(summary.inbound_edges.len(), 1);
        assert_eq!(summary.inbound_edges[0].from_name, "doc1主管");
    }

    #[test]
    fn leading_key_takes_ascii_alnum_prefix_lowercased() {
        assert_eq!(leading_key("Doc1 主管"), "doc1");
        assert_eq!(leading_key("CCCG開發"), "cccg");
        assert_eq!(leading_key(""), "");
    }

    #[test]
    fn empty_projects_root_produces_no_sessions_and_does_not_panic() {
        let root = tempfile::tempdir().unwrap();
        let missing = root.path().join("no-such-projects-dir");
        let sessions = collect_claude_sessions(&missing, at(NOW));
        assert!(sessions.is_empty());
    }

    /// The mtime prefilter (`is_candidate`) reads REAL file mtimes, which a
    /// test cannot backdate to a fictional `NOW` without a filesystem-level
    /// time-travel dependency. These FS-integration tests therefore use the
    /// REAL current time as `now` and place event timestamps relative to it
    /// (`real_now() + Duration`), so both the mtime prefilter and the
    /// content-based alive/stale check agree on the same clock — the same
    /// convention `control.rs`'s own tests use a fixed clock for, except
    /// those never touch real mtimes (dispatch status.json has no mtime
    /// prefilter), so this module's FS tests cannot borrow that trick.
    fn real_now() -> DateTime<Utc> {
        Utc::now()
    }

    fn rfc3339_offset(base: DateTime<Utc>, seconds: i64) -> String {
        (base + chrono::Duration::seconds(seconds)).to_rfc3339_opts(chrono::SecondsFormat::Millis, true)
    }

    #[test]
    fn collect_claude_sessions_reads_real_directory_layout() {
        let now = real_now();
        let root = tempfile::tempdir().unwrap();
        let project = root.path().join("d--code-openruterati");
        std::fs::create_dir_all(&project).unwrap();
        std::fs::write(
            project.join("s1.jsonl"),
            transcript(&[
                r#"{"type":"custom-title","customTitle":"CCCG開發","sessionId":"s1"}"#,
                &format!(
                    r#"{{"type":"assistant","timestamp":"{}","message":{{"model":"claude-sonnet-5"}}}}"#,
                    rfc3339_offset(now, -120)
                ),
            ]),
        )
        .unwrap();
        let subagents_dir = project.join("s1").join("subagents");
        std::fs::create_dir_all(&subagents_dir).unwrap();
        std::fs::write(
            subagents_dir.join("agent-a1.jsonl"),
            transcript(&[&format!(
                r#"{{"type":"assistant","isSidechain":true,"timestamp":"{}","message":{{"model":"claude-sonnet-5"}}}}"#,
                rfc3339_offset(now, -60)
            )]),
        )
        .unwrap();
        std::fs::write(subagents_dir.join("agent-a1.meta.json"), r#"{"description":"Do the thing"}"#).unwrap();

        // A different session whose newest event is 3h old — well outside
        // the 10-minute alive window (and its real mtime, set to "now" by
        // this very write, is also far newer than its own content's
        // timestamp would suggest — the point of this fixture is that the
        // CONTENT timestamp is what excludes it, proving the scan doesn't
        // just trust mtime freshness blindly).
        std::fs::write(
            project.join("stale.jsonl"),
            transcript(&[&format!(
                r#"{{"type":"assistant","timestamp":"{}","message":{{}}}}"#,
                rfc3339_offset(now, -3 * 3600)
            )]),
        )
        .unwrap();

        let sessions = collect_claude_sessions(root.path(), now);
        assert_eq!(sessions.len(), 1, "only the alive session should appear: {sessions:?}");
        let session = &sessions[0];
        assert_eq!(session.title, "CCCG開發");
        assert_eq!(session.agents.len(), 1);
        assert_eq!(session.agents[0].label, "Do the thing");
    }

    /// Regression for the real 2026-08-16 shape behind task 3's live-run
    /// failure: a recipient session living in a WORKTREE gets its own,
    /// differently-named project-slug directory (a worktree's `.claude`
    /// project dir is keyed off ITS OWN cwd, e.g.
    /// `D--code-openruterati--claude-worktrees-goofy-wescoff-28c933`, not
    /// the main repo's `d--code-openruterati`) — session identity here is
    /// always the transcript's own filename (`session_id =
    /// path.file_stem()`), never any externally-supplied id, and the scan
    /// walks every entry under the projects root uninfluenced by slug
    /// naming, so a worktree slug is read exactly like any other. This
    /// test pins that: two sessions in two DIFFERENTLY NAMED slug dirs,
    /// with a `<desktop-session-message>` cross-slug edge between them,
    /// both scanned and the edge resolvable by title (never by slug).
    #[test]
    fn sessions_in_different_worktree_slug_directories_are_both_scanned() {
        let now = real_now();
        let root = tempfile::tempdir().unwrap();

        let main_project = root.path().join("d--code-openruterati");
        std::fs::create_dir_all(&main_project).unwrap();
        std::fs::write(
            main_project.join("controller.jsonl"),
            transcript(&[
                r#"{"type":"custom-title","customTitle":"Doc1 主管","sessionId":"controller"}"#,
                &format!(r#"{{"type":"assistant","timestamp":"{}","message":{{"model":"claude-sonnet-5"}}}}"#, rfc3339_offset(now, -60)),
            ]),
        )
        .unwrap();

        let worktree_project = root
            .path()
            .join("D--code-openruterati--claude-worktrees-goofy-wescoff-28c933");
        std::fs::create_dir_all(&worktree_project).unwrap();
        std::fs::write(
            worktree_project.join("recipient.jsonl"),
            transcript(&[
                r#"{"type":"custom-title","customTitle":"doc3 redies調查","sessionId":"recipient"}"#,
                &format!(
                    r#"{{"type":"user","timestamp":"{}","message":{{"content":"<desktop-session-message from=\"local_x\" name=\"Doc1 主管\" encoded=\"1\">hi</desktop-session-message>"}}}}"#,
                    rfc3339_offset(now, -30)
                ),
            ]),
        )
        .unwrap();

        let sessions = collect_claude_sessions(root.path(), now);
        assert_eq!(sessions.len(), 2, "both slug dirs must be scanned: {sessions:?}");
        let recipient = sessions.iter().find(|s| s.title == "doc3 redies調查").unwrap();
        assert_eq!(recipient.inbound_edges.len(), 1);
        assert_eq!(recipient.inbound_edges[0].from_name, "Doc1 主管");
        assert!(find_controller(&sessions, "Doc1 主管").is_some());
    }

    /// Regression for the real bug this task's own live-run verification
    /// caught: a session whose PARENT transcript file's mtime is stale (it
    /// dispatched a subagent long ago and has been quiet since) must still
    /// be scanned — the mtime candidate prefilter has to consider the
    /// subagent files too, not just the parent file's own mtime, or the
    /// whole session (including its actively-growing agent) silently never
    /// gets read at all.
    #[test]
    fn stale_parent_mtime_with_a_fresh_subagent_file_still_scans_the_session() {
        let now = real_now();
        let root = tempfile::tempdir().unwrap();
        let project = root.path().join("proj");
        std::fs::create_dir_all(&project).unwrap();

        let session_path = project.join("s1.jsonl");
        std::fs::write(
            &session_path,
            transcript(&[
                r#"{"type":"custom-title","customTitle":"Doc1 主管","sessionId":"s1"}"#,
                &format!(
                    r#"{{"type":"assistant","timestamp":"{}","message":{{"model":"claude-sonnet-5"}}}}"#,
                    rfc3339_offset(now, -40 * 60) // 40 minutes old — outside the window on its own
                ),
            ]),
        )
        .unwrap();
        // Backdate the PARENT file's own mtime to match — this is what
        // `is_candidate` reads, and it must NOT be the sole gate.
        let old_mtime = std::time::SystemTime::now() - std::time::Duration::from_secs(40 * 60);
        filetime_set(&session_path, old_mtime).unwrap();

        let subagents_dir = project.join("s1").join("subagents");
        std::fs::create_dir_all(&subagents_dir).unwrap();
        std::fs::write(
            subagents_dir.join("agent-a1.jsonl"),
            transcript(&[&format!(
                r#"{{"type":"assistant","isSidechain":true,"timestamp":"{}","message":{{"model":"claude-sonnet-5"}}}}"#,
                rfc3339_offset(now, -30) // 30 seconds old — very much alive, and freshly written (real mtime)
            )]),
        )
        .unwrap();

        let sessions = collect_claude_sessions(root.path(), now);
        assert_eq!(sessions.len(), 1, "the session must still appear via its alive agent: {sessions:?}");
        assert_eq!(sessions[0].title, "Doc1 主管");
        assert_eq!(sessions[0].agents.len(), 1);
    }

    fn filetime_set(path: &std::path::Path, time: std::time::SystemTime) -> std::io::Result<()> {
        let file = std::fs::OpenOptions::new().write(true).open(path)?;
        file.set_modified(time)
    }

    #[test]
    fn tailer_skips_reparse_when_newest_mtime_is_unchanged() {
        let now = real_now();
        let root = tempfile::tempdir().unwrap();
        let project = root.path().join("proj");
        std::fs::create_dir_all(&project).unwrap();
        std::fs::write(
            project.join("s1.jsonl"),
            transcript(&[
                r#"{"type":"custom-title","customTitle":"T1","sessionId":"s1"}"#,
                &format!(r#"{{"type":"assistant","timestamp":"{}","message":{{}}}}"#, rfc3339_offset(now, -60)),
            ]),
        )
        .unwrap();

        let tailer = ClaudeSessionTailer::new();
        let first = tailer.tick(root.path(), now);
        assert_eq!(first.len(), 1);
        assert_eq!(first[0].title, "T1");

        // Black-box proof of the skip-parse shortcut: pass a `now` two hours
        // later — if the tailer actually re-parsed, the session's last event
        // (60s before the FIRST `now`) would fall outside the alive window
        // and the row would disappear. Since no file's mtime moved, the
        // shortcut must return the untouched cached snapshot instead.
        let much_later = now + chrono::Duration::hours(2);
        let second = tailer.tick(root.path(), much_later);
        assert_eq!(second.len(), 1, "skip-parse shortcut must reuse the cached snapshot, not rescan");
        assert_eq!(second[0].title, "T1");
    }

    #[test]
    fn find_controller_matches_by_exact_title_then_leading_key() {
        let sessions = vec![build_session_summary(
            "slug",
            "s1",
            &transcript(&[
                r#"{"type":"custom-title","customTitle":"Doc1 主管","sessionId":"s1"}"#,
                r#"{"type":"assistant","timestamp":"2026-08-16T11:59:00Z","message":{}}"#,
            ]),
            &[],
            at(NOW),
            CLAUDE_ALIVE_WINDOW_SECS,
        )
        .unwrap()];
        assert!(find_controller(&sessions, "Doc1 主管").is_some());
        assert!(find_controller(&sessions, "doc1").is_some());
        assert!(find_controller(&sessions, "doc1-anything-else").is_some());
        assert!(find_controller(&sessions, "doc2").is_none());
    }
}
