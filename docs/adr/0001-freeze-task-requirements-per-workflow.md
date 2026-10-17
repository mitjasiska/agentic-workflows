---
status: superseded by ADR-0003
---

# Freeze task requirements per workflow

Agentic Workflows captures one immutable task snapshot when a workflow begins and supplies it to every implementation and review pass. Source changes, especially edits to a Linear issue, produce a warning rather than silently changing a running workflow; this prevents implementation and review from operating against different requirements, deliberately replacing the prototype's latest-source behavior.
