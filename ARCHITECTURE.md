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
SqliteWorkspaceStore -> durable workspace/session metadata, lifecycle events, alert deliveries and app-run facts
WorkspaceCatalog     -> loaded metadata, creation, selection and persisted status
DurableAlertLedger   -> focused event/delivery operations and restart recovery
SessionCoordinator   -> live ConPTY ownership, output, input, resize and exit
MainWindow            -> fixed-layout presentation and user interaction
```

`MainWindow` handles the fixed set of window-level navigation shortcuts through shared primary/sub-agent selection methods. A pure `WorkspaceNavigation` helper owns display ordering, target choice, and wrapping. Every mouse, tab, and keyboard selection persists through `WorkspaceCatalog`, synchronizes sidebar and tab presentation, then restores terminal input focus. Selection never calls `SessionCoordinator`, creates a lifecycle transition, or queues terminal input. `TerminalSessionView` retains each view's partial input while selection changes; inactive terminal inputs are focusable but read-only.

Terminal search remains a presentation-only feature. A pure `TerminalSearch` helper finds case-insensitive matches and wraps result navigation; `TerminalSessionView` owns the per-view query, highlighting, and scroll position within its bounded in-memory output. Search never reads the input control, writes to ConPTY, changes readiness, creates lifecycle events, queues alerts, or persists queries, matches, or output.

`SessionAttentionTracker` owns transient unread state. A real `SessionCoordinator.OutputReceived` callback marks a session unread only when another terminal is focused, and focusing that terminal clears its marker in both sidebar and tab presentation. Repeated output is idempotent, focused output stays read, and restore creates no unread state. Attention state never enters SQLite and does not participate in lifecycle detection or alert delivery.

The fixed workspace grid exposes two native drag boundaries for sidebar/main and main/sub-agent sizing. Pure `PanelSizing` calculations clamp each pair to usable minimums while preserving available width; the main panel remains responsive as the window changes size. `TerminalSessionView.SizeChanged` continues through `SessionCoordinator.Resize` to the owned ConPTY session. Divider movement does not select sessions, submit input, change lifecycle state, or persist layout dimensions.

Application-level light and dark theme dictionaries define semantic colors for workspace surfaces, text hierarchy, status, selection, errors, splitters, and search highlights. XAML and dynamically created presentation elements consume shared theme brushes, allowing WinUI to follow the Windows application theme at runtime. Theme choice is presentation-only: it is not stored in SQLite and does not affect terminal ownership, input readiness, lifecycle events, or alert delivery.

`FailureNotificationPolicy` converts each real `Failed` lifecycle event into at most one deterministic presentation fact when the app is inactive or a different terminal is focused. `WindowsFailureNotifier` registers the unpackaged app with the Windows App SDK notification manager, displays the short fact, and activates the window when invoked. Registration and delivery failures are contained inside the adapter. Notifications are not terminal messages, are not persisted, and do not participate in lifecycle-event or alert-delivery state.

`DiagnosticBuffer` retains at most 256 structured application facts in memory under stable category and event names. Its lock establishes unique sequence numbers for deterministic snapshots during concurrent writes. Producers supply only reviewed metadata such as schema version, counts, session correlation IDs, lifecycle state names, adapter identity/version, outcomes, sanitized exception type/message, notification availability, and alert-delivery outcomes. Sensitive field categories are rejected at collection time, common credential patterns are redacted, and no stack dump is captured.

`DiagnosticExporter` writes schema-version-1 JSON in sequence and fact-name order only when the user selects **Export diagnostics**. The explicit local target is `%LOCALAPPDATA%\Halfway\diagnostics\halfway-diagnostics.json`; there is no telemetry, networking, silent upload, or SQLite diagnostics table. Export applies a second mandatory redaction and sensitive-field filtering pass. Failure is presentation-only and cannot change live ownership, lifecycle events, alert deliveries, notifications, unread state, or terminal input. Terminal output and transcripts, prompts, partial input, submitted input, alert payloads, environment variables, command lines, file contents, API keys, tokens, passwords, and secrets are neither retained nor exported.

`Halfway.Reliability.Tests` exercises complete cross-component event sequences with deterministic `ITerminalSession` fakes, readiness output signals, controllable write gates, and temporary real SQLite stores. Tests await lifecycle signals rather than sleeping and do not require Codex CLI. They cover registry/coordinator transitions, input identity, durable alert state, restart and app-run recovery, notification policy, transient attention, and diagnostic privacy together. The platform-specific suite continues to retain real ConPTY round trips as a separate boundary check.

`Halfway.Persistence` contains the SQLite store and catalog. `Halfway.Core` contains shared workspace/session metadata, launch-profile values, agent kinds, lifecycle states, and status presentation. Persisted models never own or expose `ITerminalSession`; live terminal ownership remains exclusively in `Halfway.Runtime`.

`SessionCoordinator` reconciles live ownership from the exact owned `ITerminalSession` exit event and completion task; it never scans arbitrary operating-system processes or adopts a PID. An atomic terminal-instance exchange is the idempotence boundary for exit callbacks, completion fallback, explicit stop, write, and resize. A zero exit remains Completed, a nonzero exit remains Failed, and an explicit stop or cancelled exit remains Disconnected. If completion ends without an exit fact, normal or cancelled completion conservatively becomes Disconnected while a faulted completion becomes Failed. Once ownership is released, writes and resizes fail predictably, repeated callbacks create no duplicate lifecycle event or eligible alert, and the same metadata may later start only through an explicit existing start action. Known closed-handle and closed-pipe cleanup races are contained only after the owned terminal is proven complete; other teardown failures are not blanket-suppressed. Reconciliation does not persist process identity, reattach, automatically restart, or manufacture terminal input.

Each live `ManagedSessionState` owns one focused `SubmittedInputQueue` for user input only. Enter in `TerminalSessionView` remains the explicit submission boundary; partial TextBox content never enters the queue. The queue has a fixed capacity of eight entries including any in-flight write, preserves FIFO order, and rejects the newest overflow with a local error. Its delivery delegate captures the exact coordinator state and owned terminal instance, so entries cannot cross session identity or replacement ownership. Only Running or Waiting sessions accept entries. Stop, exit, cancellation, or reconciliation closes that generation's queue and deterministically resolves queued tasks without replay or process start. Readiness resets and Waiting transitions to Running only after a successful exact-terminal write; failures leave Waiting unchanged.

User input queuing and automatic alert input are deliberately separate channels. Alerts continue through `AlertInputCoordinator` and the durable `Pending`/`Reserved`/`Delivered` ledger, remain blocked by Planner partial input and readiness, and are not placed in `SubmittedInputQueue`. The user queue is bounded process memory only: prompts, partial input, submitted input, and queue entries never enter SQLite, diagnostics, restore, or a replacement process.

### Persistence and restore

Schema version 4 preserves the version 1 `Workspaces` and `Sessions` data, version 2 `LifecycleEvents` and `AlertDeliveries`, and version 3 `AgentRelationships`. The version 3 migration transactionally backfills existing non-null parent links and enforces same-workspace relationships. The version 4 migration transactionally adds `ApplicationRuns`, containing only a stable run ID, start time, optional clean-shutdown time, and application version. Initial schema objects and the version-1 marker share one transaction; foreign keys are enabled, and `PRAGMA user_version` advances only after each migration succeeds. Windows workspace lookup and insertion use a canonical case-insensitive path identity, and existing case-duplicate workspaces are merged deterministically in a transaction while retaining sessions and parent-child relationships. Lifecycle events contain stable IDs, session/parent identity, the status transition, occurrence time, and eligibility. Delivery records contain only the deterministic message and `Pending`, `Reserved`, or `Delivered` state with reservation, delivery, and update timestamps.

After SQLite initialization succeeds, startup atomically reads the immediately preceding application run and inserts the current run. An absent clean-shutdown timestamp means only that Halfway itself did not finish an orderly shutdown. It is not evidence that a terminal process survived. A run is marked clean after owned sessions stop and queued final status and lifecycle persistence completes during normal window close. The store is disposed only after that barrier settles; a failed final write leaves the run unclean. Crash detection does not change workspace/session metadata, create lifecycle or delivery facts, send notifications, create unread state, reattach or restart processes, or generate terminal input. Because only the immediately preceding run is evaluated, an older unfinished run is not repeatedly reported after a later clean run.

New sub-agent creation inserts session metadata and its relationship in one transaction. `WorkspaceCatalog` loads and validates exactly one registered primary parent for every sub-agent, and that explicit relationship supplies live registry, coordinator, lifecycle-event, and alert-routing identity. The legacy `Sessions.ParentSessionId` value remains as compatible metadata and must agree with the registered relationship.

The production database is `%LOCALAPPDATA%\Halfway\halfway.db`. It stores deterministic eligible alert messages but no terminal transcripts or output, prompts, partial input, submitted user input, environment variables, API keys, tokens, secrets, process handles, or process IDs.

On first run, the catalog creates Planner and Runtime metadata and selects both. On restore, stable IDs and selection are reused. Queued, Running, and Waiting metadata is normalized to Disconnected because no previous process is considered alive. Completed, Failed, and Disconnected remain visible. The selected Planner and sub-agent start as fresh processes; other restored sessions require an explicit Start/Restart. Halfway never reattaches to an old process, automatically restarts a session because a crash was detected, or creates an event solely because metadata was restored. Reliability facts never manufacture terminal messages.

At startup, `WorkspaceRestoreResolver` chooses a working directory in deterministic priority order: a valid explicit environment override, an already-known current directory, the most recently opened workspace when its directory still exists, then the current directory. Catalog initialization updates the workspace activity timestamp without changing session status or creating lifecycle or delivery facts. This uses the unchanged workspace metadata and does not persist layout content, terminal output, prompts, partial input, process identity, or secrets.

At startup, stale `Reserved` deliveries return to `Pending`. Pending alerts remain eligible but are not sent until the matching parent Planner is newly running, its readiness adapter reports safe input, and no partial input exists. Reservation is an atomic compare-and-update; a successful terminal write is followed by a `Delivered` commit, while failure or cancellation releases the record to `Pending`. Delivered records never become eligible again.

Live completions for the same parent use a fixed 250 ms event-driven coalescing window. All pending members of a batch are reserved, committed, or released together in one SQLite transaction, while remaining independent lifecycle and delivery records. The message is deterministic and contains only persisted sub-agent display names. No polling, terminal content, or model-generated summary participates in batching. Restored pending events may also form one batch once their newly started parent becomes safe for input.

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

The runtime owns one `IProcessReadinessAdapter` per live session. Process start establishes `Running`; observing a safe prompt transitions it to `Waiting`; a successful terminal write resets readiness and transitions it back to `Running`. Failed writes do not falsely clear `Waiting`. The same Planner adapter supplies the alert-safety boundary, so lifecycle presentation and automatic input use one readiness fact without weakening the adapter abstraction.

Every readiness adapter exposes an immutable identifier and positive version through `IProcessReadinessAdapter`. The supported identities are `shell` v1 and `codex` v1. `ProcessReadinessAdapterCatalog` constructs only exact supported identities and rejects unknown identifiers or versions without fallback. `RuntimeReadinessAdapterSelection` owns the existing launch-profile mapping, keeping concrete adapter selection out of `MainWindow`. Codex v1 retains the conservative identity-plus-safe-prompt rule, accumulates only a bounded in-memory tail so identity and ANSI-decorated prompts may span chunks, and clears that tail after successful input. Neither sampled output nor readiness identity is currently persisted; if identity metadata is added later, only identifier and version may be stored.

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
- ApplicationRuns.
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
