# Halfway UI Specification

## 1. Design Objective

The interface should look and behave like a polished Codex workspace rather than a configurable terminal platform.

The terminal remains the primary interface.

## 2. Main Layout

```text
┌────────────────────────────────────────────────────────────────────────────┐
│ Information Bar                                                            │
├────────────────┬─────────────────────────────┬──────────────────────────────┤
│ Sidebar        │ Main Agent Terminal         │ Sub-Agent Terminals          │
│                │                             │                              │
│ AGENT          │                             │ [Runtime ●] [UI ✓] [Tests !]│
│ Planner     ●  │                             │                              │
│ PM          ◐  │                             │ Active tab terminal          │
│                │                             │                              │
│ SUB-AGENTS     │                             │                              │
│ Runtime     ●  │                             │                              │
│ UI          ✓  │                             │                              │
│ Tests       !  │                             │                              │
└────────────────┴─────────────────────────────┴──────────────────────────────┘
```

## 3. Information Bar

Display only high-value workspace information:

- Workspace name.
- Repository.
- Branch.
- Codex model where detectable.
- Running count.
- Waiting count.
- Completed count.
- Codex connection status.

The bar must not become a dashboard.

## 4. Sidebar

Fixed headings:

```text
AGENT
SUB-AGENTS
```

Each row contains:

- Session label.
- Status indicator.
- Optional unread indicator.

The status indicator should sit directly beside or at the far right of the label.

No agent cards.
No expanded metadata panels.
No hierarchy tree unless later proven necessary.

## 5. Main Agent Workspace

Displays the selected primary Codex terminal.

Required terminal behaviours:

- Keyboard input.
- Copy and paste.
- Scrollback.
- Search.
- Resize.
- Visible focus state.
- Session restore where technically possible.

No chat bubbles.
No markdown transformation.
No replacement prompt box.

## 6. Sub-Agent Workspace

- Uses tabs.
- One real Codex terminal per tab.
- Sidebar selection activates the matching tab.
- Tab status mirrors the sidebar status.
- Tabs may be closed only through an explicit session action.

Example:

```text
[Runtime ●] [Frontend ✓] [Tests !]
```

## 7. Interaction Rules

- Clicking an Agent opens that terminal in the main panel.
- Clicking a Sub-Agent selects that terminal's tab.
- A completed sub-agent remains visible until dismissed or archived.
- A failed sub-agent remains visually prominent.
- Status changes must update without requiring manual refresh.
- A terminal whose live coordinator ownership ends must stop displaying Running or Waiting; it reconciles once to Completed, Failed, or Disconnected from owned completion facts without automatic restart.

## 8. Alert Presentation

When a completion alert is sent:

- The sub-agent indicator changes.
- The parent terminal receives the short alert.
- The UI may show a subtle delivered marker.
- No modal dialog is required.
- No verbose toast is required by default.

Example:

```text
[Halfway Alert!] Runtime completed. Continue orchestration.
```

## 9. Visual Style

- Native Windows feel.
- Dense enough for professional use.
- Clear focus and selection.
- Minimal animation.
- Strong terminal readability.
- Light and dark themes may follow the Windows setting.

## 10. Deliberately Excluded UI

- File explorer.
- Source editor.
- Diff viewer in MVP.
- Git sidebar.
- AI chat pane.
- Prompt library.
- Kanban board.
- Dashboard cards.
- Arbitrary pane splitting.
- Complex docking.
