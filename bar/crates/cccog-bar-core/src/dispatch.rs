use serde::Deserialize;

/// The subset of a CCCG `status.json` needed by the first graph pass.
/// Unknown JSON fields are intentionally ignored for forward compatibility.
#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct DispatchStatus {
    pub job_id: String,
    pub provider: String,
    pub session_id: Option<String>,
    pub requested_session_id: Option<String>,
    pub cwd: Option<String>,
    pub model: Option<String>,
    pub reasoning_effort: Option<String>,
    pub hop_count: Option<i32>,
    pub hop_source: Option<String>,
    pub hop_chain: Option<String>,
    pub caller_label: Option<String>,
    pub action: Option<String>,
    pub status: String,
    pub reason: Option<String>,
    pub allow_new: Option<bool>,
    pub created_at: Option<String>,
    pub started_at: Option<String>,
    pub finished_at: Option<String>,
    pub error: Option<String>,
}

pub fn parse_status(input: &str) -> Result<DispatchStatus, serde_json::Error> {
    serde_json::from_str(input)
}
