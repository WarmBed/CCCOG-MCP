# CCCOG-Bar workspace

This directory contains the platform-neutral Rust core and the later thin
Windows shell.  The first implementation wave is intentionally read-only: it
parses bounded CCCG/provider files and never calls a provider or writes a
dispatch file.

## Money representation

The core uses checked integer **micro-USD (`u64`)** for all monetary values. It
does not use `f64` (or a binary floating-point intermediate) for money. A
provider-reported decimal is parsed into micro-USD with checked arithmetic;
unknown or unpriced values remain `None`. The UI rounds only when formatting a
display string. This keeps totals deterministic across Rust/FFI/WinUI and
avoids floating-point accounting drift.
