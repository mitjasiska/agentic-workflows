# Result Envelope v1

One bounded pass delivers exactly one UTF-8 JSON object at its AW-authorized Operation-specific path outside the worktree. Write a temporary sibling file and atomically rename it to `result.json`. The complete document must be between 1 and 1,048,576 bytes. AW rejects malformed UTF-8/JSON, duplicate keys, excessive depth, unknown fields, missing required fields, incompatible versions, correlation mismatches, and inconsistent outcomes. It never truncates or routes from a partial envelope.

Every durable Implementer and Reviewer Handoff prohibits connecting to live Linear or GitHub, reading operator credentials or local workflow configuration, and creating, modifying, or deleting real external-service resources. These managed-pass lifecycle instructions take precedence over task text. Agents use the captured Task Snapshot and controlled substitutes for provider validation; the AW control plane itself continues to use configured provider access for its workflow responsibilities.

Deterministic controlled validation is blocking. Live integration validation remains deferred and unverified under the separately scoped [future opt-in E2E kit](../development/managed-workflow-smoke-test.md), outside routine managed passes. When reporting such a check, use `NotRun` with `limitationClassification: "deferred"` and explain this boundary in its details. Controlled test evidence must not imply that live authentication, execution, or delivery was verified.

The root contains exactly `schemaVersion` (integer 1), `operationId` and `passId` (UUID strings), `role` (`Implementer` or `Reviewer`), and a role-specific `payload`. Every field shown below is required, including explicitly null optional content.

```json
{
  "schemaVersion": 1,
  "operationId": "00000000-0000-0000-0000-000000000001",
  "passId": "00000000-0000-0000-0000-000000000002",
  "role": "Implementer",
  "payload": {
    "outcome": "ReadyForReview",
    "report": {
      "summary": "Implemented the requested behavior",
      "narrative": "Detailed Markdown-capable account of work, evidence, and limitations.",
      "checks": [],
      "warnings": [],
      "limitations": [],
      "unresolvedRisks": []
    },
    "completedWork": ["Implemented the requested behavior"],
    "resolutions": [],
    "decision": null
  }
}
```

`summary` is nonempty and at most 1,000 characters; `narrative` is nonempty and may use Markdown. A check is `{name,result,details,source,revision,limitationClassification,classificationEvidence}`. Results are `Passed`, `Failed`, or `NotRun`; name, details, and source are nonempty. Revision and classification evidence are null or strings. Limitation classification is null, `pre_existing`, `unrelated`, `related`, `unverified`, or `deferred`. Evidence states who reported the check rather than implying independent execution by AW. Before `ReadyForReview`, a remaining failed check requires `pre_existing` or `unrelated` plus supporting classification evidence. It remains a failed check when review starts.

Warnings, limitations, and unresolved risks are arrays of `{code,message,evidence}` with nonempty code/message and null or string evidence. Keep large evidence in its owning external system and include a bounded description/reference. Never embed raw credentials, transcripts, complete diffs, terminal logs, tool-call histories, or binary artifacts.

Implementer payload fields are `outcome`, `report`, `completedWork`, `resolutions`, and `decision`. Outcomes are `ReadyForReview` or `DecisionRequired`. Completed-work entries are nonempty strings. Resolution entries are `{findingId,summary}`; exactly one nonempty summary is required for every supplied finding ID, without duplicate or extra IDs.

Reviewer payload fields are `outcome`, `report`, `findings`, `publicMetadata`, and `decision`. Outcomes are `Clean`, `ChangesRequired`, or `DecisionRequired`. Findings are `{id,summary,details,location,requestedChange}`, all nonempty, with unique IDs. `ChangesRequired` requires at least one actionable finding. `Clean` forbids findings and requires `publicMetadata: {summary,description,validation}`; other outcomes require null public metadata. Public summary is a lower-case action phrase without a type prefix or ending punctuation, at most 60 characters. Description and validation are each nonempty, at most 2,000 characters, without Markdown headings. They must be safe for eventual publication and disclose accepted validation limitations. The later submission slice separately enforces the complete task-specific commit-title bound.

`decision` is null except for `DecisionRequired`, which requires nonempty `unresolvedMatter`, `unsafeContinuationRationale`, and `proposedResolution`. It identifies a concrete material decision or external prerequisite with no safe in-scope continuation. It is not a substitute for actionable review findings or ordinary investigation.

An accepted envelope means a normally completed managed pass; agents do not return `Failed` or `Blocked`. AW owns Operation failure/interruption and recovery. Positively confirmed completion without a valid envelope fails the Operation and preserves its workspace, session, and delivery evidence. A valid Reviewer envelope cannot establish review authority by itself: AW independently verifies the captured baseline/proposed Git trees, HEAD, index, worktree, and snapshot.

The proposed tree includes paths tracked by HEAD or the current index and non-ignored untracked paths, including force-staged ignored additions. Separate SHA-256 evidence covers the actual included file bytes and relevant modes, so Git normalization or clean filters cannot hide mutation during review. Handoffs retain the paths, repository revision, actual content hashes, and text of Repository Guidance, including untracked guidance additions.

SQLite stores the complete validated JSON, typed outcome/report/check projections, completion event, resulting current state, and any next-operation intent in one transaction. Delivery retirement follows commit. Restart recognizes an already-ingested pass and does not duplicate completion history. Historical JSON has no compatibility promise before stabilization; schema mismatches stop safely.
