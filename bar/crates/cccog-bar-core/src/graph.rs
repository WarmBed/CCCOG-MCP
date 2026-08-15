//! Deterministic graph reduction and token attribution.

use crate::usage::{dedupe_samples, Provider, UsageSample};
use std::collections::{BTreeMap, BTreeSet, HashMap};

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct TokenTotals {
    pub input: u64,
    pub output: u64,
    pub cache_read: u64,
    pub cache_write: u64,
}

impl TokenTotals {
    fn add_sample(&mut self, sample: &UsageSample) {
        self.input = self.input.saturating_add(sample.input_tokens);
        self.output = self.output.saturating_add(sample.output_tokens);
        self.cache_read = self.cache_read.saturating_add(sample.cache_read_tokens);
        self.cache_write = self.cache_write.saturating_add(sample.cache_write_tokens);
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DispatchRecord {
    pub job_id: String,
    pub provider: String,
    pub session_id: Option<String>,
    pub requested_session_id: Option<String>,
    pub caller_label: Option<String>,
    pub hop_source: Option<String>,
    pub hop_chain: Option<String>,
    pub status: String,
    pub started_at: Option<String>,
    pub finished_at: Option<String>,
    pub task_summary: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OwnerEdgeInput {
    pub owner_id: String,
    pub provider: String,
    pub session_id: String,
    pub live: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct GraphInput {
    pub jobs: Vec<DispatchRecord>,
    pub owners: Vec<OwnerEdgeInput>,
    pub usage: Vec<UsageSample>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Node {
    pub id: String,
    pub provider: String,
    pub session_id: Option<String>,
    pub label: String,
    pub kind: String,
    pub state: String,
    pub usage: TokenTotals,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Edge {
    pub id: String,
    pub kind: String,
    pub source_node_id: String,
    pub target_node_id: String,
    pub job_id: Option<String>,
    pub task_summary: String,
    pub status: String,
    pub started_at: Option<String>,
    pub finished_at: Option<String>,
    pub usage: TokenTotals,
    pub identity_confidence: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct GraphSnapshot {
    pub nodes: Vec<Node>,
    pub edges: Vec<Edge>,
    pub totals: TokenTotals,
    pub diagnostics: Vec<String>,
    pub truncated: bool,
}

pub fn build_snapshot(input: GraphInput) -> GraphSnapshot {
    let samples = dedupe_samples(input.usage);
    let mut nodes: BTreeMap<String, Node> = BTreeMap::new();
    let mut edges: BTreeMap<String, Edge> = BTreeMap::new();

    for job in input.jobs {
        let target_session = job
            .session_id
            .clone()
            .or_else(|| job.requested_session_id.clone());
        let target_id = target_session
            .as_deref()
            .map(|session| node_id(&job.provider, session))
            .unwrap_or_else(|| format!("{}:unknown", job.provider));
        ensure_node(
            &mut nodes,
            &target_id,
            &job.provider,
            target_session.as_deref(),
            target_session.as_deref().unwrap_or("unknown"),
            &job.status,
            "session",
        );

        let (source_id, confidence) = source_node(&job);
        ensure_node(
            &mut nodes,
            &source_id,
            "caller",
            None,
            source_id.strip_prefix("caller:").unwrap_or(&source_id),
            "active",
            "caller",
        );

        let edge_id = format!("job:{}", job.job_id);
        if let Some(edge) = edges.get_mut(&edge_id) {
            // A later terminal/resolved status is authoritative, but preserve
            // the original task when the update omitted it.
            edge.target_node_id = target_id;
            edge.status = job.status;
            edge.started_at = job.started_at;
            edge.finished_at = job.finished_at;
            if !job.task_summary.is_empty() {
                edge.task_summary = job.task_summary;
            }
            continue;
        }
        edges.insert(
            edge_id.clone(),
            Edge {
                id: edge_id,
                kind: "dispatch".to_owned(),
                source_node_id: source_id,
                target_node_id: target_id,
                job_id: Some(job.job_id),
                task_summary: job.task_summary,
                status: job.status,
                started_at: job.started_at,
                finished_at: job.finished_at,
                usage: TokenTotals::default(),
                identity_confidence: confidence,
            },
        );
    }

    for owner in input.owners {
        if !owner.live {
            continue;
        }
        let source_id = format!("owner:{}", owner.owner_id);
        let target_id = node_id(&owner.provider, &owner.session_id);
        ensure_node(
            &mut nodes, &source_id, "owner", None, &source_id, "active", "owner",
        );
        ensure_node(
            &mut nodes,
            &target_id,
            &owner.provider,
            Some(&owner.session_id),
            &owner.session_id,
            "owned",
            "session",
        );
        edges.insert(
            format!("owner:{}", owner.owner_id),
            Edge {
                id: format!("owner:{}", owner.owner_id),
                kind: "path_a_owner".to_owned(),
                source_node_id: source_id,
                target_node_id: target_id,
                job_id: None,
                task_summary: String::new(),
                status: "live".to_owned(),
                started_at: None,
                finished_at: None,
                usage: TokenTotals::default(),
                identity_confidence: "exact".to_owned(),
            },
        );
    }

    // Every sample contributes exactly once to its real session node.
    for sample in &samples {
        let target_id = node_id(&provider_name(sample.provider), &sample.session_id);
        if let Some(node) = nodes.get_mut(&target_id) {
            node.usage.add_sample(sample);
        } else {
            ensure_node(
                &mut nodes,
                &target_id,
                &provider_name(sample.provider),
                Some(&sample.session_id),
                &sample.session_id,
                "observed",
                "session",
            );
            nodes.get_mut(&target_id).unwrap().usage.add_sample(sample);
        }
    }

    // Assign a sample to the first deterministic eligible dispatch edge.  This
    // prevents overlapping jobs from multiplying token totals.
    let dispatch_ids: Vec<String> = edges
        .values()
        .filter(|edge| edge.kind == "dispatch")
        .map(|edge| edge.id.clone())
        .collect();
    for sample in &samples {
        for edge_id in &dispatch_ids {
            let edge = edges.get(edge_id).unwrap();
            if !eligible_for_edge(sample, edge) {
                continue;
            }
            edges.get_mut(edge_id).unwrap().usage.add_sample(sample);
            break;
        }
    }

    let mut diagnostics = Vec::new();
    if has_cycle(&edges) {
        diagnostics.push("cycle detected in dispatch graph; traversal bounded".to_owned());
    }
    let mut totals = TokenTotals::default();
    for node in nodes.values() {
        totals.input = totals.input.saturating_add(node.usage.input);
        totals.output = totals.output.saturating_add(node.usage.output);
        totals.cache_read = totals.cache_read.saturating_add(node.usage.cache_read);
        totals.cache_write = totals.cache_write.saturating_add(node.usage.cache_write);
    }

    GraphSnapshot {
        nodes: nodes.into_values().collect(),
        edges: edges.into_values().collect(),
        totals,
        diagnostics,
        truncated: false,
    }
}

fn source_node(job: &DispatchRecord) -> (String, String) {
    if let Some(label) = job
        .caller_label
        .as_deref()
        .filter(|label| !label.is_empty())
    {
        return (format!("caller:{}", label), "exact".to_owned());
    }
    let source = job.hop_source.as_deref().unwrap_or("unknown");
    let chain = job.hop_chain.as_deref().unwrap_or("unknown");
    (
        format!("ambiguous:{}:{}", source, chain),
        "ambiguous".to_owned(),
    )
}

fn eligible_for_edge(sample: &UsageSample, edge: &Edge) -> bool {
    if edge.status == "queued" || edge.status == "failed" {
        return false;
    }
    let Some((provider, session)) = edge.target_node_id.split_once(':') else {
        return false;
    };
    if provider != provider_name(sample.provider) || session != sample.session_id {
        return false;
    }
    if let Some(start) = edge.started_at.as_deref() {
        if sample.timestamp.as_deref().is_some_and(|time| time < start) {
            return false;
        }
    }
    if let Some(end) = edge.finished_at.as_deref() {
        if sample.timestamp.as_deref().is_some_and(|time| time > end) {
            return false;
        }
    }
    true
}

fn ensure_node(
    nodes: &mut BTreeMap<String, Node>,
    id: &str,
    provider: &str,
    session_id: Option<&str>,
    label: &str,
    state: &str,
    kind: &str,
) {
    nodes.entry(id.to_owned()).or_insert_with(|| Node {
        id: id.to_owned(),
        provider: provider.to_owned(),
        session_id: session_id.map(str::to_owned),
        label: label.to_owned(),
        kind: kind.to_owned(),
        state: state.to_owned(),
        usage: TokenTotals::default(),
    });
}

fn node_id(provider: &str, session: &str) -> String {
    format!("{}:{}", provider, session)
}

fn provider_name(provider: Provider) -> String {
    match provider {
        Provider::Codex => "codex".to_owned(),
        Provider::Claude => "claude".to_owned(),
        Provider::Grok => "grok".to_owned(),
    }
}

fn has_cycle(edges: &BTreeMap<String, Edge>) -> bool {
    let mut adjacency: HashMap<String, Vec<String>> = HashMap::new();
    let target_ids: BTreeSet<String> = edges
        .values()
        .map(|edge| edge.target_node_id.clone())
        .collect();
    for edge in edges.values().filter(|edge| edge.kind == "dispatch") {
        let source = edge
            .source_node_id
            .strip_prefix("caller:")
            .filter(|id| target_ids.contains(*id))
            .unwrap_or(&edge.source_node_id)
            .to_owned();
        adjacency
            .entry(source)
            .or_default()
            .push(edge.target_node_id.clone());
    }
    let mut visiting = BTreeSet::new();
    let mut visited = BTreeSet::new();
    for node in adjacency.keys() {
        if dfs_cycle(node, &adjacency, &mut visiting, &mut visited) {
            return true;
        }
    }
    false
}

fn dfs_cycle(
    node: &str,
    adjacency: &HashMap<String, Vec<String>>,
    visiting: &mut BTreeSet<String>,
    visited: &mut BTreeSet<String>,
) -> bool {
    if visiting.contains(node) {
        return true;
    }
    if !visited.insert(node.to_owned()) {
        return false;
    }
    visiting.insert(node.to_owned());
    if let Some(children) = adjacency.get(node) {
        if children
            .iter()
            .any(|child| dfs_cycle(child, adjacency, visiting, visited))
        {
            return true;
        }
    }
    visiting.remove(node);
    false
}
