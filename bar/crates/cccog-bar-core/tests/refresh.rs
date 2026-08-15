use cccog_bar_core::refresh::{AtomicSnapshot, DebounceGate};

#[test]
fn refresh_gate_coalesces_bursts_until_debounce_window() {
    let mut gate = DebounceGate::new(100);
    gate.notify(1_000);
    gate.notify(1_020);
    gate.notify(1_040);
    assert!(!gate.due(1_139));
    assert!(gate.due(1_140));
    assert_eq!(gate.take(1_140), Some(1));
    assert!(!gate.due(1_141));
}

#[test]
fn atomic_snapshot_publishes_complete_replacements() {
    let snapshot = AtomicSnapshot::new(br#"{"generation":1}"#.to_vec());
    assert_eq!(snapshot.load(), br#"{"generation":1}"#);
    snapshot.publish(br#"{"generation":2,"nodes":["complete"]}"#.to_vec());
    assert_eq!(snapshot.load(), br#"{"generation":2,"nodes":["complete"]}"#);
}

#[test]
fn dispatch_root_scanner_builds_real_job_and_owner_graph_without_archive() {
    let root = tempfile::tempdir().expect("fixture root");
    std::fs::create_dir_all(root.path().join("jobs/job-1")).unwrap();
    std::fs::create_dir_all(root.path().join("owners")).unwrap();
    std::fs::create_dir_all(root.path().join("jobs/cccg-archive/job-old")).unwrap();
    std::fs::write(
        root.path().join("jobs/job-1/status.json"),
        r#"{"jobId":"job-1","provider":"codex","sessionId":"s-1","status":"running","createdAt":"2026-08-15T01:00:00Z","callerLabel":"claude/session"}"#,
    )
    .unwrap();
    std::fs::write(
        root.path().join("jobs/job-1/prompt.txt"),
        "do the safe task\n",
    )
    .unwrap();
    std::fs::write(
        root.path().join("jobs/cccg-archive/job-old/status.json"),
        r#"{"jobId":"old","provider":"codex","sessionId":"archived","status":"running"}"#,
    )
    .unwrap();
    std::fs::write(
        root.path().join("owners/owner-1.json"),
        r#"{"schemaVersion":1,"provider":"claude","sessionId":"owner-session"}"#,
    )
    .unwrap();
    std::fs::write(root.path().join("owners/owner-1.lock"), "live").unwrap();

    let snapshot = cccog_bar_core::refresh::snapshot_dispatch_root(root.path());
    assert!(snapshot.edges.iter().any(|edge| edge.id == "job:job-1"));
    assert!(snapshot.edges.iter().any(|edge| edge.id == "owner:owner-1"));
    assert!(!snapshot
        .nodes
        .iter()
        .any(|node| node.session_id.as_deref() == Some("archived")));
}

#[test]
fn bounded_fixture_scan_handles_many_jobs_without_unbounded_work() {
    let root = tempfile::tempdir().expect("fixture root");
    for index in 0..300 {
        let job = root.path().join(format!("jobs/job-{index}"));
        std::fs::create_dir_all(&job).unwrap();
        std::fs::write(
            job.join("status.json"),
            format!(
                r#"{{"jobId":"job-{index}","provider":"codex","sessionId":"session-{index}","status":"succeeded"}}"#
            ),
        )
        .unwrap();
        std::fs::write(job.join("prompt.txt"), "bounded fixture\n").unwrap();
    }
    let started = std::time::Instant::now();
    let snapshot = cccog_bar_core::refresh::snapshot_dispatch_root(root.path());
    assert_eq!(snapshot.edges.len(), 300);
    assert!(started.elapsed() < std::time::Duration::from_secs(2));
}
