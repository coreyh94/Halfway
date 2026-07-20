# Halfway Product Specification

## 1. Product Definition

Halfway is a polished, Windows-native desktop interface for Codex CLI.

The simplest description is:

> A less adaptable, more polished version of tmux, built specifically for Codex agents on Windows.

Halfway intentionally trades general-purpose terminal flexibility for a fixed, clear and reliable Codex workflow.

## 2. Product Goals

Halfway should:

- House real Codex CLI sessions.
- Separate primary agents from sub-agents.
- Make every tracked session easy to find.
- Display agent status beside its sidebar label.
- Provide a dedicated main-agent terminal.
- Provide tabbed sub-agent terminals.
- Detect sub-agent terminal states.
- Alert parent agents when children finish.
- Persist workspaces and session metadata.
- Avoid unnecessary Codex context growth.

## 3. What Halfway Is

Halfway is:

- A native Windows desktop application.
- A Codex CLI session host.
- A focused terminal workspace.
- An agent and sub-agent navigator.
- A session persistence layer.
- A local lifecycle and status tracker.
- A lightweight completion-signalling layer.

## 4. What Halfway Is Not

Halfway is not:

- An IDE.
- A code editor.
- A file explorer.
- A Git client.
- A debugger.
- A build system.
- A project-management system.
- An AI model.
- An AI coding assistant.
- A replacement for Codex.
- A replacement for VS Code or another editor.
- A general-purpose tmux replacement.
- A universal terminal multiplexer.
- A cross-platform UI framework.
- A universal multi-provider agent platform.

These boundaries are product requirements, not temporary MVP omissions.

## 5. Responsibility Boundary

### Codex owns

- Reasoning.
- Planning.
- Delegation.
- Code generation.
- File editing.
- Tool execution.
- Testing.
- Git operations.
- Code review.
- AI decisions.

### Halfway owns

- Windows desktop presentation.
- Terminal hosting.
- Codex process management.
- Session navigation.
- Workspace layout.
- Agent and sub-agent grouping.
- Parent-child tracking.
- Lifecycle detection.
- Status indicators.
- Completion signalling.
- Local metadata persistence.

Halfway must not attempt to duplicate Codex intelligence.

## 6. Primary Interface

```text
┌────────────────────────────────────────────────────────────────────────────┐
│ Workspace | Repo | Branch | Model | Running | Waiting | Complete | Status │
├────────────────┬─────────────────────────────┬──────────────────────────────┤
│ Sidebar        │ Main Agent Workspace        │ Sub-Agent Workspace          │
│                │                             │                              │
│ AGENT          │ Selected main agent         │ [Runtime] [UI] [Tests]      │
│ Planner     ●  │ terminal                    │                              │
│ PM          ◐  │                             │ Selected sub-agent terminal  │
│                │                             │                              │
│ SUB-AGENTS     │                             │                              │
│ Runtime     ●  │                             │                              │
│ UI          ✓  │                             │                              │
│ Tests       !  │                             │                              │
│ Docs        ○  │                             │                              │
└────────────────┴─────────────────────────────┴──────────────────────────────┘
```

## 7. Sidebar

The sidebar contains two fixed headings:

### AGENT

Primary Codex sessions, such as:

- Planner.
- Project Manager.

### SUB-AGENTS

Delegated Codex sessions, such as:

- Runtime.
- Frontend.
- Tests.
- Documentation.

Each item shows a small status indicator beside its label.

Selecting an agent focuses its terminal in the main workspace.

Selecting a sub-agent activates its tab in the sub-agent workspace.

## 8. Status Indicators

| Indicator | State |
|---|---|
| `●` | Running |
| `◐` | Waiting |
| `✓` | Completed |
| `!` | Failed or requires attention |
| `○` | Idle or queued |

## 9. Completion Signalling

Every automatic Halfway message begins with:

```text
[Halfway Alert!]
```

Examples:

```text
[Halfway Alert!] Runtime completed. Continue orchestration.
```

```text
[Halfway Alert!] Tests failed. Review the sub-agent.
```

```text
[Halfway Alert!] Runtime, UI and Tests completed. Continue orchestration.
```

```text
[Halfway Alert!] All tracked sub-agents completed. Continue orchestration.
```

Halfway reports lifecycle facts only. It must not invent summaries or interpret the work.

## 10. Context Protection

Context protection is a hard requirement.

Halfway must not automatically inject:

- Full prompts.
- Task descriptions.
- Terminal transcripts.
- Agent summaries.
- Test logs.
- Diffs.
- Changed-file lists.
- Repeated state messages.
- Heartbeats.
- Periodic status updates.

Rules:

1. Automatic messages should normally remain under 15 words.
2. Each lifecycle transition creates at most one parent message.
3. Duplicate events must be suppressed.
4. App restart must not resend delivered alerts.
5. Reattachment must not resend delivered alerts.
6. Polling must never generate Codex messages.
7. Metadata belongs in Halfway's local store, not Codex context.
8. Simultaneous completions may be batched into one message.

Default completion mode:

```text
[Halfway Alert!] Runtime completed. Continue orchestration.
```

## 11. Fixed Layout Boundary

Halfway is inspired by tmux but does not attempt feature parity.

Supported:

- Resizable sidebar.
- Resizable main and sub-agent panels.
- Tabbed sub-agent sessions.
- Workspace switching.
- Optional hiding of the sub-agent panel.

Unsupported:

- Arbitrary pane trees.
- Nested terminal layouts.
- Scriptable layouts.
- Generic shell dashboards.
- Remote administration.
- Drag-anywhere docking.
- Built-in code editing.
- General-purpose terminal automation.

The fixed structure is a deliberate product feature.

## 12. Platform Boundary

Halfway is Windows-only.

It should use native Windows technologies and should not compromise its design for theoretical portability.

A future Linux client may be developed separately using a Linux-native stack while sharing stable file formats and event definitions where practical.

Running a Linux GUI build through WSL is not the primary product direction.

## 13. Feature Test

Every proposed feature must pass both tests:

1. Does it make managing Codex agents easier?
2. Can it be implemented without unnecessarily increasing Codex context?

If either answer is no, the feature does not belong in Halfway.
