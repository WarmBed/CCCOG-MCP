use cccog_bar_ffi::{envelope_from_json, BackgroundQuotaPoller, CCCOG_BAR_SCHEMA_VERSION};
use serde_json::Value;
use std::ffi::{CStr, CString};

#[test]
fn snapshot_envelope_is_versioned_and_contains_all_sections() {
    let output = envelope_from_json(
        r#"{"snapshot":{"nodes":[],"edges":[]},"quotaCards":[],"diagnostics":["fixture"]}"#,
    );
    let value: Value = serde_json::from_str(&output).expect("JSON envelope");
    assert_eq!(value["schemaVersion"], CCCOG_BAR_SCHEMA_VERSION);
    assert_eq!(value["ok"], true);
    assert!(value["snapshot"].is_object());
    assert!(value["quotaCards"].is_array());
    assert_eq!(value["diagnostics"][0], "fixture");
}

#[test]
fn malformed_and_null_c_inputs_return_structured_errors() {
    let malformed = envelope_from_json("not-json");
    let value: Value = serde_json::from_str(&malformed).unwrap();
    assert_eq!(value["ok"], false);
    assert_eq!(value["error"]["code"], "invalid_input");

    let null = unsafe { cccog_bar_ffi::cccog_bar_snapshot_json(std::ptr::null()) };
    assert!(!null.is_null());
    let value: Value =
        serde_json::from_str(unsafe { CStr::from_ptr(null) }.to_str().unwrap()).unwrap();
    assert_eq!(value["ok"], false);
    unsafe { cccog_bar_ffi::cccog_bar_free_string(null) };
}

#[test]
fn repeated_calls_are_freeable_without_retaining_process_state() {
    for _ in 0..128 {
        let input = CString::new(r#"{"snapshot":{},"quotaCards":[],"diagnostics":[]}"#).unwrap();
        let output = unsafe { cccog_bar_ffi::cccog_bar_snapshot_json(input.as_ptr()) };
        assert!(!output.is_null());
        let text = unsafe { CStr::from_ptr(output) }.to_str().unwrap();
        assert!(text.contains("schemaVersion"));
        unsafe { cccog_bar_ffi::cccog_bar_free_string(output) };
    }
}

#[test]
fn envelope_rejects_non_object_snapshot_with_bounded_error() {
    let output = envelope_from_json(r#"{"snapshot":[],"quotaCards":[],"diagnostics":[]}"#);
    let value: Value = serde_json::from_str(&output).unwrap();
    assert_eq!(value["ok"], false);
    assert_eq!(value["error"]["code"], "invalid_input");
    assert!(output.len() < 4096);
}

#[test]
fn background_quota_poller_has_a_hard_sixty_second_floor() {
    let mut poller = BackgroundQuotaPoller::new(1);
    assert!(poller.poll(0, None, None).is_some());
    assert!(poller.poll(59, None, None).is_none());
    assert!(poller.poll(60, None, None).is_some());
}
