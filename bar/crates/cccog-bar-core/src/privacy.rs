/// Read a bounded, first-line-only task summary from a generated `prompt.txt`.
/// Prompt text is untrusted data; this function never interprets it as a
/// command or returns lines after the first task line.
pub fn summarize_prompt(bytes: &[u8], max_read: usize, max_bytes: usize) -> String {
    if max_bytes == 0 || max_read == 0 {
        return String::new();
    }

    let bounded = &bytes[..bytes.len().min(max_read)];
    let text = String::from_utf8_lossy(bounded);
    let mut lines = text.lines();
    let first = lines.next().unwrap_or_default();
    let task = if first.starts_with("[CCCG dispatch from ") {
        lines
            .find(|line| !line.trim().is_empty())
            .unwrap_or_default()
    } else if !first.trim().is_empty() {
        first
    } else {
        lines
            .find(|line| !line.trim().is_empty())
            .unwrap_or_default()
    };

    let escaped: String = task
        .chars()
        .flat_map(|character| {
            if character.is_control() {
                format!("\\u{{{:04x}}}", character as u32)
                    .chars()
                    .collect::<Vec<_>>()
            } else {
                vec![character]
            }
        })
        .collect();
    truncate_utf8(&escaped, max_bytes)
}

fn truncate_utf8(value: &str, max_bytes: usize) -> String {
    if value.len() <= max_bytes {
        return value.to_owned();
    }

    const ELLIPSIS: &str = "…";
    if max_bytes < ELLIPSIS.len() {
        let mut end = max_bytes.min(value.len());
        while end > 0 && !value.is_char_boundary(end) {
            end -= 1;
        }
        return value[..end].to_owned();
    }

    let limit = max_bytes - ELLIPSIS.len();
    let mut end = limit.min(value.len());
    while end > 0 && !value.is_char_boundary(end) {
        end -= 1;
    }
    format!("{}{}", &value[..end], ELLIPSIS)
}
