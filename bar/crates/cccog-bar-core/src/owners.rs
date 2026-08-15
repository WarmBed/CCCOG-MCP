use serde::Deserialize;
use std::path::Path;

#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct OwnerRegistration {
    pub schema_version: Option<i32>,
    pub provider: String,
    pub session_id: Option<String>,
    pub cwd: Option<String>,
    pub owner_pid: Option<u32>,
    pub spool_dir: Option<String>,
    pub started_at: Option<String>,
}

pub fn parse_owner(input: &str) -> Result<OwnerRegistration, serde_json::Error> {
    serde_json::from_str(input)
}

/// Archive paths are invisible to normal bar operations.  Split both slash
/// styles because fixtures and Windows paths are also parsed on Unix CI.
pub fn is_archived_path(path: &Path) -> bool {
    path.to_string_lossy()
        .split(['\\', '/'])
        .any(|component| component.eq_ignore_ascii_case("cccg-archive"))
}
