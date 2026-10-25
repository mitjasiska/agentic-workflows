# Traceability contract

Use one JSON object per actual readiness evaluation, not per conversation or queue lookup. The readiness field has a fixed value; the human-facing response also explains the evidence and next action. A factual context blocker that prevents evaluation has no readiness verdict yet.

Keep the object in the response or a permitted temporary artifact for handoff. A repository file is needed only when requested by the task. Use actual observed source metadata; the values below illustrate the shape:

```json
{
  "schema_version": 1,
  "operation": "prepare-task",
  "source_state": {
    "kind": "git",
    "revision": "current-revision",
    "dirty": false,
    "observed_at": "2030-01-01T10:00:00Z"
  },
  "authority_sources": ["AGENTS.md", "docs/architecture/relevant-source.md"],
  "work_item": {
    "identifier": null,
    "title": "Requested outcome",
    "requirements": ["Observable acceptance requirement"]
  },
  "guidance": [
    {
      "status": "Product Invariant",
      "source": "docs/architecture/relevant-source.md",
      "section": "Relevant section",
      "summary": "Applicable boundary",
      "material": true
    }
  ],
  "implementation_latitude": ["Reversible local choice"],
  "conflicts": [],
  "affected_tasks": [],
  "readiness": "Ready"
}
```

`operation` is `prepare-task` or `reconcile-architecture`. `identifier` may be `null` when no task exists and otherwise remains opaque; do not infer configuration from its format. Each affected task contains `identifier`, `title`, `reason`, and `proposed_action`.

`readiness` is exactly `Ready` or `Architecture decision required`. A Ready trace cannot contain a conflict or a material Open Area. A blocked trace must cite at least one conflict or material Open Area.
