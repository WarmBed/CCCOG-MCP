use serde::Deserialize;

#[derive(Debug, Clone, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct InboxEntry {
    pub id: Option<String>,
    pub from_role: Option<String>,
    pub from_provider: Option<String>,
    pub from_session_id: Option<String>,
    pub to_role: Option<String>,
    pub to_provider: Option<String>,
    pub to_session_id: Option<String>,
    pub content: Option<String>,
    pub status: Option<String>,
    pub job_id: Option<String>,
    pub created_at: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InboxParse {
    pub entries: Vec<InboxEntry>,
    pub diagnostics: Vec<String>,
    pub torn_tail: bool,
}

/// Parse JSONL without making a torn final write fatal to the rest of the
/// snapshot.  Content is returned as data only; callers must not execute it.
pub fn parse_jsonl(input: impl AsRef<str>) -> InboxParse {
    let input = input.as_ref();
    let ends_with_newline = input.ends_with('\n');
    let mut result = InboxParse {
        entries: Vec::new(),
        diagnostics: Vec::new(),
        torn_tail: false,
    };

    for (index, line) in input.lines().enumerate() {
        if line.trim().is_empty() {
            continue;
        }

        match serde_json::from_str::<InboxEntry>(line) {
            Ok(entry) => result.entries.push(entry),
            Err(_) if !ends_with_newline && index == input.lines().count() - 1 => {
                result.torn_tail = true;
            }
            Err(_) => result
                .diagnostics
                .push(format!("invalid JSONL line {}", index + 1)),
        }
    }

    result
}
