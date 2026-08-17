use cccog_bar_core::refresh::snapshot_dispatch_root;
use cccog_bar_ffi::{control_snapshot_json, envelope_from_parts, poll_remote_quotas_json, poll_remote_quotas_with_codex};
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

    // --poll-quotas-app (task 17, 2026-08-18): unlike --poll-quotas below
    // (a direct, un-cached `poll_remote_quotas_with_codex` call), this goes
    // through `poll_remote_quotas_json` — the EXACT same entry point
    // `cccog_bar_poll_quotas`/`NativeQuotaClient.TryPoll` calls in the real
    // app, TTL/backoff resilience cache and all. Needed because the two
    // paths can genuinely disagree: diagnosing why the live app's Claude
    // card stayed frozen required seeing the wire JSON the resilience layer
    // actually produces, not just the raw fetch result.
    if std::env::args_os().any(|arg| arg == "--poll-quotas-app") {
        let user_profile = std::env::var_os("USERPROFILE").map(PathBuf::from);
        let manual = std::env::args_os().any(|arg| arg == "--manual");
        let input = serde_json::json!({
            "codexSessionsPath": user_profile.as_ref().map(|home| home.join(".codex").join("sessions").to_string_lossy().into_owned()),
            "claudeCredentialPath": user_profile.as_ref().map(|home| home.join(".claude").join(".credentials.json").to_string_lossy().into_owned()),
            "grokAuthPath": user_profile.as_ref().map(|home| home.join(".grok").join("auth.json").to_string_lossy().into_owned()),
            "manual": manual,
            "now": chrono::Utc::now().timestamp(),
        });
        println!("{}", poll_remote_quotas_json(&input.to_string()));
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
        // Task 17 (2026-08-18): this used to hardcode `now: 0`, which meant
        // `fetch_claude_quota`'s own `expires_at <= now` expiry check could
        // (almost) never be true — a genuinely expired credential would
        // silently skip the refresh-token flow and go straight to a bare
        // 401 against the wrong (stale) access token, testing a DIFFERENT
        // code path than the real app's own `now`-aware call ever hits.
        // Found while diagnosing the real app's own stale Claude card: this
        // debug tool couldn't even exercise the refresh path to compare.
        let now = chrono::Utc::now().timestamp().max(0) as u64;
        poll_remote_quotas_with_codex(codex.as_deref(), claude.as_deref(), grok.as_deref(), now)
    } else {
        Vec::new()
    };
    println!(
        "{}",
        envelope_from_parts(&snapshot, &quota_cards, &diagnostics)
    );
}
