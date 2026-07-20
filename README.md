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

## Build and run the completed Phase 0 spike

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

Halfway starts a Planner PowerShell session and an independent Runtime sub-agent session through ConPTY. Runtime uses PowerShell by default, or Codex when `HALFWAY_RUNTIME_LAUNCH=codex` is set. Runtime starts in the configured working directory, streams into the Runtime tab, accepts line input, and resizes with its panel. Stopping either session leaves the other session running. Use the **Codex** button to replace Planner with the installed Codex CLI. The working directory defaults to the directory from which Halfway is launched; set `HALFWAY_WORKING_DIRECTORY` to an existing directory to override it.

The **Inject demo alert** button submits exactly one deterministic alert. If user input is partially typed, the alert remains queued until that input is submitted.

Runtime launches PowerShell by default. Set `HALFWAY_RUNTIME_LAUNCH=codex` to launch the installed Codex CLI instead; `powershell` explicitly selects the default.

## Phase 0 limitations

- Terminal output is a bounded 64 KiB raw-text view with common ANSI control sequences removed; it is not a full terminal emulator.
- Input is line-oriented. Advanced terminal keys, mouse input, search, complete scrollback, copy/paste semantics, and accurate screen-buffer rendering require an established terminal control in a later slice.
- Codex readiness uses an isolated, deliberately conservative output heuristic and may need adapter updates as Codex output changes.
- The Runtime tab is the first functional sub-agent tab; UI and Tests tabs remain placeholders.
- Runtime lifecycle is tracked as queued, running, completed, failed, or disconnected. A successful Runtime exit sends exactly one queued-safe completion alert to Planner.
- Sessions are not persisted or reattached after Halfway exits.
