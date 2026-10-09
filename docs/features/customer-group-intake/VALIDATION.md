# Validation — planning kit only

Date: 2026-10-09. Baseline inspected: AI Office `420db0672fb4b61f28f7ad0de5e63d3bd2ca97ca`. Planning checkpoint containing the tested assets: `7dc3ed5d8a04adec4842f002fbf2e83d1810e15f`.

## Commands actually run

```text
python /mnt/data/ai-office-plan/kit/validate_plan.py --self-test
python -m py_compile /mnt/data/ai-office-plan/kit/validate_plan.py
```

The local directory is a copy of the four planning assets, not a complete repository checkout. A public-repo clone was attempted but DNS resolution failed in the working container; repo inspection/writes used the GitHub connector. No application tests were run or inferred from that failure.

Equivalent command from a checkout after this planning change:

```bash
python docs/features/customer-group-intake/validate_plan.py --self-test
```

Actual result:

```json
{
  "status": "pass",
  "scope": "planning_assets_only",
  "tasks": 15,
  "profiles": 5,
  "fixture_messages": 6,
  "fixture_requests": 2,
  "fixture_report_messages": 1,
  "scenario_specs_not_executed": 24,
  "negative_mutations_rejected": 10
}
```

Checks: unique task IDs, valid acyclic dependencies, no later work/live account qualification blocking the mock profiles; same-group synthetic request evidence; fixed expected report destination; one report; no invented committed deadline in the golden fixture; internal note absent from the example report; scenario IDs and unexecuted status. Ten deliberately corrupted copies were rejected.

These checks do NOT prove semantic request extraction, privacy of arbitrary text, SQL/API/RabbitMQ correctness, authorization races, connector reliability, report delivery or model quality. The 24 Gxx records are acceptance scenarios to implement, not 24 passing application tests.

## Byte identity verified

A separate local comparison calculated Git blob hashes and matched all four against GitHub metadata at planning checkpoint `7dc3ed5`:

```text
68d721daccd3a84d4f1aad34504c83c8a97340e4  backlog.json
db515389df109c903a6a9632c58e1d4bdc234b12  acceptance-cases.jsonl
32bf92885cd48f8176076b7c299dcceca40575f3  examples/golden-batch.json
0a13a00f9cabb1e9d691f52819a8d9710057c244  validate_plan.py
```

This report adds documentation without modifying those tested bytes. Runtime/CI acceptance remains the owning implementation PR's responsibility. Do not treat a documentation merge or existing AI Office tests as proof this group feature is delivered.

## Not performed

No account/session/QR login, live group read/send, production deployment, migration, ERP/customer data write or scheduled task was started. GitHub epic #275 and child issues #276–#279 track future implementation; none is closed or marked deployed. Current #274/#239 leases, global project/workstream state, workflow configuration, secrets and application code were not changed. The documentation PR is a plan-only change and does not claim all future implementation leases.
