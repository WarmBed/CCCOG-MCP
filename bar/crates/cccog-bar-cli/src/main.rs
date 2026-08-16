use cccog_bar_core::refresh::snapshot_dispatch_root;
use cccog_bar_ffi::{control_snapshot_json, envelope_from_parts, poll_remote_quotas_with_codex};
use std::path::PathBuf;

fn dispatch_root() -> PathBuf {
    let mut args = std::env::args_os().skip(1);
    while let Some(arg) = args.next() {
        if arg == "--dispatch-root" {
            if let Some(root) = args.next() {
                return PathBuf::from(root);
            }
        }
    }
    if let Some(local_app_data) = std::env::var_os("LOCALAPPDATA") {
        return PathBuf::from(local_app_data).join("CCCG").join("dispatch");
    }
    PathBuf::from(".")
}

/// The raw string value following `flag` in argv, if present — shared by
/// `--edge-rules`/`--coordinators` below.
fn flag_value(flag: &str) -> Option<String> {
    let mut args = std::env::args_os();
    while let Some(arg) = args.next() {
        if arg == flag {
            return args.next().map(|value| value.to_string_lossy().into_owned());
        }
    }
    None
}

/// `--edge-rules initiator,marker` / `--edge-rules marker` / `--edge-rules
/// none` — a comma list of `initiator`/`marker` (any combination, any
/// order), or `none`/anything else for the baseline (both off). Returns
/// `None` only when the flag itself wasn't passed at all, so the CLI's
/// default (no flag) falls through to whatever `bar-settings.json` says —
/// same production path the app uses — while an explicit flag, even
/// `--edge-rules none`, always bypasses the settings file for that run
/// (task 4 amendment: "compare configurations WITHOUT touching the
/// settings file").
fn edge_rules_override() -> Option<serde_json::Value> {
    let raw = flag_value("--edge-rules")?;
    let initiator_wins = raw.split(',').any(|part| part.trim() == "initiator");
    let marker_only = raw.split(',').any(|part| part.trim() == "marker");
    Some(serde_json::json!({ "initiatorWins": initiator_wins, "markerOnly": marker_only }))
}

/// `--coordinators "Doc1 主管"` / `--coordinators "Doc1 主管,Doc2"` — a
/// comma-separated list of exact session titles. Absent flag = fall
/// through to `bar-settings.json`'s `flow.coordinators`, same rationale as
/// `edge_rules_override`.
fn coordinators_override() -> Option<serde_json::Value> {
    let raw = flag_value("--coordinators")?;
    let names: Vec<String> = raw.split(',').map(|part| part.trim().to_owned()).filter(|part| !part.is_empty()).collect();
    Some(serde_json::json!(names))
}

fn main() {
    let root = dispatch_root();

    // --control: print the windowed/aggregated Flow view (the same payload
    // the shell's NativeControlClient consumes) instead of the raw graph
    // snapshot — useful for checking row counts and target-title resolution
    // against a real dispatch root without launching the WinUI app.
    //
    // --edge-rules / --coordinators (task 4 amendment, 2026-08-16): compare
    // Flow control-edge configurations against real transcripts without
    // touching bar-settings.json or restarting the app — e.g.:
    //   cccog-bar-cli --control --edge-rules none
    //   cccog-bar-cli --control --edge-rules initiator
    //   cccog-bar-cli --control --edge-rules marker
    //   cccog-bar-cli --control --edge-rules initiator,marker
    //   cccog-bar-cli --control --edge-rules initiator,marker --coordinators "Doc1 主管"
    if std::env::args_os().any(|arg| arg == "--control") {
        let now = chrono::Utc::now();
        let mut input = serde_json::json!({
            "dispatchRoot": root.to_string_lossy(),
            "now": now.timestamp(),
        });
        if let Some(edge_rules) = edge_rules_override() {
            input["edgeRules"] = edge_rules;
        }
        if let Some(coordinators) = coordinators_override() {
            input["coordinators"] = coordinators;
        }
        println!("{}", control_snapshot_json(&input.to_string()));
        return;
    }

    let snapshot = snapshot_dispatch_root(&root);
    let diagnostics = snapshot.diagnostics.clone();
    let poll = std::env::args_os().any(|arg| arg == "--poll-quotas");
    let quota_cards = if poll {
        let user_profile = std::env::var_os("USERPROFILE").map(PathBuf::from);
        let codex = user_profile
            .as_ref()
            .map(|home| home.join(".codex").join("sessions"));
        let claude = user_profile
            .as_ref()
            .map(|home| home.join(".claude").join(".credentials.json"));
        let grok = user_profile.map(|home| home.join(".grok").join("auth.json"));
        poll_remote_quotas_with_codex(codex.as_deref(), claude.as_deref(), grok.as_deref(), 0)
    } else {
        Vec::new()
    };
    println!(
        "{}",
        envelope_from_parts(&snapshot, &quota_cards, &diagnostics)
    );
}
