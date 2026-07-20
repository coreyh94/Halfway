# Halfway

Halfway is a polished, Windows-native desktop client for Codex CLI.

> A focused, tmux-inspired workspace designed specifically for managing Codex agents and sub-agents on Windows.

Halfway does not replace Codex and does not replace the user's editor. It houses real Codex terminals, makes agent state visible, and sends lightweight completion alerts to parent agents.

## Core Layout

- Top information bar
- Left navigation sidebar
- Main agent terminal workspace
- Tabbed sub-agent terminal workspace

## Core Feature

When a tracked sub-agent completes, Halfway sends a short status update to its parent:

```text
[Halfway Alert!] Runtime completed. Continue orchestration.
```

## Documentation

- [PRODUCT_SPEC.md](PRODUCT_SPEC.md)
- [ARCHITECTURE.md](ARCHITECTURE.md)
- [UI_SPEC.md](UI_SPEC.md)
- [ENGINEERING_PRINCIPLES.md](ENGINEERING_PRINCIPLES.md)
- [ROADMAP.md](ROADMAP.md)

## Phase 1 workspace shell

Halfway now opens a persisted workspace for its working directory. On first run it creates stable metadata for a PowerShell **Planner** primary session and a **Runtime** sub-agent. Runtime uses PowerShell unless `HALFWAY_RUNTIME_LAUNCH=codex` is set. Both selected sessions start as fresh, independent ConPTY processes.

On later launches, Halfway restores the most recently opened workspace when the current directory is not already a known workspace. A valid `HALFWAY_WORKING_DIRECTORY` override takes priority, followed by a known current directory, the most recent workspace whose directory still exists, and finally the current directory. Opening a workspace marks it as most recent. Restore reuses metadata and selections but always starts newly verified processes; it never reattaches processes or restores terminal content.

The information-bar workspace selector lists already-known workspaces in deterministic recency order. Missing directories remain visible but unavailable, Windows path casing variants collapse to one identity, and duplicate names include path context. Selecting the active workspace is a complete no-op. Switching away requires confirmation whenever the coordinator owns a terminal generation or any view contains partial input. Cancellation changes nothing; confirmation stops exact old ownership, flushes final persistence, replaces the active presentation, and starts fresh processes only for the target's selected Planner and selected sub-agent. Panel widths and the system theme survive the rebuild, while terminal output, partial input, search, unread, and focus state do not.

Use **Add sub-agent** to create a named PowerShell or Codex sub-agent. Names must be non-empty and unique within the sub-agent group. Sidebar and tab selection stay synchronized and the selected sessions, launch profiles, parent relationship, display order, and last known statuses are restored on later starts.

Keyboard navigation keeps the sidebar, selected sub-agent tab, and terminal input focus synchronized: **Ctrl+1** focuses the selected Planner, **Ctrl+2** focuses the selected sub-agent, **Ctrl+Tab** and **Ctrl+Shift+Tab** cycle sub-agents, **Alt+Up** and **Alt+Down** move through primary sessions followed by sub-agents in display order, and **Ctrl+Shift+N** opens Add sub-agent. Navigation wraps, persists selection exactly like mouse selection, and works for inactive sessions without starting or restarting them. Partially typed input remains in its terminal and is never submitted by navigation.

Use **Ctrl+F** to search the focused terminal's current in-memory output. **Enter** or **F3** moves to the next match, **Shift+F3** moves to the previous match, and results wrap in both directions. **Escape** closes search and restores terminal input focus. Search is case-insensitive, updates as output arrives, and never searches or changes partially typed input. Queries and results are not persisted.

When terminal output arrives for a session other than the focused terminal, a `•` unread marker appears beside that session in the sidebar and, for sub-agents, its tab. Focusing the terminal clears its marker in both places. Unread state is derived only from live output, remains in memory, and is not restored or written to SQLite.

Drag the divider beside the sidebar or sub-agent panel to resize the fixed three-region workspace. Widths are constrained so the navigation and terminal panels remain usable, and terminal views report their resulting dimensions through the existing ConPTY resize path. Double-click a divider to restore its default width. Panel widths are presentation-only and are not persisted.

