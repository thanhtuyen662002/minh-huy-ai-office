# ERP Evolution and Adaptation

## Goal
Adapt to customer-specific schema/forms/reports/features without hard-coding agents to one ERP snapshot.

## Registries
- Data Source Registry: logical -> physical DB resource.
- Schema Registry: tables/views/SP/functions/triggers/indexes/FKs by version.
- ERP Catalog: DB objects + forms + reports + menus + actions.
- Feature Registry: business features and installed version per company.
- Capability Registry: stable semantic operations agents depend on.
- Compatibility Registry: supported contract versions.

## Change detection
Capture schema/source/form/report snapshots and diff them by release. Classify changes as additive, compatible, breaking or unknown.

## Impact analysis
Map changed object -> feature/capability -> skill/workflow/agent/report dependencies.

## New features
A feature such as automatic replenishment is a versioned package containing:
- business requirements,
- schema migrations,
- stored procedures/services,
- forms/configuration,
- reports,
- permissions,
- capability manifest,
- tests/evals,
- documentation.

## Learning after deploy
Deploy -> discover/index -> update catalog -> capability tests -> expose capability to Manager. A detected object is not automatically trusted as a business capability until sufficiently understood/tested.
