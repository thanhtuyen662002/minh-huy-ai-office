# Release Strategy

## Version pinning
Long-running tasks record runtime/workflow/skill/schema-contract versions. New tasks may move to a new release while old tasks drain on the compatible release.

## Database evolution
Default: EXPAND -> MIGRATE -> CONTRACT.
Breaking removal waits until old consumers/tasks are drained or explicitly migrated.

## Rollout
Build -> tests -> security/evals -> staging -> health check -> canary -> progressive rollout -> drain old workers.

## Rollback
Stop routing new tasks to the bad release and route to the last known-good compatible release. Database changes must be designed so rollback remains possible during the compatibility window.

## Emergency release
1. Kill/disable vulnerable capability if possible.
2. Create security-hotfix branch.
3. Run targeted + regression/security tests.
4. Deploy canary.
5. Restore capability.
6. Resume waiting tasks.

## Immutable production
Production artifacts are built from Git commits. Manual server edits are emergency evidence only and must be reconciled back into source immediately.
