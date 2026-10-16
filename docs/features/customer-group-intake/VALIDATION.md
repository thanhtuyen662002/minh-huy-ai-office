# Validation v2 — planning assets only

Date: 2026-10-10. The previous report from PR #280 validated the superseded manual-approved/same-group fixture. It is historical, not evidence for the corrected automatic-IT workflow.

Commands actually run on the new local planning-asset copy:

```text
python /mnt/data/group_intake_auto_v2/validate_plan.py --self-test
python -m py_compile /mnt/data/group_intake_auto_v2/validate_plan.py
```

Result:

```json
{"status":"pass","scope":"planning_assets_only","tasks":15,"fixture_notes":3,"reports":1,"scenario_specs_not_executed":24,"negative_mutations_rejected":10}
```

The checks validate no human approval/manual trigger, no customer destination, explicit source-to-IT grants, committed notes with same-source evidence, dependencies and synthetic fixture shape. Ten mutated copies were rejected: human gate, manual trigger, customer sending enabled, approval click after setup, customer destination, report approval gate, missing routes, uncommitted note, cross-group evidence and dependency cycle.

This is NOT an executable CSKH workflow. No model semantic evaluation, SQL/RabbitMQ/API/UI/connector integration, live account access, message send or deployment ran. The 24 scenario entries are still specifications to implement. Timing values in the plan are tuning proposals, not an activated schedule.

Equivalent checkout command:

```bash
python docs/features/customer-group-intake/validate_plan.py --self-test
```

Local Git blob SHA-1 values for the bytes tested/generated (compare with remote metadata before treating as remote identity proof):

```text
48879111c8300705bd85645617dbf918c34e1dde backlog.json
2a4f89a5312d2cc1ed1b1d41a8dbc302ce66518f examples/golden-batch.json
c9f9e695b5621cdce31fe897ce1e11debe61e4eb acceptance-cases.jsonl
f31db972b3ba6480b057ceb59a4127d94d739421 validate_plan.py
ea1fbab61c151bfd893a5c2b26a4c464874273ee CODEX_PROMPT.md
```

The replacement Codex prompt was written to `/mnt/data/CODEX_AI_OFFICE_AUTO_IT_PROMPT_V2.md`. It is a new artifact; the older downloaded prompt must not be used as authority for runtime approvals or customer-group reporting.
