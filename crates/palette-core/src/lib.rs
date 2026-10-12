//! Palette engine: indexing, fuzzy search, usage ranking, actions, icons.
//! The WinUI 3 shell is a thin layer over this crate, through the C ABI in `ffi`.

pub mod actions;
pub mod ffi;
pub mod iconsrc;
pub mod index;
pub mod model;
pub mod paths;
pub mod scan;
pub mod search;
pub mod semantic;
pub mod usage;
pub mod wins;

use std::sync::Arc;

/// Called from background threads when new results/index data are available.
pub type Notify = Arc<dyn Fn() + Send + Sync>;
