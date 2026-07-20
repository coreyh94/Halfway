# Halfway Architecture

## 1. Platform and Stack

- Platform: Windows only.
- Language: C#.
- Runtime: Modern .NET.
- UI: WinUI 3 with Windows App SDK.
- Terminal backend: Windows ConPTY.
- Persistence: SQLite.
- Managed tool: Codex CLI.
- Packaging: MSIX or self-contained Windows installer.

## 2. Architectural Direction

Halfway is a native Windows process and terminal-management application.

It should not use Tauri, Electron or a webview-based frontend for the primary implementation.

The architecture should favour:

- Direct Windows API access.
- Clear process ownership.
- Reliable terminal input and output.
- Recoverable local state.
- Deterministic lifecycle events.
- Minimal coupling to Codex output formatting.

## 3. Major Components

```text
Halfway.exe
│
├── WinUI 3 Application Shell
│   ├── Information Bar
│   ├── Agent Sidebar
│   ├── Main Terminal Workspace
│   └── Sub-Agent Tab Workspace
│
├── WorkspaceManager
├── SessionManager
├── AgentRegistry
├── ParentChildTracker
├── CodexProcessManager
├── ConPtyTerminalHost
├── LifecycleDetector
├── AlertDispatcher
├── EventLedger
└── SQLiteStore
```

## 4. Component Responsibilities

The implementation separates five concrete ownership layers:

```text
SqliteWorkspaceStore -> durable workspace/session metadata, lifecycle events and alert-delivery facts
WorkspaceCatalog     -> loaded metadata, creation, selection and persisted status
DurableAlertLedger   -> focused event/delivery operations and restart recovery
SessionCoordinator   -> live ConPTY ownership, output, input, resize and exit
MainWindow            -> fixed-layout presentation and user interaction
```

`Halfway.Persistence` contains the SQLite store and catalog. `Halfway.Core` contains shared workspace/session metadata, launch-profile values, agent kinds, lifecycle states, and status presentation. Persisted models never own or expose `ITerminalSession`; live terminal ownership remains exclusively in `Halfway.Runtime`.

### Persistence and restore

Schema version 2 preserves the version 1 `Workspaces` and `Sessions` data and adds `LifecycleEvents` and `AlertDeliveries` in an idempotent transaction. Foreign keys are enabled, and `PRAGMA user_version` advances only after migration succeeds. Lifecycle events contain stable IDs, session/parent identity, the status transition, occurrence time, and eligibility. Delivery records contain only the deterministic message and `Pending`, `Reserved`, or `Delivered` state with reservation, delivery, and update timestamps.

The production database is `%LOCALAPPDATA%\Halfway\halfway.db`. It stores deterministic eligible alert messages but no terminal output, transcripts, prompts, secrets, process handles, or process IDs.

On first run, the catalog creates Planner and Runtime metadata and selects both. On restore, stable IDs and selection are reused. Queued, Running, and Waiting metadata is normalized to Disconnected because no previous process is considered alive. Completed, Failed, and Disconnected remain visible. The selected Planner and sub-agent start as fresh processes; other restored sessions require an explicit Start/Restart. Halfway never reattaches to an old process or creates an event solely because metadata was restored.

At startup, stale `Reserved` deliveries return to `Pending`. Pending alerts remain eligible but are not sent until the matching parent Planner is newly running, its readiness adapter reports safe input, and no partial input exists. Reservation is an atomic compare-and-update; a successful terminal write is followed by a `Delivered` commit, while failure or cancellation releases the record to `Pending`. Delivered records never become eligible again. This slice delivers alerts individually; batching remains future work.

### WorkspaceManager

- Opens and closes workspaces.
- Tracks repository path and workspace settings.
- Restores the last active layout.
- Persists selected agent and sub-agent tab.

### SessionManager

- Creates, resumes and terminates managed sessions.
- Associates each terminal with a stable Halfway session ID.
- Tracks terminal ownership and current state.
- Restores recoverable session metadata after restart.

### AgentRegistry

Stores:

- Agent ID.
- Display name.
- Agent type.
- Parent ID.
- Codex session identity where available.
- Process identity.
- Current status.
- Selected terminal.
- Notification state.

### ParentChildTracker

- Maintains explicit parent-child relationships.
- Does not rely solely on the parent model remembering children.
- Detects when all tracked children reach terminal states.
- Provides data for sidebar grouping and alert routing.

### CodexProcessManager

- Launches Codex CLI.
- Supplies working directory and environment.
- Tracks process start, exit and failure.
- Supports controlled input injection.
- Avoids rewriting Codex prompts.

### ConPtyTerminalHost

- Creates and owns ConPTY sessions.
- Streams output to the terminal control.
- Sends user and Halfway-generated input.
- Handles resize events.
- Preserves terminal semantics.

Terminal emulation should use an established control or library where practical. Halfway should not implement a full terminal emulator from scratch unless unavoidable.

### LifecycleDetector

Determines states such as:

- Queued.
- Running.
- Waiting.
- Completed.
- Failed.
- Disconnected.

Detection should prefer structured Codex events if exposed.

Fallback detection may use:

- Process state.
- Terminal activity.
- Exit codes.
- Stable output markers.
- User-configured integration hooks.

Output parsing must be isolated behind an adapter because Codex output formats may change.

### AlertDispatcher

- Sends short messages to parent sessions.
- Enforces the reserved prefix.
- Deduplicates alerts.
- Batches rapid completions where appropriate.
- Never includes verbose payloads by default.

### EventLedger

Each event stores:

- Event ID.
- Session ID.
- Parent session ID.
- Previous state.
- New state.
- Event timestamp.
- Alert eligibility.
- Alert delivered timestamp.
- Batch ID where applicable.

A completion alert is eligible only when:

```text
previous_state != completed
new_state == completed
alert_delivered == false
```

### SQLiteStore

Stores local application metadata only.

Suggested tables:

- Workspaces.
- Sessions.
- AgentRelationships.
- LifecycleEvents.
- AlertDeliveries.
- UserSettings.

Codex conversation content should not be copied into SQLite by default.

## 5. Input Injection

Halfway alerts are injected as terminal input into the correct parent Codex session.

Example:

```text
[Halfway Alert!] Runtime completed. Continue orchestration.
```

Injection must:

- Target exactly one parent.
- Occur at most once per eligible event.
- Avoid interrupting partially typed user input.
- Avoid sending while the terminal is in an unsafe input mode.
- Queue until input can be submitted safely.

The implementation must distinguish:

- Terminal output completion.
- Prompt readiness.
- Safe message submission.

## 6. Failure Handling

Halfway should handle:

- Codex process crash.
- Halfway application crash.
- Windows restart.
- Lost terminal connection.
- Invalid parent relationship.
- Duplicate completion detection.
- Alert delivery failure.
- Stale sessions.

On restart, Halfway may restore metadata and reconnect where possible, but must never claim a process is active without verification.

## 7. Security Boundary

Halfway should:

- Run Codex under the user's normal Windows account.
- Avoid storing API keys itself unless Codex requires an explicit integration.
- Avoid logging secrets from terminal output.
- Avoid hidden prompt manipulation.
- Clearly mark Halfway-generated input.
- Keep all automatic messaging deterministic.

## 8. Linux Boundary

The Windows implementation is not required to share its UI stack with Linux.

A future Linux-native client may share:

- Workspace file format.
- Session schema.
- Agent lifecycle enum.
- Alert message conventions.
- Event ledger semantics.

It does not need to share:

- UI code.
- Terminal backend.
- Windowing code.
- Packaging.
