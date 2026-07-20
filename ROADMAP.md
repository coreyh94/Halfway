# Halfway Roadmap

## Phase 0 — Technical Spike ✓

Validated:

- [x] WinUI 3 application shell.
- [x] Embedded ConPTY terminal.
- [x] Launching Codex CLI.
- [x] Sending input to Codex.
- [x] Detecting prompt readiness.
- [x] Injecting one safe `[Halfway Alert!]` message.
- [x] Running two independent terminal sessions with Codex launch support.

This phase exists to prove the hardest integration risks before polishing the UI.
Phase 0 was completed with deterministic PowerShell and `cmd.exe` coverage; Codex CLI remains an external runtime prerequisite.

## Phase 1 — MVP Shell ✓

- [x] Native Windows window.
- [x] Information bar.
- [x] Agent/Sub-Agent sidebar.
- [x] Main terminal panel.
- [x] Tabbed sub-agent panel.
- [x] Session creation.
- [x] Session selection.
- [x] Basic status indicators.
- [x] SQLite workspace persistence.

Phase 1 restores durable workspace/session metadata while always starting newly verified processes. It does not restore terminal transcripts or reattach processes.

## Phase 2 — Lifecycle Tracking ✓

- [x] Explicit parent-child registration.
- [x] Running, waiting, completed and failed lifecycle states.
- [x] Durable lifecycle-event ledger.
- [x] Restart-safe alert deduplication.
- [x] Durable completion alert delivery.
- [x] Deterministic batched completions.
- [x] Pending/Reserved/Delivered restart-safe delivery state.

Phase 2 persists eligible lifecycle events, atomically reserves delivery, recovers stale reservations, retries pending alerts after restart, and never requeues delivered alerts. Same-parent completions use a fixed event-driven batching window with atomic delivery state for every member. Readiness-driven tracking distinguishes Running from Waiting while retaining process-driven completion, failure, and disconnection. Schema version 3 adds explicit same-workspace parent-child registration and uses it for live lifecycle and alert routing. Terminal transcripts and prompts remain unpersisted.

## Phase 3 — Usability ✓

- [x] Keyboard navigation.
- [x] Terminal search.
- [x] Workspace restore.
- [x] Unread indicators.
- [x] Resizable panels.
- [x] Light and dark theme.
- [x] Windows notifications for failures where useful.

Phase 3 adds deterministic keyboard navigation, bounded in-memory terminal search, last-workspace restore, transient unread indicators, constrained panel resizing, system light/dark palettes, and best-effort Windows notifications for background failures. None of these usability features persist terminal content or manufacture lifecycle events or terminal messages.

## Phase 4 — Reliability

- [x] Crash-state detection and clean shutdown tracking.
- [x] Stale live-session detection and reconciliation.
- [x] Safe in-memory submitted user-input queuing.
- [x] Versioned process readiness adapters.
- Diagnostics and exportable logs.
- Automated integration tests around lifecycle events.

The first Phase 4 slice adds schema version 4 application-run facts. An unfinished immediately preceding run records only that Halfway did not shut down cleanly. Processes are never reattached or automatically restarted; restored active metadata still becomes Disconnected. Crash detection creates no lifecycle event, alert delivery, notification, unread marker, or terminal message. Transcripts, prompts, partial input, submitted input, environment variables, and secrets remain unpersisted.

The second Phase 4 slice reconciles each exact coordinator-owned terminal from its exit event and completion task. Zero, nonzero, cancelled, explicit-stop, and missing-exit outcomes retain deterministic Completed, Failed, or Disconnected mapping. Ownership release is idempotent, stale writes and resizes fail, and duplicate callbacks cannot create duplicate lifecycle events or completion alerts. No process scan, PID adoption, reattachment, automatic restart, or reliability-generated terminal message is used.

The third Phase 4 slice adds a per-live-session FIFO queue for fully submitted user input only. Capacity is eight entries including the in-flight write; overflow rejects the newest entry with a visible local error. Queue generations are bound to exact terminal ownership and close on cancellation, stop, or exit, so input never crosses to a sibling or replacement. Partial and submitted input remain unpersisted. Automatic alerts stay separate and retain readiness, partial-input blocking, and durable delivery semantics.

The fourth Phase 4 slice gives readiness adapters stable `shell` v1 and `codex` v1 identities, exact catalog selection, and safe rejection of unsupported versions. Runtime launch-profile selection owns adapter construction rather than `MainWindow`. Codex v1 remains conservative while supporting split and ANSI-decorated chunks. Readiness output is bounded in memory and is not persisted.

## Possible Later Features

Only where they preserve scope:

- Worktree awareness.
- Branch indicators.
- Session timeline.
- Cost/token display if reliably exposed.
- Multiple workspaces.
- Detached terminal windows.

## Permanently Out of Scope

- Code editor.
- File explorer.
- Git GUI.
- Debugger.
- General-purpose tmux compatibility.
- Universal provider support.
- Model-driven summaries generated by Halfway.
- Arbitrary pane trees.
- Cross-platform UI compromise.

## Future Linux Product

A Linux client may be designed separately using native Linux technologies.

It may share stable data and event formats with Halfway, but it is not a porting constraint on the Windows application.
