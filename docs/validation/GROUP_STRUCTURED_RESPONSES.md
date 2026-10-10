# Bounded structured Responses checkpoint — issue278 / Draft288

Local implementation checkpoint only. Automatic SQL notes, model quality and complete issue278 acceptance are still outstanding. Accepted dependency main2e/issue277 is recorded separately in project state.

## Actual shipping path

Agent.Worker Program registers the existing primary/backup Responses adapters using StructuredResponsesPolicy.CreateProductionClient. The adapter now advertises StructuredGeneration and branches to BoundedStructuredResponses; legacy Reasoning remains on its existing serialization/output path. Unsupported Embedding/Vision/Transcription are refused before HTTP.

The request contains only configured model, bounded input, store=false, configured max_output_tokens and text.format. Format is json_schema, strict=true, with an actual schema object. Server tenant/company/task/request IDs and authorization are not extra body fields. Provider authentication is a header.

Host profile: input128000, schema32000, extracted output64000 and response entity262144 UTF8 bytes; JSON depth32/schema nesting10. Closed objects/all required properties, arrays, primitives/nullable primitives and exact typed enums are supported. Unsupported composition/ref/constraint keywords are refused. Escaped aliases cannot hide duplicate properties; escaped surrogate pairs and exact numeric enum identity are validated. These bounds do not establish an actual model token/context budget or validate facts/evidence.

Accept one completed assistant message with one output_text containing a schema-valid JSON object, completed root and bounded canonical model/consistent nonnegative usage. Refusals, incomplete/error/tool/multiple/fallback-only responses fail closed. Host source authorization/evidence grounding and business status decisions remain later checks.

A singleton adapter admits at most4 owned operations. The complete serialization/request/body/parser/disposal lifetime runs inside one <=60s deadline. Cancellation preserves the caller token and asynchronously signals the owned operation. Capacity remains occupied until underlying work, cleanup and cancellation callbacks finish. A permanently unsettled operation retains its permit until controlled process replacement; no recovery from arbitrary hung provider code is claimed. Ordered primary/backup can each own4; the future workflow must supply its aggregate linked budget.

Actual content copies into a fixed private sink through all Stream write overloads. Overflow/cancellation is sticky even if injected content suppresses its write exception. Parsing starts only after content-copy completion and sink sealing; data is cleared when owned processing finishes. ContentLength and actual size must agree when declared. Empty/oversized/invalid UTF8/deep/duplicate/truncated JSON and unsupported Content-Encoding are refused. Redirects/decompression are disabled in the real production handler. This bounds adapter-accepted entity bytes; arbitrary injected content/handler internal allocations are outside that guarantee.

Structured errors are fixed and omit raw bodies/network details/inner exceptions. Legacy Reasoning error/output compatibility is retained and is not included in the new structured privacy guarantee. Large legacy64000 UTF16 CJK outputs remain accepted even when their encoded entity exceeds the structured-only bound.

## Verification performed

- Worker build: zero warnings/errors.
- Actual public adapter and retained legacy/failover tests:91 PASS,0 failures,0 skipped. This includes byte-boundary/+1 tests; closed schema/type/enum/depth/Unicode; actual CopyToAsync overloads/sticky overflow; forged lengths/truncation/UTF8/depth/encoding; completed-message/refusal/error/multiple/usage/metadata controls; unsupported capability before HTTP; transient-only actual structured primary/backup; four-slot deadline retention/recovery for noncooperative headers/body/EOF/synchronous cleanup/callbacks; synchronous handler/late private fault; caller cancellation; real factory loopback redirect destination receives no connection; large legacy Reasoning compatibility.
- Formatter applied only to the six changed C# files; verify-no-changes, diff checks and both strict UTF8 YAML parses PASS. Exact commit/CI evidence belongs in the current PR HANDOFF.
- A newly added encoded-envelope oracle initially assumed emoji escaping exceeded the entity cap; it did not. Corrected the owned fixture to HTML characters, explicitly proved output<64000/entity>262144 and reran all91 successfully. This was a test-fixture correction, not a provider-runtime diagnosis.

Tests use owned synthetic handlers/content and one owned loopback TCP fixture. There were no actual provider/model/customer calls, SQL note writes, live connector access or deployments. Full shipped DI/SQL/broker/restart/no-click/browser and frozen actual >=100/>=30 semantic evaluations remain required. Proposed120/40 labels are UNEVALUATED.

## Next executable action

Review this exact source/test/docs checkpoint independently before pushing the same Draft288, close exact-head CI, then implement durable batches and the scoped SQL-backed brain/notes/outbox. Keep278 open until every acceptance item is proven.279 IT reporter and full233/ERP/Windows gates remain outstanding.
