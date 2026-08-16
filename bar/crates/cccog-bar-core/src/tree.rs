//! Flow tree assembly: combines live Claude sessions (`claude_sessions.rs`),
//! presence heartbeats (`presence.rs`), and CCCG dispatch jobs/owners into
//! the operator-specified two-level tree —
//!
//! ```text
//! ● Doc1 主管                     claude · 執行中          45s
//!    ↳ Doc2  (agent · sonnet5)             工作中          2m
//!      Doc4  (session · sonnet5)           閒置           12m
//!      Doc5c (codex · luna-max)            工作中          3m
//! ◐ CCCG開發                      claude · 閒置            8m
//! ○ grok                          resumable · 額度盡       2h
//! ```
//!
//! flattened to a `Vec<TreeRow>` with a `depth` field (0 = top-level
//! controller/resumable row, 1 = its child) — the shell renders indentation
//! from `depth` rather than a real recursive tree control (see
//! `bar/SYNC.md` for why a flat depth-tagged list was chosen over a nested
//! DTO).
//!
//! Three child kinds, one indent style, matching the operator's rules:
//! - `agent`: a same-session Task-tool subagent (sidechain transcript).
//! - `session`: an independent Claude session reached via
//!   `<cross-session-message>` — the edge is resolved by matching the tag's
//!   `name` attribute against a live session's own title (`find_controller`
//!   in `claude_sessions.rs`); this is a best-effort text match, not an
//!   identity guarantee (documented there).
//! - `codex` / `grok`: an active CCCG dispatch job, attached under whichever
//!   live session's title matches the job's `callerLabel`/`hopChain`.
//!
//! A node appears once: a session with an inbound edge from another live
//! session is never also listed top-level — it hangs under whichever
//! controller sent the most recent matching message (rule 3). Terminal
//! dispatch jobs and non-live owner leases are aggregated into bottom rows
//! rather than attached anywhere (rule 4's "terminal/resumable aggregated
//! at the bottom").
//!
//! Operator follow-up (2026-08-16, same day): top-level controller rows
//! also carry `typeModelText` now — `"claude · <model>"` — so a session's
//! model is visible without opening a child row, consistent with how every
//! child already shows its own `(kind · model)`. Omitted (never guessed)
//! when the session has no `message.model` seen yet.

use crate::claude_sessions::{alive_agents, find_controller, short_model_name, SessionSummary, RUNNING_FRESH_SECS};
use crate::presence::{resolve_presence_state, PresenceRecord, PresenceState};
use chrono::{DateTime, Utc};
use serde::Serialize;
use std::collections::{HashMap, HashSet};

pub const RUNNING_STATE: &str = "執行中";
pub const IDLE_STATE: &str = "閒置";
pub const RESUMABLE_STATE: &str = "resumable";
pub const TERMINAL_STATE: &str = "terminal";

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct TreeRow {
    /// 0 = top-level (controller session or a bottom resumable/terminal
    /// aggregate), 1 = a child of the preceding depth-0 row.
    pub depth: u32,
    pub kind: String,
    pub label: String,
    /// `"agent · sonnet-5"` on a child row, `"claude · sonnet-5"` on a
    /// top-level controller row — `None` when no model has been observed
    /// yet (never guessed).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub type_model_text: Option<String>,
    /// Drives the dot color always, and (for child rows only) the
    /// `typeModelText` run's color — "claude" | "codex" | "grok" |
    /// "unknown".
    pub provider: String,
    pub state_label: String,
    pub pulse: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub elapsed_seconds: Option<u64>,
}

