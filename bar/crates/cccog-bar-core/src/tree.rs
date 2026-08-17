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

/// Task 4 (2026-08-16, operator-approved design, then amended to be
/// independently toggleable rather than hard-wired together): the two
/// control-edge rules the operator wants to A/B against real data before
/// picking one. Both default `false` — the baseline is the ORIGINAL
/// "latest edge wins" behavior, unchanged, so an absent/default config is
/// explicit current behavior, not a silent third mode.
///
/// - `initiator_wins`: within a session PAIR, only the pair's INITIATOR
///   (whoever sent the chronologically first *marked* dispatch in-window,
///   in either direction — task 11, 2026-08-17: unmarked messages, e.g. a
///   worker's own check-in report, never claim initiator status even when
///   they land first) can ever become the other's controller. Every
///   message in the opposite direction is a reply and is dropped before it
///   can create an edge or flip control — this removes the A⇄B mutual-edge
///   cycle at the source (`break_cycles` stays as a safety net, but should
///   rarely trigger with this on).
/// - `marker_only`: only edges whose message body starts with a dispatch
///   marker (`claude_sessions::body_has_dispatch_marker`) are even
///   considered candidates — a bare reply/report/ack never restructures
///   the tree, regardless of direction.
///
/// The two compose: with both on, only a dispatch-marked message from the
/// pair's initiator can create an edge (the operator's original combined
/// design); with only one on, that rule alone applies to the raw edge set.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct EdgeRules {
    pub initiator_wins: bool,
    pub marker_only: bool,
}

