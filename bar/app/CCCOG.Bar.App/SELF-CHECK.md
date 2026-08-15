# CCCOG-Bar Wave 5 self-check

- [x] Unpackaged WinUI 3 project targets `win-x64` and sets `PerMonitorV2`.
- [x] Tray icon owns a single topmost dashboard window; left click toggles it.
- [x] Graph renders caller/session nodes, dispatch edges, bounded task labels,
  provider filter, and a running/active opacity animation.
- [x] Quota panel renders label, used-percent bar, reset text, and stale badge.
- [x] Local scanner excludes paths below `cccg-archive` and skips torn JSON.
- [x] File watcher and quota worker marshal every result back through
  `DispatcherQueue.TryEnqueue`; active graph storyboards are stopped before
  replacement.
- [x] `UnhandledException` writes expanded exception/HResult/inner-exception
  evidence to `bar-crash.log` beside the executable and handles the event.
- [x] 90-second x64 executable soak after the crash fix: PID 44184 stayed
  alive from 20:59 through 21:01 on 2026-08-15, including watcher churn and
  the first quota tick; `bar-crash.log` was absent throughout.
- [ ] Final visual spacing, keyboard narration, contrast, DPI scaling, and
  accessibility remain user acceptance items.
