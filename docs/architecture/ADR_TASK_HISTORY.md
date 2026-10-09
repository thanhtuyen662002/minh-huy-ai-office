# Owner task history and recovery

Status: implementation in #270 / Draft PR271; acceptance remains open.

## Policy

An active global user with active membership in an active company may rediscover and read their own durable pilot tasks in that company. Administrator roles do not grant access to another owner's archive. Historical reads preserve the accepted result-owner policy: current source/grant permission admits new execution, while reading an already owned task does not reopen ERP, resolve a source secret, dispatch work or charge credits.

The archive service derives scope from server authorization, filters tenant/company/owner before pagination, and requires matching fresh directory authority before queries and after all private reads. The existing opaque `/api/tasks/{id}` result contract gains the same final fence; its fields remain compatible. The new `/api/tasks` list and `/api/tasks/{id}/history` detail are separate allowlisted projections. Company-wide realtime events are not archive discovery or authorization.

## Persistence and projections

Pages default to25, cap at100 and offset10000. Order is `CreatedAtUtc DESC, Id DESC`; the additional descending owner/time index is versioned and preserves the original owner foreign-key index. Reads make no durable changes.

Only the first exact `pilot.task.requested` event is request evidence. Its complete known field set, duplicate rejection, canonical scalar text and submission bounds must validate before a summary of at most241 UTF16 units is published. SQL reads `nvarchar` payload as bounded binary bytes to reject invalid stored UTF16 before SqlClient can repair it. Request/checkpoint payload ceilings are64KiB/256KiB, depth12. Payloads exceeding limits, missing metadata, unknown status, duplicate/unmapped fields or malformed data become bounded unavailable metadata/results. A completed durable task stays completed when its result cannot be projected; an invalid latest checkpoint never falls back to an older answer.

Detail accepts only the existing connection-probe or catalog-grounded AI checkpoint shapes. Source identity and question length must match accepted request evidence; token totals and bounded catalog metadata must validate. It returns an allowlisted answer/probe result, provider/model and observed usage, without raw JSON, secret references, database/table catalog, WaitReason or provider failure diagnostics. Usage observation is not settled billing. Future executor shapes require an explicit contract extension and tests.

## Delivery gates

Focused service/API tests cover owner/company isolation, equal-time pages, exact DTO/no-store, malformed/unknown evidence, final membership/global-user/company races and restored positives, existing result compatibility/source-grant policy, and unchanged durable graphs. This checkpoint alone does not prove SQL execution, browser navigation/recovery, native concurrent revocation, clean Windows, customer ERP, provider completion or full product acceptance. Issued-SID BFF/UI, mandatory owned SQL/Chromium proof and independent frozen review remain on the same271 lease.

## Browser checkpoint

Issued-SID BFF list/detail now validate canonical selectors, exact typed scoped JSON and strict UTF8 in successful replies, and return bounded generic error bodies without upstream diagnostics. Shared buffering/deadline/final SID fences remain. The supported Công việc panel is available to ordinary members; it lists/paginates and retrieves saved status/results through GET only. It supports empty/loading/failure/refresh and completed-with-unavailable states. Component lifetime includes company/user/session generation and request serial, local cancellation and late-result fencing. The session request helper composes caller cancellation with its existing generation cancellation; retained callers without a signal are unchanged.70 focused new web tests/typecheck and40 retained session race/lifecycle tests pass. Mandatory new actual SQL/Chromium acceptance and frozen review remain; these unit tests are not production acceptance.