Halfway follows the Windows light or dark application theme automatically. Both palettes use the same semantic surface, text, status, selection, error, splitter, and search-highlight roles, so changing the system theme updates the existing workspace without altering session state. There is no separate persisted theme preference in this slice.

When a real session transition reaches **Failed**, Halfway sends one concise Windows app notification if the app is inactive or another terminal is focused. A failure already visible in the focused terminal does not produce a redundant notification, and selecting a notification brings Halfway forward. Notification registration and delivery are best-effort presentation features: if Windows notifications are unavailable, lifecycle tracking continues unchanged. Restore, repeated state callbacks, and non-failure transitions do not create notifications.

Metadata is stored in `%LOCALAPPDATA%\Halfway\halfway.db`. Schema version 4 retains all schema version 3 workspace, session, relationship, lifecycle-event, and alert-delivery data and transactionally adds application-run records. Initial schema creation and its version marker commit atomically. Windows workspace paths use one case-insensitive directory identity; legacy case-duplicate records are merged transactionally without dropping their sessions or relationships. Each run has a stable ID, start time, optional clean-shutdown time, and application version. At startup, only an unfinished immediately preceding run is reported as evidence that Halfway itself did not shut down cleanly; a later clean run prevents older unfinished history from being repeatedly reported. The current run is marked clean only after orderly session shutdown and final status and lifecycle persistence complete.

Crash detection never claims that an old process is alive, reattaches or restarts a process, creates a lifecycle event, sends a notification, changes an alert delivery, marks a terminal unread, or manufactures a terminal message. Workspace metadata restoration continues to normalize previously active states to Disconnected. Existing stale `Reserved` recovery is unchanged, pending alerts remain eligible, and delivered alerts are never resent. Eligible completions and their deterministic messages survive restart, and completions arriving for the same parent within a 250 ms window remain coalesced into one deterministic alert.

Halfway never treats restored metadata as proof that a process is alive. Previously active states restore as Disconnected; only the selected Planner and selected sub-agent start fresh automatically. Other restored sub-agents remain visible and can be explicitly started. Processes are never reattached. Terminal transcripts, prompts, partial input, submitted user input, process handles, process IDs, environment variables, API keys, tokens, and secrets are not persisted. Only deterministic eligible alert messages are stored with delivery facts; reliability features add no automatic terminal messages.

Live sessions transition from Running to Waiting when their isolated PowerShell or Codex readiness adapter observes a safe prompt. Successfully submitted user or Halfway input returns Waiting to Running until readiness is observed again. Failed writes leave the session Waiting. Completion, failure, disconnection, and explicit-stop mapping remain process-driven.

`SessionCoordinator` also observes the completion task of each exact terminal instance it owns. If ownership ends without an exit callback, a normal or cancelled completion reconciles once to Disconnected and a faulted completion reconciles once to Failed. Exit callbacks remain authoritative when present: zero exits become Completed, nonzero exits become Failed, and explicit stops or cancelled exits become Disconnected. The first ownership-ending fact atomically releases that exact terminal, so repeated callbacks, stop/exit races, writes, and resizes cannot keep or revive stale ownership. Reconciliation never scans processes, uses persisted status as liveness, reattaches, restarts, or generates a terminal message or duplicate completion alert.

Pressing Enter submits the current terminal input to a dedicated in-memory queue for that exact live session. Each queue holds at most eight accepted submissions, including an in-flight write, and preserves FIFO order. If full, the newest submission is rejected with a local input error; older accepted input is not dropped. Completed, Failed, Disconnected, and non-owned sessions reject submissions, while stop, exit, cancellation, or ownership replacement resolves queued work without replay to another terminal. Waiting changes to Running only after its submitted write succeeds; failed writes leave Waiting unchanged. Partial text is never queued, and queued or submitted user input is never persisted or restored. Automatic Halfway alerts remain a separate channel governed by readiness, partial-input blocking, and durable reserve/commit/release rules.