/// A dispatch job, already reduced to what the tree needs — callers (the
/// FFI layer) build this from `dispatch::DispatchStatus`; kept separate from
/// `control::JobEntry` (private to that module) rather than exposing it,
/// since the tree only needs a handful of fields and its own much simpler
/// "attach under a matching controller, else drop into the terminal bucket"
/// policy (not `control.rs`'s windowed same-path aggregation).
#[derive(Debug, Clone)]
pub struct DispatchJobInput {
    pub provider: String,
    pub caller_label: Option<String>,
    pub hop_chain: Option<String>,
    pub active: bool,
    /// `status` is "running"/"working" — drives 執行中 vs 閒置 for an
    /// active job's row.
    pub running: bool,
    pub model: Option<String>,
    /// Active: start time. Used for elapsed-seconds only; terminal jobs are
    /// aggregated by count, not shown individually, so their own time isn't
    /// needed here.
    pub time: Option<DateTime<Utc>>,
    /// Short display label for the job's target (session id prefix or a
    /// resolved title) — already bounded/sanitized by the caller.
    pub target_label: String,
}

#[derive(Debug, Clone)]
pub struct OwnerInput {
    pub provider: String,
    /// Has a live `.lock` lease.
    pub live: bool,
}

pub struct TreeBuildInput {
    pub sessions: Vec<SessionSummary>,
    pub presence: HashMap<String, PresenceRecord>,
    pub jobs: Vec<DispatchJobInput>,
    pub owners: Vec<OwnerInput>,
    pub now: DateTime<Utc>,
}

/// Resolve a session's own display state (`RUNNING_STATE`/`IDLE_STATE`) and
/// whether it should pulse: presence (the A/B layer) wins when it resolves
/// to `Running`/`Idle`; `Unknown` (missing/ancient presence) falls back to
/// the C-layer transcript-recency judgment.
fn resolve_session_state(session: &SessionSummary, presence: Option<&PresenceRecord>, now: DateTime<Utc>) -> (&'static str, bool) {
    if let Some(record) = presence {
        match resolve_presence_state(record, now.timestamp_millis()) {
            PresenceState::Running => return (RUNNING_STATE, true),
            PresenceState::Idle => return (IDLE_STATE, false),
            PresenceState::Unknown => {}
        }
    }
    let age = (now - session.last_event_at).num_seconds();
    if age <= RUNNING_FRESH_SECS {
        (RUNNING_STATE, true)
    } else {
        (IDLE_STATE, false)
    }
}

fn elapsed_seconds(since: DateTime<Utc>, now: DateTime<Utc>) -> u64 {
    (now - since).num_seconds().max(0) as u64
}

/// `callerLabel` wins, else the last `>`-separated `hopChain` hop, else
/// `None` (an unattachable job — dropped, not shown under a wrong guess).
/// Deliberately the same precedence as `control.rs::source_label`, minus
/// the "ambiguous" placeholder (a tree row with no resolvable controller
/// just doesn't render as a child at all).
fn job_source_name(job: &DispatchJobInput) -> Option<String> {
    if let Some(label) = job.caller_label.as_deref().map(str::trim).filter(|label| !label.is_empty()) {
        return Some(label.to_owned());
    }
    job.hop_chain
        .as_deref()
        .and_then(|chain| chain.split('>').map(str::trim).filter(|segment| !segment.is_empty()).last())
        .map(str::to_owned)
}

