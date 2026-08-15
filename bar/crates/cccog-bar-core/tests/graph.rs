use cccog_bar_core::graph::{
    build_snapshot, DispatchRecord, GraphInput, OwnerEdgeInput, TokenTotals,
};
use cccog_bar_core::usage::{Provider, UsageSample};

fn sample(key: &str, session: &str, timestamp: &str, input: u64) -> UsageSample {
    UsageSample {
        provider: Provider::Codex,
        session_id: session.to_owned(),
        message_key: key.to_owned(),
        timestamp: Some(timestamp.to_owned()),
        model: Some("synthetic-model".to_owned()),
        input_tokens: input,
        output_tokens: 2,
        cache_read_tokens: 3,
        cache_write_tokens: 1,
        source_path: "synthetic-rollout.jsonl".to_owned(),
        source_offset: 0,
    }
}

#[test]
fn labeled_dispatch_builds_nodes_edge_and_token_attribution() {
    let input = GraphInput {
        jobs: vec![DispatchRecord {
            job_id: "job-1".to_owned(),
            provider: "codex".to_owned(),
            session_id: Some("thread-1".to_owned()),
            requested_session_id: None,
            caller_label: Some("Claude/session-7".to_owned()),
            hop_source: None,
            hop_chain: None,
            status: "succeeded".to_owned(),
            started_at: Some("2026-08-15T01:00:00Z".to_owned()),
            finished_at: Some("2026-08-15T02:00:00Z".to_owned()),
            task_summary: "synthetic task".to_owned(),
        }],
        owners: vec![],
        usage: vec![sample("m1", "thread-1", "2026-08-15T01:30:00Z", 10)],
    };
    let snapshot = build_snapshot(input);
    assert_eq!(snapshot.edges.len(), 1);
    assert_eq!(snapshot.edges[0].source_node_id, "caller:Claude/session-7");
    assert_eq!(snapshot.edges[0].target_node_id, "codex:thread-1");
    assert_eq!(
        snapshot.edges[0].usage,
        TokenTotals {
            input: 10,
            output: 2,
            cache_read: 3,
            cache_write: 1
        }
    );
    assert!(snapshot
        .nodes
        .iter()
        .any(|node| node.id == "codex:thread-1"));
}

#[test]
fn legacy_dispatch_is_explicitly_ambiguous() {
    let snapshot = build_snapshot(GraphInput {
        jobs: vec![DispatchRecord {
            job_id: "job-old".to_owned(),
            provider: "grok".to_owned(),
            session_id: Some("grok-1".to_owned()),
            requested_session_id: None,
            caller_label: None,
            hop_source: Some("user:test@machine".to_owned()),
            hop_chain: Some("human>codex".to_owned()),
            status: "running".to_owned(),
            started_at: None,
            finished_at: None,
            task_summary: "old task".to_owned(),
        }],
        owners: vec![],
        usage: vec![],
    });
    assert_eq!(snapshot.edges[0].identity_confidence, "ambiguous");
    assert!(snapshot.edges[0].source_node_id.starts_with("ambiguous:"));
}

#[test]
fn stale_owner_is_removed_and_live_owner_edge_remains() {
    let snapshot = build_snapshot(GraphInput {
        jobs: vec![],
        owners: vec![
            OwnerEdgeInput {
                owner_id: "stale".to_owned(),
                provider: "codex".to_owned(),
                session_id: "old".to_owned(),
                live: false,
            },
            OwnerEdgeInput {
                owner_id: "live".to_owned(),
                provider: "codex".to_owned(),
                session_id: "new".to_owned(),
                live: true,
            },
        ],
        usage: vec![],
    });
    assert_eq!(snapshot.edges.len(), 1);
    assert_eq!(snapshot.edges[0].id, "owner:live");
}

#[test]
fn resolved_session_reuses_one_job_edge_and_deduplicates_usage() {
    let jobs = vec![
        DispatchRecord {
            job_id: "job-resolve".to_owned(),
            provider: "codex".to_owned(),
            session_id: None,
            requested_session_id: Some("requested".to_owned()),
            caller_label: Some("caller".to_owned()),
            hop_source: None,
            hop_chain: None,
            status: "running".to_owned(),
            started_at: None,
            finished_at: None,
            task_summary: "first".to_owned(),
        },
        DispatchRecord {
            job_id: "job-resolve".to_owned(),
            provider: "codex".to_owned(),
            session_id: Some("resolved".to_owned()),
            requested_session_id: Some("requested".to_owned()),
            caller_label: Some("caller".to_owned()),
            hop_source: None,
            hop_chain: None,
            status: "succeeded".to_owned(),
            started_at: None,
            finished_at: None,
            task_summary: "second".to_owned(),
        },
    ];
    let snapshot = build_snapshot(GraphInput {
        jobs,
        owners: vec![],
        usage: vec![
            sample("same", "resolved", "2026-08-15T01:00:00Z", 5),
            sample("same", "resolved", "2026-08-15T01:00:00Z", 5),
        ],
    });
    assert_eq!(snapshot.edges.len(), 1);
    assert_eq!(snapshot.edges[0].target_node_id, "codex:resolved");
    assert_eq!(snapshot.edges[0].usage.input, 5);
}

#[test]
fn failed_and_queued_edges_do_not_receive_unobserved_usage_and_cycles_are_bounded() {
    let snapshot = build_snapshot(GraphInput {
        jobs: vec![
            DispatchRecord {
                job_id: "failed".to_owned(),
                provider: "codex".to_owned(),
                session_id: Some("a".to_owned()),
                requested_session_id: None,
                caller_label: Some("b".to_owned()),
                hop_source: None,
                hop_chain: None,
                status: "failed".to_owned(),
                started_at: None,
                finished_at: None,
                task_summary: "failed".to_owned(),
            },
            DispatchRecord {
                job_id: "cycle-a".to_owned(),
                provider: "codex".to_owned(),
                session_id: Some("b".to_owned()),
                requested_session_id: None,
                caller_label: Some("codex:a".to_owned()),
                hop_source: None,
                hop_chain: None,
                status: "running".to_owned(),
                started_at: None,
                finished_at: None,
                task_summary: "cycle".to_owned(),
            },
            DispatchRecord {
                job_id: "cycle-b".to_owned(),
                provider: "codex".to_owned(),
                session_id: Some("a".to_owned()),
                requested_session_id: None,
                caller_label: Some("codex:b".to_owned()),
                hop_source: None,
                hop_chain: None,
                status: "running".to_owned(),
                started_at: None,
                finished_at: None,
                task_summary: "cycle".to_owned(),
            },
        ],
        owners: vec![],
        usage: vec![sample("failed-use", "a", "2026-08-15T01:00:00Z", 99)],
    });
    assert_eq!(
        snapshot
            .edges
            .iter()
            .find(|edge| edge.id == "job:failed")
            .unwrap()
            .usage
            .input,
        0
    );
    assert!(snapshot
        .diagnostics
        .iter()
        .any(|diagnostic| diagnostic.contains("cycle")));
}
