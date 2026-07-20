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

Use **Add sub-agent** to create a named PowerShell or Codex sub-agent. Names must be non-empty and unique within the sub-agent group. Sidebar and tab selection stay synchronized and the selected sessions, launch profiles, parent relationship, display order, and last known statuses are restored on later starts.

Metadata is stored in `%LOCALAPPDATA%\Halfway\halfway.db`. Schema version 2 adds a durable lifecycle-event ledger and explicit `Pending`, `Reserved`, and `Delivered` alert-delivery states. Eligible completion events and their deterministic messages survive restart. Stale reservations recover to pending on startup, pending alerts remain eligible, and delivered alerts are never resent. Completions arriving for the same parent within a 250 ms window are coalesced into one deterministic alert.

Halfway never treats restored metadata as proof that a process is alive. Previously active states restore as Disconnected; only the selected Planner and selected sub-agent start fresh automatically. Other restored sub-agents remain visible and can be explicitly started. Processes are not reattached, and terminal output, partial prompts, process handles, environment variables, API keys, and secrets are not persisted. Only deterministic eligible alert messages are stored with delivery facts; terminal transcripts and prompts remain unpersisted.

Live sessions transition from Running to Waiting when their isolated PowerShell or Codex readiness adapter observes a safe prompt. Successfully submitted user or Halfway input returns Waiting to Running until readiness is observed again. Failed writes leave the session Waiting. Completion, failure, disconnection, and explicit-stop mapping remain process-driven.

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

Halfway starts the selected Planner and sub-agent through independent ConPTY sessions in the workspace working directory. Stopping one leaves its siblings running. Use the Planner **Codex** button to replace its PowerShell process with the installed Codex CLI. The working directory defaults to the directory from which Halfway is launched; set `HALFWAY_WORKING_DIRECTORY` to an existing directory to override it.

The **Inject demo alert** button submits exactly one deterministic alert. If user input is partially typed, the alert remains queued until that input is submitted.

Runtime launches PowerShell by default. Set `HALFWAY_RUNTIME_LAUNCH=codex` to launch the installed Codex CLI instead; `powershell` explicitly selects the default.

## Current limitations

- Terminal output is a bounded 64 KiB raw-text view with common ANSI control sequences removed; it is not a full terminal emulator.
- Input is line-oriented. Advanced terminal keys, mouse input, search, complete scrollback, copy/paste semantics, and accurate screen-buffer rendering require an established terminal control in a later slice.
- Codex readiness uses an isolated, deliberately conservative output heuristic and may need adapter updates as Codex output changes.
- Sessions are metadata-persisted but processes are never reattached and terminal content is never restored.
- Only one workspace (selected by working directory) is presented at a time; no delete/archive workflow is included.
- Lifecycle state remains process-based; the first Phase 2 slice now durably records lifecycle events and restart-safe alert delivery.
- Completion batching uses a fixed 250 ms event-driven window; it is not configurable and does not generate summaries.
