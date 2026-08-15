//! Read-only parsing primitives for CCCOG-Bar.
//!
//! The core deliberately has no Windows/UI dependency.  It consumes bounded
//! file snapshots and returns data-only values for a later graph reducer.

pub mod dispatch;
pub mod graph;
pub mod inbox;
pub mod owners;
pub mod privacy;
pub mod quota;
pub mod refresh;
pub mod usage;
