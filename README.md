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
