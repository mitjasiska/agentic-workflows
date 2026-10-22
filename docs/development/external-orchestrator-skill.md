# External Orchestrator skill development

The maintained source for the model-invoked skill is [`skills/external-orchestrator/`](../../skills/external-orchestrator/). Edit and review that copy; installed copies are deployment artifacts, not sources of truth.

## Validate

Run the package validator supplied by the agent environment, then run the focused tests:

```text
python <skill-creator>/scripts/quick_validate.py skills/external-orchestrator
python -m unittest discover -s skills/external-orchestrator/tests -v
```

The traceability validator accepts a JSON path or `-` for standard input:

```text
python skills/external-orchestrator/scripts/validate_traceability.py <trace.json>
```

## Install

Copy or synchronize the complete `skills/external-orchestrator/` directory to `external-orchestrator/` under the agent's configured skills root. Restart or reload the agent so it rediscovers the skill. Do not embed a machine-specific skills path in this repository.

Install `create-linear-task` separately. The External Orchestrator composes that skill at runtime and intentionally does not contain its Linear target configuration or issue renderer.