Process readiness adapters expose stable identities: PowerShell uses `shell` v1 and Codex uses `codex` v1. Launch-profile-to-adapter selection is isolated in the runtime layer, and unknown identifiers or versions fail without silently falling back. Codex v1 remains conservative: it requires observed Codex identity plus a safe prompt, supports split and ANSI-decorated output chunks, and resets readiness after successful input. Failed writes do not reset readiness or clear Waiting. Output sampled for readiness remains bounded in memory and is never persisted.

Use **Export diagnostics** in the information bar to write `%LOCALAPPDATA%\Halfway\diagnostics\halfway-diagnostics.json`. Export is explicit and local; Halfway never uploads diagnostics. The versioned JSON contains at most the newest 256 structured application facts in sequence order, including startup/shutdown outcomes, counts, lifecycle states, readiness adapter identity, notification availability, and alert-delivery outcomes. A mandatory export-time redaction pass removes common bearer tokens, keys, passwords, and secrets even though producers are restricted to safe fields. Export failures are shown locally and do not affect live sessions.

Diagnostics are memory-only until the user exports them and are cleared on restart. They never contain terminal output or transcripts, prompts, partial input, submitted user input, alert terminal payloads, environment variables, command lines, file contents, stack dumps, API keys, tokens, passwords, or secrets. They do not create lifecycle events, alerts, notifications, unread state, or terminal messages. SQLite remains at schema version 4 and stores no diagnostic records.

Phase 4 reliability sequences are covered by a dedicated integration test project using deterministic terminal fakes, readiness signals, and temporary real SQLite databases. The tests span start/readiness/input, completion batching, alert reserve-release-retry-commit, restart recovery, delivered-alert deduplication, stop and exit mapping, restore isolation, crash markers, stale ownership, session-bound queued input, notification facts, and diagnostics privacy. They require neither Codex CLI nor arbitrary timing sleeps, while the existing real ConPTY round trips remain part of the full solution suite.

## Build and run Phase 1

Prerequisites:

- Windows 10 version 1809 or newer (ConPTY).
- .NET 8 SDK and the Windows App SDK workload/dependencies required by WinUI 3.
- x64 Windows.
- Codex CLI on `PATH` to use the Codex launch button.

From the repository root:

```powershell
dotnet test Halfway.sln --configuration Debug
dotnet build Halfway.App\Halfway.App.csproj --configuration Debug -p:Platform=x64
dotnet run --project Halfway.App\Halfway.App.csproj --configuration Debug -p:Platform=x64
```

Halfway starts the selected Planner and sub-agent through independent ConPTY sessions in the workspace working directory. Stopping one leaves its siblings running. Use the Planner **Codex** button to replace its PowerShell process with the installed Codex CLI. Set `HALFWAY_WORKING_DIRECTORY` to an existing directory to explicitly choose a workspace instead of applying normal restore resolution.

The **Inject demo alert** button submits exactly one deterministic alert. If user input is partially typed, the alert remains queued until that input is submitted.

Runtime launches PowerShell by default. Set `HALFWAY_RUNTIME_LAUNCH=codex` to launch the installed Codex CLI instead; `powershell` explicitly selects the default.

## Current limitations

- Terminal output is decoded as a stateful UTF-8 stream and shown in a bounded 64 KiB raw-text view with common ANSI control sequences removed; it is not a full terminal emulator.
- Input is line-oriented. Advanced terminal keys, mouse input, complete scrollback, copy/paste semantics, and accurate screen-buffer rendering require an established terminal control in a later slice. Search is limited to the bounded in-memory raw-text view.
- Codex readiness uses the isolated, deliberately conservative `codex` v1 adapter and may require a new explicit adapter version as Codex output changes.
- Sessions are metadata-persisted but processes are never reattached and terminal content is never restored.
- Exactly one known workspace is presented and owns live processes at a time; no creation/import, delete/archive, background workspace, reattachment, or automatic restart workflow is included.
- Lifecycle state remains process-based; the first Phase 2 slice now durably records lifecycle events and restart-safe alert delivery.
- Completion batching uses a fixed 250 ms event-driven window; it is not configurable and does not generate summaries.
