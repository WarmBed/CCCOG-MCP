//! Bounded file refresh primitives used by the shell and debug CLI.
//!
//! The watcher-facing types intentionally avoid an async/runtime dependency:
//! a platform watcher only has to call `notify`, and the UI can publish a
//! completely serialized snapshot through the lock in one replacement.

use crate::dispatch::parse_status;
use crate::graph::{build_snapshot, DispatchRecord, GraphSnapshot, OwnerEdgeInput};
use crate::owners::{is_archived_path, parse_owner};
use crate::privacy::summarize_prompt;
use std::path::{Path, PathBuf};
use std::sync::{Arc, RwLock};

#[derive(Debug, Clone)]
pub struct DebounceGate {
    debounce_ms: u64,
    last_event_ms: Option<u64>,
    pending: bool,
    generation: u64,
}

impl DebounceGate {
    pub fn new(debounce_ms: u64) -> Self {
        Self {
            debounce_ms,
            last_event_ms: None,
            pending: false,
            generation: 0,
        }
    }

    pub fn notify(&mut self, now_ms: u64) {
        self.last_event_ms = Some(now_ms);
        self.pending = true;
    }

    pub fn due(&self, now_ms: u64) -> bool {
        self.pending
            && self
                .last_event_ms
                .is_some_and(|last| now_ms.saturating_sub(last) >= self.debounce_ms)
    }

    /// Consume a quiet period and return the monotonically increasing refresh
    /// generation.  A burst of file events produces one generation.
    pub fn take(&mut self, now_ms: u64) -> Option<u64> {
        if !self.due(now_ms) {
            return None;
        }
        self.pending = false;
        self.last_event_ms = None;
        self.generation = self.generation.saturating_add(1);
        Some(self.generation)
    }
}

#[derive(Clone, Debug)]
pub struct AtomicSnapshot {
    bytes: Arc<RwLock<Vec<u8>>>,
}

impl AtomicSnapshot {
    pub fn new(initial: Vec<u8>) -> Self {
        Self {
            bytes: Arc::new(RwLock::new(initial)),
        }
    }

    pub fn publish(&self, next: Vec<u8>) {
        *self.bytes.write().expect("snapshot lock poisoned") = next;
    }

    pub fn load(&self) -> Vec<u8> {
        self.bytes.read().expect("snapshot lock poisoned").clone()
    }
}

/// Read the bounded, non-archived dispatch files used by CCCOG-Bar.  A bad
/// individual file becomes a diagnostic and does not prevent other jobs from
/// appearing in the graph.
pub fn snapshot_dispatch_root(root: &Path) -> GraphSnapshot {
    let mut jobs = Vec::new();
    let mut owners = Vec::new();
    let mut diagnostics = Vec::new();

    collect_status_files(&root.join("jobs"), &mut jobs, &mut diagnostics);
    collect_owner_files(&root.join("owners"), &mut owners, &mut diagnostics);

    let mut snapshot = build_snapshot(crate::graph::GraphInput {
        jobs,
        owners,
        usage: Vec::new(),
    });
    snapshot.diagnostics.extend(diagnostics);
    snapshot
}

fn collect_status_files(dir: &Path, jobs: &mut Vec<DispatchRecord>, diagnostics: &mut Vec<String>) {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if is_archived_path(&path) {
            continue;
        }
        if path.is_dir() {
            collect_status_files(&path, jobs, diagnostics);
            continue;
        }
        if path.file_name().and_then(|name| name.to_str()) != Some("status.json") {
            continue;
        }
        let Ok(raw) = std::fs::read_to_string(&path) else {
            diagnostics.push("unable to read dispatch status".to_owned());
            continue;
        };
        let Ok(status) = parse_status(&raw) else {
            diagnostics.push("invalid dispatch status ignored".to_owned());
            continue;
        };
        let prompt = path
            .parent()
            .map(|parent| parent.join("prompt.txt"))
            .and_then(|prompt| std::fs::read(prompt).ok())
            .map(|bytes| summarize_prompt(&bytes, 8 * 1024, 240))
            .unwrap_or_default();
        jobs.push(DispatchRecord {
            job_id: status.job_id,
            provider: status.provider,
            session_id: status.session_id,
            requested_session_id: status.requested_session_id,
            caller_label: status.caller_label,
            hop_source: status.hop_source,
            hop_chain: status.hop_chain,
            status: status.status,
            started_at: status.started_at,
            finished_at: status.finished_at,
            task_summary: prompt,
        });
    }
}

fn collect_owner_files(
    dir: &Path,
    owners: &mut Vec<OwnerEdgeInput>,
    diagnostics: &mut Vec<String>,
) {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if is_archived_path(&path) {
            continue;
        }
        if path.is_dir() {
            collect_owner_files(&path, owners, diagnostics);
            continue;
        }
        if path.extension().and_then(|extension| extension.to_str()) != Some("json") {
            continue;
        }
        let Ok(raw) = std::fs::read_to_string(&path) else {
            diagnostics.push("unable to read owner registration".to_owned());
            continue;
        };
        let Ok(owner) = parse_owner(&raw) else {
            diagnostics.push("invalid owner registration ignored".to_owned());
            continue;
        };
        let Some(session_id) = owner.session_id else {
            continue;
        };
        let live = path.with_extension("lock").is_file();
        owners.push(OwnerEdgeInput {
            owner_id: path
                .file_stem()
                .and_then(|stem| stem.to_str())
                .unwrap_or("owner")
                .to_owned(),
            provider: owner.provider,
            session_id,
            live,
        });
    }
}

#[allow(dead_code)]
fn _path_for_diagnostics(path: &Path) -> PathBuf {
    path.to_path_buf()
}