/// `controller_of` is built purely from "each session's own most-recent
/// inbound edge" — genuinely live back-and-forth CCD traffic (A relays to
/// B, B replies to A with its own `<*-session-message>`, both entirely
/// normal) can point two or more sessions at each other. Found live: a
/// real mutual "CCCG開發" ⇄ "Doc4 新增" pair collapsed the ENTIRE tree to
/// just the one unrelated session with no inbound edges — every node
/// downstream of the cycle (including this task's own controller session
/// and its live agent) silently vanished, because a session assigned as
/// someone's controller but which is ITSELF assigned a controller never
/// gets iterated as a top-level row, so nothing ever attaches to it.
///
/// Each session has at most one outgoing "controlled by" edge, so any
/// cycle is a simple loop reachable by walking `controller_of` from any
/// node. This walks every node once (memoized via `resolved`), and on
/// finding a cycle removes the edge belonging to whichever session in that
/// cycle has the lexicographically smallest id — an arbitrary but
/// deterministic tie-break (there is no principled "which one is really
/// the controller" answer for a mutual pair; picking one consistently is
/// what matters, not which one). That session becomes top-level and the
/// rest of the (former) cycle nests under it exactly as any other chain
/// would.
fn break_cycles(controller_of: &mut HashMap<String, String>) {
    let mut resolved: HashSet<String> = HashSet::new();
    let keys: Vec<String> = controller_of.keys().cloned().collect();
    for start in keys {
        if resolved.contains(&start) {
            continue;
        }
        let mut path: Vec<String> = Vec::new();
        let mut current = start;
        loop {
            if resolved.contains(&current) {
                resolved.extend(path);
                break;
            }
            if let Some(cycle_start) = path.iter().position(|node| *node == current) {
                let break_at = path[cycle_start..].iter().min().cloned().expect("non-empty cycle slice");
                controller_of.remove(&break_at);
                resolved.extend(path);
                break;
            }
            path.push(current.clone());
            match controller_of.get(&current) {
                Some(next) => current = next.clone(),
                None => {
                    resolved.extend(path);
                    break;
                }
            }
        }
    }
}

/// After `break_cycles`, `controller_of` is guaranteed acyclic but can
/// still contain multi-hop CHAINS (a 3-or-more-node cycle breaks to a
/// chain, e.g. `a -> b -> c` once `a`'s own outgoing edge is removed) —
/// and the tree is built as exactly two levels (depth 0 controller, depth
/// 1 child), so a session whose `controller_of` entry points at another
/// CHILD rather than an actual top-level session would never be attached
/// anywhere (the depth-1 lookup in `build_tree` only matches a child's
/// controller against sessions already known to be top-level). This walks
/// every entry to its ultimate root — the first node in the chain that is
/// NOT itself a key in `controller_of` — and rewrites the entry to point
/// there directly, so every child, however many hops its original
/// resolution chain had, ends up correctly attached one level under an
/// actual top-level row.
fn flatten_controllers_to_root(controller_of: &mut HashMap<String, String>) {
    let keys: Vec<String> = controller_of.keys().cloned().collect();
    let bound = keys.len();
    for key in keys {
        let mut root = controller_of[&key].clone();
        let mut hops = 0;
        while let Some(next) = controller_of.get(&root) {
            root = next.clone();
            hops += 1;
            if hops > bound {
                break; // defensive only — break_cycles already makes this unreachable
            }
        }
        controller_of.insert(key, root);
    }
}

