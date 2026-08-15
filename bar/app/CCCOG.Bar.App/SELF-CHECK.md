# CCCOG-Bar Wave 5 self-check

- [x] Unpackaged WinUI 3 project targets `win-x64` and sets `PerMonitorV2`.
- [x] Tray icon owns a single topmost dashboard window; left click toggles it.
- [x] Graph renders caller/session nodes, dispatch edges, bounded task labels,
  provider filter, and a running/active opacity animation.
- [x] Quota panel renders label, used-percent bar, reset text, and stale badge.
- [x] Default graph view is `Active + 2h`; the visual self-check showed
  separated caller/session nodes, curved status-colored edges, no task text
  painted on edges, and completed dispatches grouped with `×N` badges
  (21 candidate jobs reduced to 7 visible dispatch edges on the live snapshot).
- [x] Screenshot self-check at 21:38 on 2026-08-15 showed Codex
  `37% used` with a concrete reset time; Claude reported `stale: OAuth refresh
  rejected` and Grok reported `stale: quota HTTP 401`.
- [x] Local scanner excludes paths below `cccg-archive` and skips torn JSON.
- [x] File watcher and quota worker marshal every result back through
  `DispatcherQueue.TryEnqueue`; active graph storyboards are stopped before
  replacement.
- [x] `UnhandledException` writes expanded exception/HResult/inner-exception
  evidence to `bar-crash.log` beside the executable and handles the event.
- [x] Final x64 executable soak: PID 50036 stayed responsive from
  21:37:27 through 21:40:01 on 2026-08-15 (154 seconds, including watcher
  churn and the 60-second quota tick); `bar-crash.log` was absent throughout.
- [x] The environment's native `computer-use` window pipe was unavailable, so
  the screenshot check used Win32 `PrintWindow` against the real executable.
- [ ] Final visual spacing, keyboard narration, contrast, DPI scaling, and
  accessibility remain user acceptance items.
