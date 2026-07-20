# Halfway Engineering Principles

## 1. Codex Remains Codex

Halfway must not recreate or reinterpret Codex intelligence.

## 2. The Terminal Is the Product

Every agent is represented by a real Codex terminal session.

## 3. Metadata Stays Local

Agent status, relationships and lifecycle events belong in Halfway's local store, not the Codex conversation.

## 4. Context Is Expensive

Automatic messages must be short, deterministic and deduplicated.

## 5. Native Windows First

Use Windows-native APIs and interaction patterns when they improve reliability or polish.

Do not weaken the Windows application to preserve cross-platform UI portability.

## 6. Fixed Beats Flexible

Halfway deliberately provides one strong layout rather than arbitrary terminal configuration.

## 7. No Feature Creep Into an IDE

Reject editors, file explorers, Git clients, debuggers and project-management features.

## 8. Transparent Automation

All Halfway-generated messages must start with:

```text
[Halfway Alert!]
```

Halfway must never silently alter user prompts.

## 9. Facts, Not Interpretations

Halfway reports:

- Completed.
- Failed.
- Waiting.
- Disconnected.

It does not generate narrative summaries of agent work.

## 10. Reliable Before Clever

Prefer a small deterministic lifecycle system over model-driven orchestration logic.

## 11. Recover Without Lying

After a restart, verify process and session state before displaying an agent as running.

## 12. One Event, One Alert

A terminal-state transition may create at most one automatic parent alert.

## 13. Established Components First

Use proven terminal controls, SQLite libraries and Windows APIs rather than rebuilding foundational infrastructure.

## 14. Boundaries Are Features

The product becomes worse when it tries to support every terminal, model or development workflow.