/// Build the flattened, depth-tagged tree.
pub fn build_tree(input: TreeBuildInput) -> Vec<TreeRow> {
    let TreeBuildInput { sessions, presence, jobs, owners, now } = input;

    // Rule 3 dedup: for every session, pick the single most-recent inbound
    // edge whose `name` resolves to ANOTHER live session — that session
    // becomes this one's controller and it is removed from the top-level
    // set. A session can't be its own controller (guards a degenerate
    // self-addressed cross-session-message from creating a cycle).
    let mut controller_of: HashMap<String, String> = HashMap::new(); // child session_id -> controller session_id
    for session in &sessions {
        let mut best: Option<(DateTime<Utc>, String)> = None;
        for edge in &session.inbound_edges {
            let Some(controller) = find_controller(&sessions, &edge.from_name) else {
                continue;
            };
            if controller.session_id == session.session_id {
                continue; // self-loop guard
            }
            if best.as_ref().is_none_or(|(at, _)| edge.at > *at) {
                best = Some((edge.at, controller.session_id.clone()));
            }
        }
        if let Some((_, controller_id)) = best {
            controller_of.insert(session.session_id.clone(), controller_id);
        }
    }
    break_cycles(&mut controller_of);
    flatten_controllers_to_root(&mut controller_of);

    let child_session_ids: HashSet<&str> = controller_of.keys().map(String::as_str).collect();
    let by_id: HashMap<&str, &SessionSummary> = sessions.iter().map(|s| (s.session_id.as_str(), s)).collect();

    let mut rows: Vec<TreeRow> = Vec::new();

    // Top-level controller rows: every session with no chosen controller of
    // its own, newest activity first.
    let mut top_level: Vec<&SessionSummary> = sessions
        .iter()
        .filter(|session| !child_session_ids.contains(session.session_id.as_str()))
        .collect();
    top_level.sort_by(|a, b| b.last_event_at.cmp(&a.last_event_at));

    for controller in &top_level {
        let (state_label, pulse) = resolve_session_state(controller, presence.get(&controller.session_id), now);
        // Operator follow-up (2026-08-16): top-level rows show their model
        // too, consistent with children — "claude · <model>" from the
        // session's own most-recent in-window `message.model`. Omitted
        // (not guessed) when no model has been seen yet.
        let type_model_text = controller.model.as_deref().map(|model| format!("claude · {}", short_model_name(model)));
        rows.push(TreeRow {
            depth: 0,
            kind: "controller".to_owned(),
            label: controller.title.clone(),
            type_model_text,
            provider: "claude".to_owned(),
            state_label: state_label.to_owned(),
            pulse,
            elapsed_seconds: Some(elapsed_seconds(controller.last_event_at, now)),
        });

        // Children: this session's own alive agents, then any live session
        // whose latest inbound edge points here.
        let mut children: Vec<TreeRow> = Vec::new();

        for agent in alive_agents(controller) {
            let age = (now - agent.last_event_at).num_seconds();
            let (state_label, pulse) = if age <= RUNNING_FRESH_SECS {
                (RUNNING_STATE, true)
            } else {
                (IDLE_STATE, false)
            };
            let model_text = agent.model.as_deref().map(short_model_name).unwrap_or_else(|| "claude".to_owned());
            children.push(TreeRow {
                depth: 1,
                kind: "agent".to_owned(),
                label: agent.label.clone(),
                type_model_text: Some(format!("agent · {model_text}")),
                provider: "claude".to_owned(),
                state_label: state_label.to_owned(),
                pulse,
                elapsed_seconds: Some(elapsed_seconds(agent.last_event_at, now)),
            });
        }

        let mut child_sessions: Vec<&SessionSummary> = controller_of
            .iter()
            .filter(|(_, controller_id)| controller_id.as_str() == controller.session_id)
            .filter_map(|(child_id, _)| by_id.get(child_id.as_str()).copied())
            .collect();
        child_sessions.sort_by(|a, b| b.last_event_at.cmp(&a.last_event_at));
        for child in child_sessions {
            let (state_label, pulse) = resolve_session_state(child, presence.get(&child.session_id), now);
            let model_text = child.model.as_deref().map(short_model_name).unwrap_or_else(|| "claude".to_owned());
            children.push(TreeRow {
                depth: 1,
                kind: "session".to_owned(),
                label: child.title.clone(),
                type_model_text: Some(format!("session · {model_text}")),
                provider: "claude".to_owned(),
                state_label: state_label.to_owned(),
                pulse,
                elapsed_seconds: Some(elapsed_seconds(child.last_event_at, now)),
            });
        }

        // Active dispatch jobs whose caller matches this controller.
        let mut job_children: Vec<TreeRow> = Vec::new();
        for job in &jobs {
            if !job.active {
                continue;
            }
            let Some(source_name) = job_source_name(job) else {
                continue;
            };
            let Some(matched) = find_controller(&sessions, &source_name) else {
                continue;
            };
            // Attach to the ultimate top-level ancestor of the match (a
            // matched session that is itself someone's child forwards the
            // job up to its own controller, keeping the tree exactly two
            // levels deep).
            let attach_to = controller_of
                .get(&matched.session_id)
                .and_then(|id| by_id.get(id.as_str()))
                .map(|s| s.session_id.clone())
                .unwrap_or_else(|| matched.session_id.clone());
            if attach_to != controller.session_id {
                continue;
            }
            let (state_label, pulse) = if job.running { (RUNNING_STATE, true) } else { (IDLE_STATE, false) };
            let model_text = job.model.clone().unwrap_or_else(|| job.provider.clone());
            job_children.push(TreeRow {
                depth: 1,
                kind: job.provider.clone(),
                label: job.target_label.clone(),
                type_model_text: Some(format!("{} · {}", job.provider, model_text)),
                provider: job.provider.clone(),
                state_label: state_label.to_owned(),
                pulse,
                elapsed_seconds: job.time.map(|time| elapsed_seconds(time, now)),
            });
        }
        children.extend(job_children);
        children.sort_by(|a, b| a.elapsed_seconds.unwrap_or(u64::MAX).cmp(&b.elapsed_seconds.unwrap_or(u64::MAX)));
        rows.extend(children);
    }

    // Bottom aggregate: terminal dispatch jobs, per provider.
    let mut terminal_counts: HashMap<String, u32> = HashMap::new();
    for job in &jobs {
        if job.active {
            continue;
        }
        *terminal_counts.entry(job.provider.clone()).or_insert(0) += 1;
    }
    let mut terminal_providers: Vec<&String> = terminal_counts.keys().collect();
    terminal_providers.sort();
    for provider in terminal_providers {
        let count = terminal_counts[provider];
        rows.push(TreeRow {
            depth: 0,
            kind: "terminal".to_owned(),
            label: format!("{provider} ×{count}"),
            type_model_text: None,
            provider: provider.clone(),
            state_label: TERMINAL_STATE.to_owned(),
            pulse: false,
            elapsed_seconds: None,
        });
    }

    // Bottom aggregate: owner leases, live (閒置) vs no lock (resumable),
    // one row per (provider, live) pair with a count.
    let mut owner_counts: HashMap<(String, bool), u32> = HashMap::new();
    for owner in &owners {
        *owner_counts.entry((owner.provider.clone(), owner.live)).or_insert(0) += 1;
    }
    let mut owner_keys: Vec<&(String, bool)> = owner_counts.keys().collect();
    owner_keys.sort();
    for key @ (provider, live) in owner_keys {
        let count = owner_counts[key];
        let label = if count > 1 { format!("{provider} ×{count}") } else { provider.clone() };
        rows.push(TreeRow {
            depth: 0,
            kind: if *live { provider.clone() } else { "resumable".to_owned() },
            label,
            type_model_text: None,
            provider: provider.clone(),
            state_label: if *live { IDLE_STATE.to_owned() } else { RESUMABLE_STATE.to_owned() },
            pulse: false,
            elapsed_seconds: None,
        });
    }

    rows
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::claude_sessions::build_session_summary;

    fn at(value: &str) -> DateTime<Utc> {
        DateTime::parse_from_rfc3339(value).unwrap().with_timezone(&Utc)
    }

    const NOW: &str = "2026-08-16T12:00:00Z";

    fn session(id: &str, title: &str, last_event: &str) -> SessionSummary {
        let content = format!(
            r#"{{"type":"custom-title","customTitle":"{title}","sessionId":"{id}"}}
{{"type":"assistant","timestamp":"{last_event}","message":{{"model":"claude-sonnet-5"}}}}"#
        );
        build_session_summary("slug", id, &content, &[], at(NOW), crate::claude_sessions::CLAUDE_ALIVE_WINDOW_SECS).unwrap()
    }

    fn session_with_edge(id: &str, title: &str, last_event: &str, from_name: &str, edge_at: &str) -> SessionSummary {
        let content = format!(
            r#"{{"type":"custom-title","customTitle":"{title}","sessionId":"{id}"}}
{{"type":"user","timestamp":"{edge_at}","message":{{"content":"<cross-session-message from=\"local_x\" name=\"{from_name}\">hi</cross-session-message>"}}}}
{{"type":"assistant","timestamp":"{last_event}","message":{{"model":"claude-sonnet-5"}}}}"#
        );
        build_session_summary("slug", id, &content, &[], at(NOW), crate::claude_sessions::CLAUDE_ALIVE_WINDOW_SECS).unwrap()
    }

    fn empty_input(sessions: Vec<SessionSummary>) -> TreeBuildInput {
        TreeBuildInput {
            sessions,
            presence: HashMap::new(),
            jobs: Vec::new(),
            owners: Vec::new(),
            now: at(NOW),
        }
    }

    #[test]
    fn a_session_with_no_inbound_edge_is_top_level() {
        let rows = build_tree(empty_input(vec![session("s1", "CCCG開發", "2026-08-16T11:59:00Z")]));
        assert_eq!(rows.len(), 1);
        assert_eq!(rows[0].depth, 0);
        assert_eq!(rows[0].kind, "controller");
        assert_eq!(rows[0].label, "CCCG開發");
        // Operator follow-up (2026-08-16): top-level rows show their model
        // too now, consistent with children — the `session()` fixture's
        // assistant event carries "claude-sonnet-5".
        assert_eq!(rows[0].type_model_text.as_deref(), Some("claude · sonnet-5"));
        assert_eq!(rows[0].provider, "claude");
    }

    #[test]
    fn a_controller_row_omits_the_model_parenthetical_when_no_model_was_seen() {
        let content = r#"{"type":"custom-title","customTitle":"Doc1 主管","sessionId":"s1"}
{"type":"user","timestamp":"2026-08-16T11:59:00Z","message":{"content":"no model on a user turn"}}"#;
        let session = build_session_summary("slug", "s1", content, &[], at(NOW), crate::claude_sessions::CLAUDE_ALIVE_WINDOW_SECS).unwrap();
        assert!(session.model.is_none(), "fixture precondition: no message.model anywhere in this transcript");
        let rows = build_tree(empty_input(vec![session]));
        assert_eq!(rows[0].type_model_text, None, "never guess a model that was never observed");
    }

    #[test]
    fn session_reached_via_cross_session_message_becomes_a_child_not_top_level() {
        let controller = session("c1", "Doc1 主管", "2026-08-16T11:59:30Z");
        let child = session_with_edge("c2", "Doc4", "2026-08-16T11:50:00Z", "Doc1 主管", "2026-08-16T11:55:00Z");
        let rows = build_tree(empty_input(vec![controller, child]));

        let top_level: Vec<&TreeRow> = rows.iter().filter(|r| r.depth == 0).collect();
        assert_eq!(top_level.len(), 1, "Doc4 must not appear as a second top-level row");
        assert_eq!(top_level[0].label, "Doc1 主管");

        let children: Vec<&TreeRow> = rows.iter().filter(|r| r.depth == 1).collect();
        assert_eq!(children.len(), 1);
        assert_eq!(children[0].kind, "session");
        assert_eq!(children[0].label, "Doc4");
    }

    #[test]
    fn a_node_contacted_by_two_controllers_hangs_under_the_latest_one() {
        let old_controller = session("c1", "doc1", "2026-08-16T11:59:00Z");
        let new_controller = session("c2", "doc2", "2026-08-16T11:59:00Z");
        // child received a message from doc1 at 11:40, then from doc2 at
        // 11:55 -- doc2's message is the newer one, so it wins.
        let content = format!(
            r#"{{"type":"custom-title","customTitle":"child","sessionId":"c3"}}
{{"type":"user","timestamp":"2026-08-16T11:40:00Z","message":{{"content":"<cross-session-message from=\"x\" name=\"doc1\">hi</cross-session-message>"}}}}
{{"type":"user","timestamp":"2026-08-16T11:55:00Z","message":{{"content":"<cross-session-message from=\"x\" name=\"doc2\">hi</cross-session-message>"}}}}
{{"type":"assistant","timestamp":"2026-08-16T11:56:00Z","message":{{}}}}"#
        );
        let child = build_session_summary("slug", "c3", &content, &[], at(NOW), crate::claude_sessions::CLAUDE_ALIVE_WINDOW_SECS).unwrap();

        let rows = build_tree(empty_input(vec![old_controller, new_controller, child]));
        let top_level: Vec<&TreeRow> = rows.iter().filter(|r| r.depth == 0).collect();
        assert_eq!(top_level.len(), 2, "doc1 and doc2 both stay top-level, child is deduped away");
        // "child" must appear exactly once in the whole row list, as a
        // depth-1 row (its presence anywhere at depth 0 would mean the
        // dedup failed).
        let occurrences = rows.iter().filter(|r| r.label == "child").count();
        assert_eq!(occurrences, 1);
        assert!(rows.iter().any(|r| r.label == "child" && r.depth == 1));
    }

    /// Regression for a real, live bug found while verifying task 3's fix:
    /// two sessions that recently messaged EACH OTHER (a completely normal
    /// live back-and-forth — A relays to B, B replies to A) each end up
    /// with the other as their "most recent inbound edge" controller,
    /// forming a 2-cycle. Before `break_cycles`, this made BOTH sessions —
    /// and this task's own real "CCCG開發" ⇄ "Doc4 新增" pair reproduced
    /// this exactly — vanish from the tree entirely: a session assigned as
    /// someone's controller but which itself has a controller is never
    /// iterated as a top-level row, so nothing (including a live agent
    /// child!) ever gets attached anywhere visible.
    #[test]
    fn a_mutual_edge_between_two_sessions_does_not_erase_either_of_them() {
        let a = session_with_edge("a", "CCCG開發", "2026-08-16T11:59:00Z", "Doc4 新增", "2026-08-16T11:50:00Z");
        let b = session_with_edge("b", "Doc4 新增", "2026-08-16T11:58:00Z", "CCCG開發", "2026-08-16T11:55:00Z");
        let rows = build_tree(empty_input(vec![a, b]));

        assert_eq!(
            rows.iter().filter(|r| r.label == "CCCG開發" || r.label == "Doc4 新增").count(),
            2,
            "both sessions in the mutual pair must still render somewhere: {rows:?}"
        );
        let top_level_count = rows.iter().filter(|r| r.depth == 0).count();
        assert_eq!(top_level_count, 1, "the cycle breaks to exactly one top-level anchor, not zero, not two");
        let child_count = rows.iter().filter(|r| r.depth == 1).count();
        assert_eq!(child_count, 1, "the other session nests under the anchor instead of vanishing");
    }

    /// Same shape, three sessions in a longer cycle (A→B→C→A) — the fix
    /// must not be special-cased to exactly two nodes.
    #[test]
    fn a_three_way_mutual_edge_cycle_does_not_erase_any_of_them() {
        let a = session_with_edge("a", "Alpha", "2026-08-16T11:59:00Z", "Gamma", "2026-08-16T11:50:00Z");
        let b = session_with_edge("b", "Beta", "2026-08-16T11:58:00Z", "Alpha", "2026-08-16T11:51:00Z");
        let c = session_with_edge("c", "Gamma", "2026-08-16T11:57:00Z", "Beta", "2026-08-16T11:52:00Z");
        let rows = build_tree(empty_input(vec![a, b, c]));

        assert_eq!(rows.len(), 3, "all three sessions must still render: {rows:?}");
        assert_eq!(rows.iter().filter(|r| r.depth == 0).count(), 1);
        assert_eq!(rows.iter().filter(|r| r.depth == 1).count(), 2);
    }

    #[test]
    fn agents_render_as_children_with_agent_kind_and_model_text() {
        let content = r#"{"type":"custom-title","customTitle":"Doc1","sessionId":"s1"}
{"type":"assistant","timestamp":"2026-08-16T11:59:00Z","message":{"model":"claude-sonnet-5"}}"#;
        let agent_content = r#"{"type":"assistant","isSidechain":true,"timestamp":"2026-08-16T11:59:30Z","message":{"model":"claude-sonnet-5"}}"#;
        let subagents = vec![crate::claude_sessions::SubagentInput {
            agent_id: "a1",
            content: agent_content,
            meta_json: Some(r#"{"description":"Doc2"}"#),
        }];
        let session = build_session_summary("slug", "s1", content, &subagents, at(NOW), crate::claude_sessions::CLAUDE_ALIVE_WINDOW_SECS).unwrap();
        let rows = build_tree(empty_input(vec![session]));
        let agent_row = rows.iter().find(|r| r.kind == "agent").unwrap();
        assert_eq!(agent_row.depth, 1);
        assert_eq!(agent_row.label, "Doc2");
        assert_eq!(agent_row.type_model_text.as_deref(), Some("agent · sonnet-5"));
    }

    #[test]
    fn active_dispatch_job_attaches_under_matching_caller_label() {
        let controller = session("s1", "Doc1 主管", "2026-08-16T11:59:00Z");
        let job = DispatchJobInput {
            provider: "codex".to_owned(),
            caller_label: Some("doc1".to_owned()),
            hop_chain: None,
            active: true,
            running: true,
            model: Some("luna-max".to_owned()),
            time: Some(at("2026-08-16T11:57:00Z")),
            target_label: "Doc5c".to_owned(),
        };
        let mut input = empty_input(vec![controller]);
        input.jobs = vec![job];
        let rows = build_tree(input);
        let job_row = rows.iter().find(|r| r.kind == "codex").unwrap();
        assert_eq!(job_row.depth, 1);
        assert_eq!(job_row.label, "Doc5c");
        assert_eq!(job_row.type_model_text.as_deref(), Some("codex · luna-max"));
        assert_eq!(job_row.state_label, RUNNING_STATE);
    }

    #[test]
    fn terminal_jobs_and_non_live_owners_aggregate_at_the_bottom() {
        let mut input = empty_input(Vec::new());
        input.jobs = vec![DispatchJobInput {
            provider: "codex".to_owned(),
            caller_label: None,
            hop_chain: None,
            active: false,
            running: false,
            model: None,
            time: None,
            target_label: "x".to_owned(),
        }];
        input.owners = vec![OwnerInput { provider: "grok".to_owned(), live: false }];
        let rows = build_tree(input);
        let terminal = rows.iter().find(|r| r.kind == "terminal").unwrap();
        assert_eq!(terminal.state_label, TERMINAL_STATE);
        let resumable = rows.iter().find(|r| r.kind == "resumable").unwrap();
        assert_eq!(resumable.state_label, RESUMABLE_STATE);
        assert_eq!(resumable.label, "grok");
    }

    #[test]
    fn presence_running_overrides_stale_transcript_to_show_executing() {
        // Session's own last transcript event is 5 minutes old (would read
        // 閒置 from the C-layer alone), but a fresh PostToolUse presence
        // heartbeat says it's mid-turn right now.
        let controller = session("s1", "Doc1 主管", "2026-08-16T11:55:00Z");
        let mut presence = HashMap::new();
        presence.insert(
            "s1".to_owned(),
            PresenceRecord {
                session_id: "s1".to_owned(),
                cwd: None,
                event: "PostToolUse".to_owned(),
                ts: at(NOW).timestamp_millis() - 5_000,
            },
        );
        let mut input = empty_input(vec![controller]);
        input.presence = presence;
        let rows = build_tree(input);
        assert_eq!(rows[0].state_label, RUNNING_STATE);
        assert!(rows[0].pulse);
    }

    #[test]
    fn presence_stale_shows_idle_even_if_c_layer_alone_would_say_running() {
        let controller = session("s1", "Doc1 主管", "2026-08-16T11:59:50Z"); // 10s old, C-layer alone => running
        let mut presence = HashMap::new();
        presence.insert(
            "s1".to_owned(),
            PresenceRecord {
                session_id: "s1".to_owned(),
                cwd: None,
                event: "Stop".to_owned(),
                ts: at(NOW).timestamp_millis() - 10_000,
            },
        );
        let mut input = empty_input(vec![controller]);
        input.presence = presence;
        let rows = build_tree(input);
        assert_eq!(rows[0].state_label, IDLE_STATE, "a Stop heartbeat means turn ended, even if very fresh");
    }
}
