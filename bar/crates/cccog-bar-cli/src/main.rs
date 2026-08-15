use cccog_bar_core::refresh::snapshot_dispatch_root;
use cccog_bar_ffi::{envelope_from_parts, poll_remote_quotas_with_codex};
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

fn main() {
    let root = dispatch_root();
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