pub struct TreeBuildInput {
    pub sessions: Vec<SessionSummary>,
    pub presence: HashMap<String, PresenceRecord>,
    pub jobs: Vec<DispatchJobInput>,
    pub owners: Vec<OwnerInput>,
    pub now: DateTime<Utc>,
    pub edge_rules: EdgeRules,
    /// Session titles (exact match) that always render top-level and never
    /// as anyone's child, and whose own dispatch edges win over a
    /// non-pinned controller's when both claim the same child in-window.
    /// Empty = feature off (default; absent config key).
    pub pinned_coordinators: Vec<String>,
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

/// One session's inbound message, already resolved to the sending
/// session's id (unresolvable names and self-loops are dropped before this
/// exists at all) — the shared unit both the edge-selection loop and the
/// pair-initiator computation below operate on.
struct ResolvedEdge {
    controller_id: String,
    at: DateTime<Utc>,
    is_dispatch_marker: bool,
}

/// Unordered pair key so `(A, B)` and `(B, A)` traffic land in the same
/// bucket regardless of which direction is being looked at.
fn pair_key(a: &str, b: &str) -> (String, String) {
    if a < b {
        (a.to_owned(), b.to_owned())
    } else {
        (b.to_owned(), a.to_owned())
    }
}

/// Rule 1's direction lock: for every unordered pair with traffic in either
/// direction within the window, the pair's initiator is whoever sent the
/// chronologically EARLIEST message. Task 11 (2026-08-17, live-verified
/// regression): when `marked_only` is set, "earliest" is restricted to
/// MARKED messages only — a real "Doc7 向 Doc1 主管報到" check-in (an
/// unmarked worker report, no dispatch marker at all) landed before Doc1's
/// own first marked dispatch to doc7; the raw/unfiltered version of this
/// function let that unmarked check-in claim doc7 as the pair's permanent
/// initiator, which under `initiator_wins` locked Doc1 out of ever
/// controlling doc7, even from a later, genuine, marked dispatch. "Who
/// spoke first" is only a meaningful signal about WHO DISPATCHED WHOM when
/// restricted to messages that are actually dispatches; a report, a
/// check-in, or chatter sent first doesn't make its sender the controller.
///
/// `marked_only` is deliberately the caller's own `edge_rules.marker_only`
/// value, not unconditional: with `marker_only` OFF, `initiator_wins` is a
/// pure "who genuinely spoke first, content-agnostic" cycle-breaker in its
/// own right (see `edge_rule_combinations_produce_different_topologies_on_the_same_fixture`,
/// which exists specifically to keep the four rule combinations visibly
/// distinct for the operator's A/B) — gating the fix here, rather than
/// applying it unconditionally, keeps that mode's own semantics intact
/// while still fixing the live "both rules on" bug this task targets.
fn compute_pair_initiators(resolved_edges: &HashMap<String, Vec<ResolvedEdge>>, marked_only: bool) -> HashMap<(String, String), String> {
    let mut earliest: HashMap<(String, String), (DateTime<Utc>, String)> = HashMap::new();
    for (receiver, edges) in resolved_edges {
        for edge in edges {
            if marked_only && !edge.is_dispatch_marker {
                continue; // an unmarked report/check-in never claims initiator status
            }
            let key = pair_key(receiver, &edge.controller_id);
            let is_new_earliest = earliest.get(&key).is_none_or(|(at, _)| edge.at < *at);
            if is_new_earliest {
                earliest.insert(key, (edge.at, edge.controller_id.clone()));
            }
        }
    }
    earliest.into_iter().map(|(key, (_, sender))| (key, sender)).collect()
}

/// Build the flattened, depth-tagged tree.
pub fn build_tree(input: TreeBuildInput) -> Vec<TreeRow> {
    let TreeBuildInput { sessions, presence, jobs, owners, now, edge_rules, pinned_coordinators } = input;

    let by_id: HashMap<&str, &SessionSummary> = sessions.iter().map(|s| (s.session_id.as_str(), s)).collect();
    let is_pinned = |session_id: &str| -> bool {
        by_id.get(session_id).is_some_and(|session| pinned_coordinators.iter().any(|name| name == &session.title))
    };

    // Resolve every session's raw inbound edges once: name -> controller
    // session id, dropping unresolvable names and self-loops. Everything
    // below (rule 1's initiator lock, rule 2's marker filter, rule 3's
    // dedup/pinned-precedence) operates on this already-resolved set.
    let mut resolved_edges: HashMap<String, Vec<ResolvedEdge>> = HashMap::new();
    for session in &sessions {
        let mut edges = Vec::new();
        for edge in &session.inbound_edges {
            let Some(controller) = find_controller(&sessions, &edge.from_name) else {
                continue;
            };
            if controller.session_id == session.session_id {
                continue; // self-loop guard
            }
            edges.push(ResolvedEdge {
                controller_id: controller.session_id.clone(),
                at: edge.at,
                is_dispatch_marker: edge.is_dispatch_marker,
            });
        }
        resolved_edges.insert(session.session_id.clone(), edges);
    }

    let initiator_of_pair: HashMap<(String, String), String> = if edge_rules.initiator_wins {
        compute_pair_initiators(&resolved_edges, edge_rules.marker_only)
    } else {
        HashMap::new()
    };

    // Rule 3 dedup: for every session, pick the single best surviving
    // inbound edge — filtered by rule 1 (initiator_wins) and rule 2
    // (marker_only) first, then a pinned coordinator's edge always beats a
    // non-pinned one regardless of recency, then most-recent-wins among
    // whatever remains.
    let mut controller_of: HashMap<String, String> = HashMap::new(); // child session_id -> controller session_id
    for session in &sessions {
        let edges = resolved_edges.get(session.session_id.as_str()).map(Vec::as_slice).unwrap_or_default();
        let mut best: Option<&ResolvedEdge> = None;
        for edge in edges {
            if edge_rules.marker_only && !edge.is_dispatch_marker {
                continue;
            }
            if edge_rules.initiator_wins {
                let key = pair_key(&session.session_id, &edge.controller_id);
                if initiator_of_pair.get(&key) != Some(&edge.controller_id) {
                    continue; // a reply within this pair — never creates or flips an edge
                }
            }
            let replace = match best {
                None => true,
                Some(current) => {
                    let this_pinned = is_pinned(&edge.controller_id);
                    let current_pinned = is_pinned(&current.controller_id);
                    if this_pinned != current_pinned {
                        this_pinned // a pinned sender wins regardless of recency
                    } else {
                        edge.at > current.at
                    }
                }
            };
            if replace {
                best = Some(edge);
            }
        }
        if let Some(best) = best {
            controller_of.insert(session.session_id.clone(), best.controller_id.clone());
        }
    }
    break_cycles(&mut controller_of);
    flatten_controllers_to_root(&mut controller_of);
    // Rule 3 (pinned coordinators), final enforcement: a pinned session
    // never renders as anyone's child, whatever the edge resolution above
    // produced — this is a hard override, not just a precedence weight.
    controller_of.retain(|child_id, _| !is_pinned(child_id));

    let child_session_ids: HashSet<&str> = controller_of.keys().map(String::as_str).collect();

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
            edge_rules: EdgeRules::default(),
            pinned_coordinators: Vec::new(),
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

    /// Task 4 (2026-08-16): the SAME mutual-edge fixture as above, but with
    /// `initiator_wins` on — the pair never even reaches `break_cycles`,
    /// because only the chronologically first sender ("Doc4 新增", whose
    /// message at 11:50 predates "CCCG開發"'s reply at 11:55) can ever
    /// become a controller. This is the deterministic, semantically
    /// correct resolution the tiebreak-based safety net could only
    /// approximate by luck.
    #[test]
    fn the_old_cycle_fixture_resolves_deterministically_via_initiator_wins() {
        let a = session_with_edge("a", "CCCG開發", "2026-08-16T11:59:00Z", "Doc4 新增", "2026-08-16T11:50:00Z");
        let b = session_with_edge("b", "Doc4 新增", "2026-08-16T11:58:00Z", "CCCG開發", "2026-08-16T11:55:00Z");
        let mut input = empty_input(vec![a, b]);
        input.edge_rules = EdgeRules { initiator_wins: true, marker_only: false };
        let rows = build_tree(input);

        let top = rows.iter().filter(|r| r.depth == 0).collect::<Vec<_>>();
        assert_eq!(top.len(), 1);
        assert_eq!(top[0].label, "Doc4 新增", "Doc4 新增 spoke first (11:50 < 11:55), so it is the pair's initiator");
        let children = rows.iter().filter(|r| r.depth == 1).collect::<Vec<_>>();
        assert_eq!(children.len(), 1);
        assert_eq!(children[0].label, "CCCG開發");
    }

    /// Task 11 (2026-08-17): the real live regression, reproduced with the
    /// real session titles and the real message shapes. "Doc7 向 Doc1 主管
    /// 報到" initiated contact first (an unmarked worker check-in, 11:00) —
    /// under the OLD raw-earliest initiator computation this alone would
    /// have permanently locked Doc1 out of ever controlling doc7. Doc1's
    /// own dispatch to doc7 (`doc1 → doc7:歡迎加入...`, the real arrow-prefix
    /// shape, 11:30 — after the check-in) must still win: it's the pair's
    /// only MARKED message.
    #[test]
    fn unmarked_checkin_does_not_block_a_later_marked_dispatch_from_the_opposite_direction() {
        let doc1_content = r#"{"type":"custom-title","customTitle":"Doc1 主管","sessionId":"d1"}
{"type":"user","timestamp":"2026-08-16T11:54:00Z","message":{"content":"<desktop-session-message from=\"x\" name=\"Doc7\">Doc7 向 Doc1 主管報到</desktop-session-message>"}}
{"type":"assistant","timestamp":"2026-08-16T11:54:10Z","message":{"model":"claude-sonnet-5"}}"#;
        let doc7_content = r#"{"type":"custom-title","customTitle":"Doc7","sessionId":"d7"}
{"type":"user","timestamp":"2026-08-16T11:58:00Z","message":{"content":"<desktop-session-message from=\"x\" name=\"Doc1 主管\">doc1 → doc7:歡迎加入...</desktop-session-message>"}}
{"type":"assistant","timestamp":"2026-08-16T11:58:10Z","message":{"model":"claude-sonnet-5"}}"#;
        let doc1 = build_session_summary("slug", "d1", doc1_content, &[], at(NOW), crate::claude_sessions::CLAUDE_ALIVE_WINDOW_SECS).unwrap();
        let doc7 = build_session_summary("slug", "d7", doc7_content, &[], at(NOW), crate::claude_sessions::CLAUDE_ALIVE_WINDOW_SECS).unwrap();

        let rows = tree_with_rules(vec![doc1, doc7], EdgeRules { initiator_wins: true, marker_only: true });

        let top: Vec<&TreeRow> = rows.iter().filter(|r| r.depth == 0).collect();
        assert_eq!(top.len(), 1, "doc7's unmarked check-in must not create a second top-level anchor: {rows:?}");
        assert_eq!(top[0].label, "Doc1 主管");
        let children: Vec<&TreeRow> = rows.iter().filter(|r| r.depth == 1).collect();
        assert_eq!(children.len(), 1);
        assert_eq!(children[0].label, "Doc7", "doc1's real dispatch must nest doc7 under it, unblocked by the earlier check-in");
    }

    /// Task 11: once a pair's initiator is locked in by its first MARKED
    /// message, a LATER marked message in the opposite direction is a
    /// reply, not a flip -- dropped, never restructuring the tree. Real
    /// example this guards against: doc7 (correctly nested under Doc1)
    /// later sends its OWN marked-shaped status update back to Doc1; that
    /// must not un-nest doc7 and re-parent Doc1 under it.
    #[test]
    fn a_later_marked_message_in_the_opposite_direction_does_not_flip_the_initiator() {
        let doc1_content = r#"{"type":"custom-title","customTitle":"Doc1 主管","sessionId":"d1"}
{"type":"user","timestamp":"2026-08-16T11:58:00Z","message":{"content":"<desktop-session-message from=\"x\" name=\"Doc7\">doc7 → doc1:完成回報...</desktop-session-message>"}}
{"type":"assistant","timestamp":"2026-08-16T11:58:10Z","message":{"model":"claude-sonnet-5"}}"#;
        let doc7_content = r#"{"type":"custom-title","customTitle":"Doc7","sessionId":"d7"}
{"type":"user","timestamp":"2026-08-16T11:55:00Z","message":{"content":"<desktop-session-message from=\"x\" name=\"Doc1 主管\">doc1 → doc7:歡迎加入...</desktop-session-message>"}}
{"type":"assistant","timestamp":"2026-08-16T11:58:10Z","message":{"model":"claude-sonnet-5"}}"#;
        let doc1 = build_session_summary("slug", "d1", doc1_content, &[], at(NOW), crate::claude_sessions::CLAUDE_ALIVE_WINDOW_SECS).unwrap();
        let doc7 = build_session_summary("slug", "d7", doc7_content, &[], at(NOW), crate::claude_sessions::CLAUDE_ALIVE_WINDOW_SECS).unwrap();

        let rows = tree_with_rules(vec![doc1, doc7], EdgeRules { initiator_wins: true, marker_only: true });

        let top: Vec<&TreeRow> = rows.iter().filter(|r| r.depth == 0).collect();
        assert_eq!(top.len(), 1, "doc7's later marked reply must not create a second top-level anchor: {rows:?}");
        assert_eq!(top[0].label, "Doc1 主管", "the pair's initiator (doc1, first marked dispatch at 11:55) never flips");
        let children: Vec<&TreeRow> = rows.iter().filter(|r| r.depth == 1).collect();
        assert_eq!(children.len(), 1);
        assert_eq!(children[0].label, "Doc7");
    }

    fn init_and_reply_sessions() -> (SessionSummary, SessionSummary) {
        // zz_init sends aa_reply a plain (non-marker) message FIRST
        // (11:59:00) -- establishing it as the pair's initiator. aa_reply
        // replies LATER (11:59:20) with a message that happens to look
        // like a dispatch marker -- a realistic shape (an ack quoting a
        // task-shaped subject line, say), not a contrived edge case.
        let init_content = r#"{"type":"custom-title","customTitle":"ZInit","sessionId":"zz_init"}
{"type":"user","timestamp":"2026-08-16T11:59:20Z","message":{"content":"<cross-session-message from=\"x\" name=\"AReply\">[派工] 回頭幫我看一下</cross-session-message>"}}
{"type":"assistant","timestamp":"2026-08-16T11:59:25Z","message":{"model":"claude-sonnet-5"}}"#;
        let reply_content = r#"{"type":"custom-title","customTitle":"AReply","sessionId":"aa_reply"}
{"type":"user","timestamp":"2026-08-16T11:59:00Z","message":{"content":"<cross-session-message from=\"x\" name=\"ZInit\">嗨</cross-session-message>"}}
{"type":"assistant","timestamp":"2026-08-16T11:59:05Z","message":{"model":"claude-sonnet-5"}}"#;
        let init = build_session_summary("slug", "zz_init", init_content, &[], at(NOW), crate::claude_sessions::CLAUDE_ALIVE_WINDOW_SECS).unwrap();
        let reply = build_session_summary("slug", "aa_reply", reply_content, &[], at(NOW), crate::claude_sessions::CLAUDE_ALIVE_WINDOW_SECS).unwrap();
        (init, reply)
    }

    fn tree_with_rules(sessions: Vec<SessionSummary>, edge_rules: EdgeRules) -> Vec<TreeRow> {
        build_tree(TreeBuildInput {
            sessions,
            presence: HashMap::new(),
            jobs: Vec::new(),
            owners: Vec::new(),
            now: at(NOW),
            edge_rules,
            pinned_coordinators: Vec::new(),
        })
    }

    /// Task 4 amendment (2026-08-16): all four rule combinations, on the
    /// SAME fixture, asserting the semantic difference each toggle makes —
    /// exactly what the operator wants to compare before picking one.
    /// Session ids are deliberately chosen so alphabetical order is the
    /// OPPOSITE of chronological order ("aa_reply" < "zz_init" but
    /// zz_init spoke first) — this exposes baseline's tiebreak as
    /// coincidence-dependent rather than accidentally "looking correct".
    #[test]
    fn edge_rule_combinations_produce_different_topologies_on_the_same_fixture() {
        let (init, reply) = init_and_reply_sessions();

        // Baseline (both off): direction/content-agnostic "latest edge
        // wins" creates a real mutual cycle (each session's own most
        // recent inbound message names the other) — break_cycles' id-based
        // tiebreak resolves it, but which one wins is pure alphabetical
        // luck, not semantics. Here it happens to crown the REPLIER.
        let rows = tree_with_rules(vec![init.clone(), reply.clone()], EdgeRules::default());
        let top = rows.iter().find(|r| r.depth == 0).expect("someone must anchor the tree");
        assert_eq!(top.label, "AReply", "baseline's tiebreak picks the replier here, by alphabetical accident");
        assert!(rows.iter().any(|r| r.depth == 1 && r.label == "ZInit"));

        // initiator_wins alone: the TRUE first sender always wins,
        // independent of id ordering or which direction's message is
        // more recent.
        let rows = tree_with_rules(vec![init.clone(), reply.clone()], EdgeRules { initiator_wins: true, marker_only: false });
        let top = rows.iter().find(|r| r.depth == 0).expect("someone must anchor the tree");
        assert_eq!(top.label, "ZInit", "the real initiator wins under rule 1, not the alphabetically-lucky one");
        assert!(rows.iter().any(|r| r.depth == 1 && r.label == "AReply"));

        // marker_only alone (NO direction lock): whichever message merely
        // LOOKS like a dispatch wins, regardless of who actually spoke
        // first -- here that's the reply, so the REPLIER ends up
        // "controlling" the session that actually initiated the
        // conversation. This is exactly why marker_only in isolation is
        // not a full fix on its own.
        let rows = tree_with_rules(vec![init.clone(), reply.clone()], EdgeRules { initiator_wins: false, marker_only: true });
        let top = rows.iter().find(|r| r.depth == 0).expect("someone must anchor the tree");
        assert_eq!(top.label, "AReply", "marker_only alone still lets a marker-shaped REPLY win control");
        assert!(rows.iter().any(|r| r.depth == 1 && r.label == "ZInit"));

        // Both together (the operator's original combined design, task 11
        // fix applied): the RAW first sender (ZInit, unmarked "嗨") no
        // longer claims initiator status just for having spoken first --
        // only MARKED messages compete for that. AReply's later "[派工]"
        // is the pair's only marked message, so AReply -- not ZInit -- is
        // the initiator, and its dispatch to ZInit creates the edge. This
        // is the same shape as the real doc7 bug this task fixes: an
        // earlier unmarked message must never block a later genuine
        // dispatch from the opposite direction.
        let rows = tree_with_rules(vec![init, reply], EdgeRules { initiator_wins: true, marker_only: true });
        let top = rows.iter().find(|r| r.depth == 0).expect("AReply's marked dispatch must anchor the pair");
        assert_eq!(top.label, "AReply", "the first MARKED sender wins, not the raw-first (but unmarked) sender");
        assert!(rows.iter().any(|r| r.depth == 1 && r.label == "ZInit"));
    }

    /// Task 4: real non-dispatch bodies (a completion report and a recall
    /// — the operator's own explicit non-edge examples) must never create
    /// an edge under `marker_only`, even though under the baseline rules
    /// they would (baseline doesn't look at content at all).
    #[test]
    fn marker_only_rejects_completion_reports_and_recalls_as_non_edges() {
        let content = |body: &str| {
            format!(
                r#"{{"type":"custom-title","customTitle":"Worker","sessionId":"w1"}}
{{"type":"user","timestamp":"2026-08-16T11:59:30Z","message":{{"content":"<cross-session-message from=\"x\" name=\"Ctrl\">{body}</cross-session-message>"}}}}
{{"type":"assistant","timestamp":"2026-08-16T11:59:40Z","message":{{"model":"claude-sonnet-5"}}}}"#
            )
        };
        let ctrl = session("c1", "Ctrl", "2026-08-16T11:59:00Z");

        for body in ["【調查完工】結果如下", "[撤令|來自 Ctrl] 立即停止"] {
            let worker = build_session_summary("slug", "w1", &content(body), &[], at(NOW), crate::claude_sessions::CLAUDE_ALIVE_WINDOW_SECS).unwrap();
            let rows = tree_with_rules(vec![ctrl.clone(), worker], EdgeRules { initiator_wins: false, marker_only: true });
            let worker_row = rows.iter().find(|r| r.label == "Worker").unwrap();
            assert_eq!(worker_row.depth, 0, "body {body:?} must not create an edge under marker_only");
        }
    }

    /// Task 4, rule 3: a pinned coordinator never renders as anyone's
    /// child, even when another session's dispatch would otherwise have
    /// claimed it.
    #[test]
    fn pinned_coordinator_never_renders_as_a_child() {
        let coordinator = session("doc1", "Doc1 主管", "2026-08-16T11:59:00Z");
        let other = session_with_edge("other", "Some Worker", "2026-08-16T11:59:00Z", "Doc1 主管", "2026-08-16T11:55:00Z");
        // "Doc1 主管" dispatching to "other" is fine and expected (other
        // nests under Doc1) -- the guarantee under test is the REVERSE:
        // build a second scenario where something claims Doc1 as ITS
        // child and confirm it's rejected.
        let claims_doc1 = session_with_edge("claimer", "Claimer", "2026-08-16T11:59:00Z", "Doc1 主管", "2026-08-16T11:50:00Z");
        let doc1_replies = session_with_edge("doc1", "Doc1 主管", "2026-08-16T11:58:00Z", "Claimer", "2026-08-16T11:55:00Z");

        let mut input = empty_input(vec![claims_doc1, doc1_replies]);
        input.pinned_coordinators = vec!["Doc1 主管".to_owned()];
        let rows = build_tree(input);
        let doc1_row = rows.iter().find(|r| r.label == "Doc1 主管").unwrap();
        assert_eq!(doc1_row.depth, 0, "a pinned coordinator must stay top-level even though Claimer's message would otherwise have claimed it");

        // Sanity: the same non-pinned pair (from the earlier fixture)
        // still lets Doc1 be someone's controller normally.
        let rows_unpinned = build_tree(empty_input(vec![coordinator, other]));
        let child = rows_unpinned.iter().find(|r| r.label == "Some Worker").unwrap();
        assert_eq!(child.depth, 1, "Doc1 主管 can still dispatch to others normally");
    }

    /// Task 4, rule 3: a pinned coordinator's edge wins over a non-pinned
    /// one for the same child, even when the non-pinned edge is MORE
    /// recent — pinned status overrides recency, not the other way round.
    #[test]
    fn pinned_coordinator_edge_takes_precedence_over_a_more_recent_non_pinned_one() {
        let pinned = session("doc1", "Doc1 主管", "2026-08-16T11:59:00Z");
        let non_pinned = session("doc2", "Doc2", "2026-08-16T11:59:00Z");
        let content = r#"{"type":"custom-title","customTitle":"Target","sessionId":"z1"}
{"type":"user","timestamp":"2026-08-16T11:50:00Z","message":{"content":"<cross-session-message from=\"x\" name=\"Doc1 主管\">hi</cross-session-message>"}}
{"type":"user","timestamp":"2026-08-16T11:58:00Z","message":{"content":"<cross-session-message from=\"x\" name=\"Doc2\">hi, more recently</cross-session-message>"}}
{"type":"assistant","timestamp":"2026-08-16T11:59:00Z","message":{"model":"claude-sonnet-5"}}"#;
        let target = build_session_summary("slug", "z1", content, &[], at(NOW), crate::claude_sessions::CLAUDE_ALIVE_WINDOW_SECS).unwrap();

        let mut input = empty_input(vec![pinned, non_pinned, target]);
        input.pinned_coordinators = vec!["Doc1 主管".to_owned()];
        let rows = build_tree(input);
        let target_row = rows.iter().find(|r| r.label == "Target").unwrap();
        assert_eq!(target_row.depth, 1);
        // Rows are emitted as (controller, its own children...) blocks in
        // top-level order — so "Target sits between Doc1's row and Doc2's
        // row" is a direct, rigorous proof it's Doc1's child, not Doc2's
        // (a looser "appears somewhere after Doc1" check wouldn't rule out
        // it actually belonging to Doc2's block).
        let doc1_index = rows.iter().position(|r| r.label == "Doc1 主管").unwrap();
        let doc2_index = rows.iter().position(|r| r.label == "Doc2").unwrap();
        let target_index = rows.iter().position(|r| r.label == "Target").unwrap();
        assert!(
            doc1_index < target_index && target_index < doc2_index,
            "Target must be grouped inside Doc1 主管's block (doc1_index={doc1_index}, target_index={target_index}, doc2_index={doc2_index}): {rows:?}"
        );
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
