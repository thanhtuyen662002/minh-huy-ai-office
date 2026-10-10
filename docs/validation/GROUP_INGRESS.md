# Group ingress: implementation and evidence

Issue277/PR283 implements the automatic customer-source ingress accepted by
issue276. Contracts PR282 is delivered on main4cfeda58; PR283 now targets main.
The active PR HANDOFF records the exact candidate and workflow IDs.

## Current owned runtime acceptance: 2026-10-10

Frozen runtime HEAD `c619adfd5c7681f76cbe53d9e91b57f0cc8c8161` has terminal
Build38035314357/Governance38035314405/all eight quality gates SUCCESS.
Actual114164418527 and aggregate114167539832 passed. Root inspected a selected set of80 required
retained/new fixed native and Chromium markers with zero terminal failures, plus
all six original images in artifact11664207582. The current jobs passed2027.NET
and1334web tests. Independent full frozen core277 review APPROVED6095552242
verified the current logs/gates and all six originals. Accurate final doc-only
review and refreshed final PR/main gates remain before integration. The following checkpoint sections record history; their
earlier pending/failure statements do not override this result. The80 count is the selected
retained/new required-marker set, not the overall PASS-line count. It includes
retained542 actual/shipping/prepare markers and all11 current reference/recovery
constants; the obsolete inflated application-restart label was replaced.

- Actual SQL admission/filtering, concurrent100 original receipts, edit/recall and
  late-history gaps, protected content, commit cursor, transaction rollback,
  current revocation/restore and exact private read release all passed.
- Actual separately fenced listener and encrypted spool survived observed SQL
  commit followed by lost ACK and owned process death. Fresh current SQL grant
  denial occurred before the spool key; exact encrypted backlog was retained.
  Restart reconciled the original ACK without changing the full source graph.
- The shipping Worker recovery factory and registered hosted loop were exercised
  without injected session/spool/client implementations. Core restart, current
  grant403/exact restore, new process owner/epoch2, original receipt and deletion
  of only the acknowledged capture passed. Recovery-only startup used the actual
  runtime SQL principal and validated inert DI without resolving keys/storage.
- Actual reference-only outbox/RabbitMQ/shipping Core producer/Worker consumer
  passed lost broker ACK/process death/redelivery,100 duplicates, insert rollback,
  current Extract revoke, unsafe column refusal/exact restore, Core/Worker restart,
  unchanged full graph/cursor and absence of fabricated portal tasks/users.
- A persistent original reference remained ready across an actual owned RabbitMQ
  process restart, with the same inspected container and a later StartedAt. Exact
  payload/protocol/persistent properties and full SQL graph were checked before
  and after restart, without any republication. The shipping consumer resumed;
  one snapshot met exact ACK+1/delivery+1/consumer1 within the unchanged30s bound,
  then empty queue/count2/full8 graph/portal counts/pooled isolation passed.
- Issued-session Chromium exercised shipping group catalog/message/body reads,
  source read denial/restore, logout late-body fencing and mobile navigation.
  Retained owner-task history stayed private. Both committed202 headers-loss and
  body-loss explicit replays passed the unchanged full EOF/UTF8/JSON/original
  receipt/SID/all-eight-effect-graph checks; later owner/company gates also passed.

Earlier replay timeouts remain recorded as failed evidence; their product cause
is unproven. No shipping timeout or receipt oracle was weakened to pass this run.
Passive owned diagnostic metadata is never an accepted receipt. A separate pinned
local Chromium synthetic transport control passed, but a local Next transport
probe stopped at readiness404 before any submit; it supplies no product-cause
or native SQL/OIDC acceptance evidence.

The full542 review withheld issue277 closure because only Core/Worker had restarted.
Scoped queue-proof approval6094998445 supplied the literal pending-reference
restart. Exact244 passed its pre-consumer survival checks, then failed the immediate
post-ACK delivery/consumer observation; numeric counters were absent, so its cause
remains unproven. Scoped observation repair6095219736 closed independently reproduced
delayed-snapshot false refusal and mixed-snapshot extra-ACK acceptance, preserving
strict equality and deadlines. The reviewed proof executed successfully on exactc619.
Worker restoration still runs on a lost stop reply and retains the first failure.
Local guard/adversarial controls remain distinct from this actual native evidence.

Configuration was
restored to shipping default-off and no live connector login, send or activation
occurred. Actual provider/account and production volume qualification remain
separate gates. Issue278 must implement durable automatic batching, SQL brain,
business/attention notes and atomic NotesCommitted; issue279 must implement
separately authorized internal IT disclosure/reporting. No business employee
Generate/Accept/Approve/Send prerequisite is introduced. Full233 remains active.

## First SQL checkpoint

The additive `AddGroupSourceIngress` migration adds the operator-owned service,
connector account, binding, service capability and portal read-grant registry.
Source and internal destination are different roles. A global physical-group
index refuses a second role/account/company alias; identity hashes remain only
indexes and do not replace full ordinal string checks in the runtime.

Group listener leases, source commit cursors, immutable messages/revisions and
receipts, coverage gaps and reference-only outbox rows have tenant/company keys
and restrictive foreign keys. They do not use portal task ownership or expose
private owner-task history. Pending first/last timestamps and a committed cursor
are persisted for the automatic batch scheduler in278; no RAM timer or employee
approval is introduced. Reconnecting a listener does not erase its coverage gap.

Runtime SQL cannot enroll accounts, grant capabilities, change binding roles or
renew qualification. Registry rows are operator-owned and read-only. Original
message/revision/receipt rows are append-only. Mutable tables deny changes to
scope and immutable references, including column-level UPDATE grants. A dedicated
effective-permissions verifier composes with the existing global escalation,
ownership-chain and trigger proof. Destructive migration Down is refused; source
history requires reviewed forward repair.

Original source content uses AES-256-GCM with a random96-bit nonce and128-bit tag.
Tenant/company/source/message/revision/source-version/deletion-generation/key-ID
are authenticated associated data. Key material remains outside SQL source rows
and Git; the stored key identifier supports explicit retained-key resolution.
Text is strict UTF8, bounded to8000 UTF16 units, and plaintext byte buffers are
cleared after protection/decryption. This format does not itself authorize a
read: the runtime must check current authority before key resolution and private
content release. No encryption key or live credential is shipped by this change.

## Verified locally at the first checkpoint

- Nine new tests PASS: cross-scope/revision/deletion/key refusal, tampering,
  exact Unicode/whitespace/empty round trips, adjacent overflow and invalid scalar
  refusal, authenticated malformed/oversized cleartext, relational isolation,
  physical-role uniqueness, cursor/outbox invariants and migration permissions.
- Full869 Persistence tests PASS, zero skipped.
- Changed-file C# formatting, pending-model check and diff check PASS.
- Forward SQL generated from accepted task-intent migration without applying it.

## Service authentication checkpoint

The server verifies bounded strict JSON and an HMAC over the exact request body,
service ID, credential epoch, timestamp and nonce. SQL derives scope from the
current operator registry and requires the separate Ingest capability. A portal
JWT or request-supplied tenant, grant, qualification or secret reference cannot
construct the authenticated identity. Full original UTF16 bytes are checked
after index lookup; the original physical-group catalog independently refuses
role/account/company aliases even if an index is malformed. Secret resolution
and the final authenticated result are fenced by current SQL authority and the
effective-permissions proof on the same pinned transaction connection.

Live policy requires current controlled connector receive qualification. An
explicit owned Development fixture can exercise a synthetic connector and
cannot qualify or activate live use. Edit/recall also require current evidence
for those capabilities. Source content keys resolve only from exact configured
tenant/company/group/key enrollments, with explicit retained read keys and no
cross-source fallback. Key buffers are copied and cleared on disposal.

Thirty-six focused authentication/key controls PASS, including physical-role
aliases, exact case/padding identities, malformed stored UTF16, strict nested
JSON, signature/skew boundaries, current snapshots and private key refusal.
These use owned in-memory fixtures; actual SQL rights and concurrent commit
acceptance remain required. No API, store, listener or sender is exposed yet.

Independent review found mutable caller-memory, expired final signing/evidence
and a known-synthetic artifact labeled ControlledAccount counterexample. The
repair owns one bounded body snapshot before any await, parses/signs that same
snapshot and clears it afterwards. Final authentication rechecks signing time
and current qualification after asynchronous work. The verified service carries
its signed time into every protected store admission/final fence. Live policy
explicitly refuses the known synthetic provider/artifact regardless of registry
labels; an owned controlled fixture retains a positive policy control.

Eight new regression controls plus full934 Persistence tests PASS, zero skipped.
These local results require independent counterexample closure and exact CI;
they are not evidence of actual connector qualification or SQL runtime success.

## Transactional source store checkpoint

Only the server-authenticated identity can call the store. A serializable pinned
SQL transaction takes the source application lock before current registry/lease
admission, then commits the protected immutable revision, exact logical-event
receipt, source counter and reference-only outbox together. The counter is read
fresh after the prior commit lock; a tracked snapshot or allocated identity is
not a committed cursor. Current authority, qualification, listener epoch/expiry
and effective rights are rechecked before saving and before committing.

Receipt identity binds the original event metadata and text. Transport signature
nonce/time and listener owner/epoch are excluded from the logical fingerprint,
so an identical protected spool event can reconcile after a listener restart.
A changed logical event under the same event ID is refused. Duplicate ACKs
resolve the original source/revision reference and do not renew pending time,
resolve another content key, create an outbox or allocate another sequence.

Edit/recall history is append-only; an original-unseen gap is persisted when the
first observed event is an edit/recall. A later original can be retained without
erasing that gap. The authorized current-source projection and extractor must
apply edit/recall precedence: a late original is not an undo of an edit or recall.
Historical backfill remains explicit on every revision; later batch processing
must separate it from notification-eligible live input. No current-content or
notification projection is delivered by this store checkpoint.

Twenty-four store controls PASS, including one protected graph, one hundred
sequential replays and changed listener recovery, six changed-envelope refusals,
stale/foreign leases, final authority/key/expiry denial before effects, immutable
edit/recall/late-original gaps, overflow and an independently committed context
against an old tracked cursor. These in-memory controls do not prove actual SQL
concurrent commits or rollback after a failed SQL write; those gates remain.

## Internal HTTP and timestamp repair checkpoint

POST `/internal/group-ingress/events` is a separate HMAC service endpoint. It is
default-off: the host must explicitly set `AIOffice:GroupIntake:Enabled=true` and
configure the platform SQL connection. Live controlled qualification is the
default policy. The synthetic policy additionally requires a Development host
and both `OwnedSyntheticFixture=true` and `OwnedDisposableFixture=true` under
that same server section. Client headers/body cannot select this policy.

The five canonical `X-AIOffice-Group-*` headers carry Service, Epoch, Signed-At,
Nonce and Signature. No query scope selector is accepted. Declared and streamed
bodies have the same64KiB bound. Current SQL service/source grants determine
scope; tenant/user headers provide no authority. A200 response contains only
committed receipt metadata after the store transaction commits; it does not
claim extraction, broker delivery or IT notification. Disabled/errors/success
all receive no-store. Responses expose bounded categories, never source text,
secret references or database exception details.

`AIOffice:GroupIntake:SourceKeys` is a private host configuration array with
TenantId, CompanyId, SourceBindingId, KeyId, SecretRef and IsWriteKey fields.
It enrolls exact scopes and retained keys; values/credentials stay outside Git.
The runtime registry remains operator-owned. This endpoint does not create or
renew a listener lease; an authorized fenced listener remains required.

Independent auth probes now close all four previously accepted invalid cases.
The store also refreshes immutable write timestamps after asynchronous key and
final authorization checks. Mutable first/last pending anchors account for the
first source SaveChanges/final-proof delay inside the same transaction. Owned
40second key/write delays retain a30second quiet period after that work; they
are not a measurement of actual physical SQL commit latency. The downstream
scheduler must read committed state and preserve its maximum-window semantics.

Twenty-eight real Core HTTP/DI controls and two store delay regressions PASS;
full959 Persistence tests PASS, zero skipped. HTTP tests use owned in-memory
persistence and explicit Development policy. Actual SQL/concurrency/rollback
acceptance for this new path remains required. Original independent timestamp probes now2/2 PASS. HTTP review found lazy key-provider initialization outside the handler and an unbounded stalled body. The repair validates the complete enrollment before serving and adds a linked10second body/auth/store deadline. Owned timeout returns bounded no-store503 and requires same-event reconciliation; caller abort remains an abort. Three invalid enrollment and two stalled/caller-abort regression controls PASS; focused52 HTTP/store tests PASS. Frozen repair review remains required; earlier061 CI all8 PASS and retained
realCore202 header/body-loss proof do not execute this new group HTTP path.

## Remaining acceptance

There is no worker/listener/spool, broker consumer or private inbox UI yet. Actual SQL application/effective-rights proof, commit-order
and duplicate100/revision/recall controls, rollback/no-outbox proof, protected
source reads, fenced listener/restart, broker interruption/recovery and issued
session denial/restored positives remain required. No runtime receipt, group
brain, note creation or notification success is inferred from model tests.

277 must finish those paths.278 owns actual scoped SQL source/notes/glossary
context, automatic extraction and atomic business notes.279 owns SQL-driven
internal IT reports and unknown-send reconciliation. Live connector qualification
remains separately unverified; synthetic fixtures must never activate live use.

## Owned native SQL proof being added

The complete-stack CI invokes `scripts/smoke-group-ingress.py` against its exact
owned disposable GitHub fixture. Before configuration/files/processes it checks
CI, GITHUB_ACTIONS, RUNNER_TEMP/aioffice-local and the fixed loopback API. Private
random HMAC/content keys live in an exclusive0600 override outside Git. Only
that Development fixture enables synthetic mode, temporarily recreates Core,
then removes private configuration and restores shipping default-off behavior.
SQL fixture bytes use stdin; diagnostics contain fixed categories/message IDs.

The mandatory new gate checks shipping HTTP-to-runtime SQL admission, concurrent
100 same-event replays, complete durable-byte snapshots, original UTF16 and
physical-role aliases, actual outbox failure/whole-graph rollback, real column
permission escalation/refusal, observed queued source/grant/epoch/account/
deletion/listener revocation with restored positives, append-only edit/recall/
late historical gaps, contiguous cursor and retained Core restart. It preserves
portal task/user counts. Four new local fixture-boundary/vector/privacy tests
and all14 stack guard tests PASS. Native execution is pending; no SQL acceptance
or listener/queue/private-inbox delivery is claimed from script existence.

Independent native fixture review found four incorrect/incomplete oracles. The
repair uses canonical COALESCE empty-array digests and an actual empty-source
positive; correct Edit3/Recall4/original1 kinds plus exact per-message revisions,
content hashes/backfill flags and unchanged original-gap bytes; the complete
ordered1..cursor vector (rejects1,3 atcursor2); and unconditional private-file
unlink in a nestedfinally when default restore or readiness fails. All16 local
fixture guard/vector/privacy/oracle/cleanup tests PASS. This repair still needs
frozen review and actual exact-head native execution. Scoped HTTP repair was
independently approved with32 controls, including the true configured-key
positive without a key-provider mock (PR283 comment6090044353).

Exact e228 native job114046156901 failed the first new group committed ACK with
403 after retained native controls passed, before duplicate/history execution.
This is an actionable refusal, not infrastructure. Fixed server-only auth/store
phase labels and fixed owned permission predicate IDs diagnose that fence;
no input, identity, credential, source text or exception/log dump is emitted.
The repaired history projection also converts bounded JSON to nvarchar4000 and
verifies exact original UTF16 byte length against SQL before parsing (sqlcmd's
default256 limit for max types otherwise truncates the408character fixture).
All17 local ownership/privacy/transport/oracle/cleanup guards and88 focused
shipping auth/HTTP/store controls PASS. Native current-fence diagnosis, frozen
final review and exact-head execution remain required.

## Protected source read checkpoint (not yet exposed)

GroupSourceReader reads one committed source message for a current portal member
with an explicit exact GroupReaderGrant. Administrator membership does not
supply that grant or an IT disclosure grant. Source role/original identity/
physical catalog/version/deletion and current grant versions fence key work and
final release. SQL reads pin the connection identity in ReadCommitted, use bounded
original UTF16 bytes and reject caller/ambient transactions rather than holding
membership/grant locks across private key awaits. No source version or key/ref
can be selected by HTTP/model input.

Content must decrypt in its exact source/message/revision/version/deletion/key
context and match original message hash, source SHA256 and immutable logical
receipt envelope. Recall outranks edits; edits outrank a late original. A recall
returns metadata with null text. The final committed winner, original message/
receipt bytes and current grants are rechecked after materialization; a recall
or external revoke during key work discards the earlier private body. This is a
read of source history, never another portal user's task archive or an automatic
IT disclosure.

Twenty focused read controls PASS using owned in-memory fixtures: exact Unicode/
reply/source and no task/user/write effects, administrator/foreign user/company/
role/grant/index denial before key work, six late current-revoke fences, cipher/
receipt/message/sender corruption refusal, recall/edit/late-original precedence
and recall committed during key await with restored recalled projection. This
class has no shipping DI/API/list/read UI registration yet; frozen review and
actual SQL/issued-session private-read/late-body evidence remain required.


## Protected read final release repair

Independent frozen `ccd9b98` review reproduced a committed recall during the final awaited authority check returning the old private body. The final read now starts a short owned Serializable transaction **after** key resolution/decryption, checks current member/grant/source authority before winner/message/receipt/gap, and commits as its documented read linearization boundary. No external key await occurs under final read locks. The pinned SQL session returns to ReadCommitted on disposal. A late recall must be observed before the winner snapshot or its insertion must wait until read commit; SQL lock/range evidence is still required.

Local: all 21 reader controls PASS, including final-authority recall refusal followed by a fresh Recall/null positive read. Existing key-await revocation and recall controls remain. Reader is not API/DI/UI exposed yet. Actual runtime diagnostic at remote `42cc94d`, Build37998551981/Governance37998552028/actual114050684002, remains pending; six prerequisite gates passed. No source feature delivery claimed.


## Native runtime permission refusal repair

Remote `42cc94d` Build37998551981/actual114050684002 is terminal red: first group ACK403, fixed phase `auth/permissions-initial`, predicate085/095/105/115 fail. These are the four mutable tables' table-wide UPDATE requirements. The existing migration intentionally DENYs UPDATE on immutable key/evidence columns, so whole-table UPDATE is not the runtime contract. See [Microsoft effective and column permission checks](https://learn.microsoft.com/en-us/sql/t-sql/functions/has-perms-by-name-transact-sql?view=sql-server-ver17). The observed native failure is the evidence for this specific mismatch.

Repair: require effective UPDATE=1 on every fixed allowed mutable field (lease owner/epoch/times, source cursors/pending times, gap reconnect time, outbox delivery times/attempts), and effective UPDATE=0 on **all remaining columns**, including new unexpected columns. Missing columns/NULL permission results refuse access. Registry and append-only checks remain unchanged; no migration/grants were broadened. Owned SQL proof now executes rolled-back permitted updates as the runtime login, denies a writable field, and grants an immutable key to verify actual denial/no effects and restored positive controls.

Local 113 model/auth/HTTP/store/reader controls PASS (0 skipped), 17 ownership/privacy/history/cursor/cleanup guards PASS, Python compilation/changed solution whitespace/diff PASS. New native repair has not run yet. Frozen reader814 independent review closes its original recall P2: 33 retained/own controls PASS; native range/isolation proof and private HTTP/UI remain required. No whole277 approval or live qualification.


## Private source GET checkpoint

Default-off GET `/api/group-sources/{sourceId}/messages/{messageId}` is now registered with the same scoped reader/key provider. Enabled reads require configured portal authentication and current selected-company membership plus explicit source ReaderGrant; an Ingest service HMAC cannot authenticate this portal route. Canonical nonzero GUIDs only, no request authority selectors, fixed10second linked deadline, no-store including refusals, and bounded generic503 on stored/key/SQL failures. View exposes only source/message/current revision metadata and authorized plaintext; no key IDs, encrypted buffers, credential refs or portal task ownership.

New HTTP controls cover exact protected source/current member, nonadmin granted reader positives, admin without current grant/disabled user/member/source/foreign company/source denial, selector/noncanonical route denial, key failure/late grant loss and restored positive, HMAC-only anonymous401, default-off404 and private cache policy. Actual native fixture now requires the existing issued portal token and explicit owned operator ReaderGrant; exact Unicode source GET, anonymous/foreign/grant denials/restoration and Recall/null after late history are mandatory. Those native controls have not executed yet; API/DI checkpoint awaits independent review and its own exact CI. No BFF/inbox UI yet.

Local privateHTTP checkpoint:79 current HTTP/reader/key/protection/retained ingress tests PASS, zero skipped;18 new portal group-read controls.17 owned guards/Python compile/changed solution whitespace/YAML/diff PASS. These are local TestServer/InMemory controls, not native acceptance.


## Initial portal identity boundary repair

Frozen84ca independent review reproduced private exception/stack leakage and missing total deadline during RequestAuthorizationContextMiddleware's initial authenticated-directory await, before the GET handler. A group-source path-only request boundary now wraps authentication/initial directory and handler, links a10second token through RequestAborted, restores the original caller token, and returns generic no-store503 for failures before response start. Caller cancellation still propagates without successful/private response. Correlation middleware remains outside the boundary so it observes the final503. No directory or authorization check is bypassed. Raw owned SQL GUID used for recalled-message GET is normalized to canonical D lowercase before the request.

Local initial-directory exception/stall plus retained portal reads20 PASS, additional initial-directory caller-abort control1 PASS,17 owned guards/changed format/diff PASS. Repaired independent original probes and actual SQL/portal run are required; no BFF/UI/full277 delivery claimed.


## Owned native ingress closure and source discovery checkpoint

Exact cd17377db0ba28667b77dec562746c961d2668ba Build37999783297 retry2/Governance37999783280: all8 PASS. Root inspected actual114055909438 and quality114059401788 after initial preSQL registry502 retry. All13 new native group phases PASS: exact mutable-column permitted writes/loss/escalation; concurrent100 one receipt unchanged bytes; DM/self/echo/foreign/HMAC/epoch/conflict; independent physical alias/original malformedUTF16; full graph outbox rollback/stable retry; append-only column escalation; observed source/grant/service/account/deletion/listener revocations; exact edit/recall/late-history/coverage/cursor/Core restart; no fake task/user; private override removed/default-off restored. Retained actual SQL/OIDC/worker/Redis expiry/current SID/private clear/realCore202 headers and body loss Chromium also PASS. This is ingress/runtime persistence evidence, not listener/spool/queue/private inbox/full277 delivery.

Remote afb2114a50ff26fc1d3bd3bff13cd321ec1db6ba privateGET repair independently APPROVED59controls+17guards; its own Build38001296429/Governance38001296406/actual114059737157 is active with six prerequisitesPASS. No new native GET result yet.

New bounded source discovery and logical message-head pages use explicit current ReaderGrants filtered before pagination, current member/full physical-role source proof, and an owned Serializable metadata-only read/commit. Source display labels are exact bounded UTF16, never routing authority. Message headers order by each logical message's latest committed sequence, while recall/edit precedence determines the displayed current revision; keyset pagination is a fresh UI listing, **not the frozen processing snapshot for #278**. Only metadata is returned; no content key resolution or decrypted source. Readability retains conservative exact current source-version/deletion checks; version-changed history requires explicit retention/repair policy and is not silently reinterpreted under another physical identity.

Local14 new metadata/filter-before-pagination/keyset/recall-edit/selector controls and96 current HTTP/reader/key/protection/retained ingress controls PASS, zero skipped;17 guards/Python compile/format/YAML/diff PASS. Native mandatory source and message listing oracle is encoded but has not executed. Independent frozen review, actual SQL release/range proof, BFF/UI, fenced listener/encrypted spool and broker recovery remain277 gates.


## Exact private GET result and retained browser failure

Remote afb2114a50ff26fc1d3bd3bff13cd321ec1db6ba Build38001296429/Governance38001296406: six prerequisites and Governance PASS. Actual114059737157 passed all13 ingress phases plus the new issued portal/current explicit grant read, exact protected Unicode, HMAC-only/foreign scope denials, grant removal/restoration, and Recall/null after late history. Root inspected private logs. The run is nevertheless terminal red: retained Chromium passed sign-in, member/admin lost-reply controls, scope, role restoration, logout/re-login, then failed owned-expiry-submit-receipt after202/no-store headers. Its precise body/JSON/ID failure was not observable from the old combined stage. New fixed stage identifiers distinguish those boundaries without printing private bytes or errors; this is diagnostic work, not a claimed product fix. Same-PR CI closure remains mandatory.

Catalog93cbbfb independent scoped approval:68 retained controls +5 own probes +17 guards PASS. Grant filtering before pagination, foreign-tenant/ungranted exclusion, malformed labels, physical aliases, broken receipts and SQL Server keyset translation were checked. Native catalog/range/BFF/UI/listener/spool/broker/full277 remain unapproved. Owned parent fixture also refreshes its legitimately issued owner token immediately before independent group reads; expiration has not been proven as the retained browser failure cause.


## Private inbox BFF checkpoint

Read-only `/api/local/group-sources`, scoped source message-head pages, and exact message detail now use the existing issued browser session/Core helper. Browser authority headers are ignored; company selection is bound to the issued SID. Canonical route IDs and single bounded page selectors are required. Strict decoded-duplicate/depth JSON, fatal UTF8, actual128KiB stream bounds, fixed10second cancellation, exact DTO fields, source/company scope, safe integer sequence/cursor ordering, original scalar text/opaque IDs, Recall/null semantics, and final issued-session release are enforced. Upstream errors/cookies/private headers are never relayed. Password development mode receives the same bounded parser; feature/API still default-off.

Local41 current route/session/stream/projection adversarial controls PASS, TypeScript noEmit PASS. Includes legitimate three-path reads/Unicode/trailing opaque spaces/FEFF, no authority header forwarding, final SID revoke/company change, delayed body logout/caller cancellation, malformed UTF8/duplicate keys/byte bounds, forged scopes/extra key fields, Recall and cursor order. No shipping inbox UI or actual browser/native BFF result yet; independent frozen review and full277 gates remain.


### BFF coordinator cancellation repair

Independent071 review reproduced caller cancellation during the third/final issued-SID await followed by private200. The BFF now races initial Core/shared coordinator work and final current-SID proof against its existing caller/deadline signal, removes cancellation listeners on completion, and checks cancellation after the final await before releasing JSON. These are read-only operations; a delayed coordinator completion cannot change the already-refused response. Local43 BFF controls PASS including owned initial/final await barriers and immediate cancellation/no private bytes; TypeScript noEmit PASS. Independent original-probe closure/native browser still required.


## Shipping private inbox UI checkpoint

The workspace now exposes Hộp thư nguồn to current signed-in readers independently of admin roles. It discovers granted sources, pages logical message metadata, reads current protected text on demand, shows Recall/null and coverage gaps, and makes no intake mutation/approval/send. Private data is bound to tenant/company/user/session generation, hidden synchronously during scope replacement, cleared before every fresh read or refusal, never persisted in browser storage, and discarded after superseding/unmount/cancel/final authority failure. A focused/visible panel refreshes current data; hidden panels clear private state and pending work. No decrypted snippets are included in indexes.

Local13 UI controls PASS and TypeScript noEmit PASS: grant source→metadata→private Unicode, remount, empty discovery/source, current Recall/gap,401/403/404/503 clearing, focus fresh-read/old-response discard, tenant/company/user/generation replacement, final authority, keyset/latest navigation, unmount abort; all requests read-only/no-store. React skill checklist: hooks remain unconditional; source/private state scoped and async work fenced/cleaned; lists keyed; buttons labelled/keyboard accessible; loading/status/error announced; direct imports; no HTML rendering or private browser persistence; dependent authority checks intentionally sequenced. This is local shipping-component evidence; actual issued-session SQL/BFF/Chromium proof and independent review remain required.


## Native catalog closure and inbox browser candidate

Exact6cd8d5773ca4a6b11bc313da138ddb2859b6ee6e Build38002804580/Governance38002804587/all8 PASS; actual114064637065/quality114067255299. Root inspected the mandatory source/head catalog and protected Unicode/currentgrant/Recall controls, all13 ingress phases, and retained full Chromium/Redis/realCore202 receipt-loss/currentSID/company-owner isolation gates. The prior AFB coarse receipt failure did not reproduce with separate body/JSON/ID diagnostics; no underlying cause has been proved or bypassed.

Independent BFF be9 scoped approval: original071 final-SID cancellation failure CLOSED;43 retained+7 own controls PASS, TypeScript/YAML/diff. Inbox UI review found missing mobile direct navigation; fixed independent mobile action plus flex-wrap and removed accidental pending-task-only duplicate. Local14 component/workspace controls PASS;7 Node browser boundary guards and17 Python guards PASS; syntax/compile/TypeScript PASS.

A new mandatory owned Chromium inbox candidate runs while the native proof's exclusive private Core key override is live, with a separate real Code/S256 session. It exercises shipping source→metadata→exact private Unicode, foreign selectors, SQL ReaderGrant revoke/no private UI and restore under sameSID, an actual private BFF response held across shipping logout, obsoleteSID401 and a fresh issued session read. It restores only the fixture grant/web mode and asserts unchanged full group snapshots/no fabricated portal graph. Guard precedes files/Docker/SQL/browser/network; no content/key/config/error dump, private stdin is bounded, and Core override cleanup remains nested finally. This candidate has not run; frozen review and exact CI/browser evidence remain required.


### Inbox layout bounds repair

Independent bca layout proof with the shipping component/Tailwind reproduced horizontal overflow from legitimate unbroken200-unit source labels and256-unit sender IDs at375 and1440 widths. Source list items/buttons and detail containers now use maximum-width/minimum-width containment; source labels and bidi sender IDs wrap without changing original scalar values. Mobile direct-reader navigation remains independent of task submission. Original independent mobile/layout probes must close before shipping UI approval.


Owned inbox browser candidate additionally opens the mobile navigation directly at375px after a separate fresh issued session, verifies viewport containment, and records two sanitized synthetic inbox screenshots in the existing CI artifact. No private configurations or browser traces are uploaded. This remains an unexecuted browser oracle until its exact CI runs.

### First native inbox execution and refusal diagnostics

Exact1a7c7cf4ea5f1f895cdfe40d0ea41b6959e5dcda Build38004179619/actual114069045285 is terminal red at the coarse `native-reader-grant-revocation` browser stage; quality114071849894 fails. Governance38004179640 and six prerequisites PASS. Root inspected all13 retained native group-ingress PASS markers. The owned browser reached real Code/S256, shipping source/metadata/private Unicode and foreign-scope controls; native grant revoke/clearing/restoration, logout, mobile and screenshots are not yet verified. The precise failing substep and product cause are unknown.

The oracle now separates fixed stages for SQL update/disabled-row proof, the refresh click, inbox-scoped refusal alert, private/catalog removal, private HTTP403/no-store, unchanged SID and filtered catalog HTTP200/no-store. It emits only fixed stage names and HTTP status; it never emits source contents, credentials, SQL text or exception bodies. This is diagnosis, not a claimed shipping fix. A new shipping workspace/current-session hook test proves source403 clears the body/catalog while an otherwise current member stays signed in, followed by empty catalog and restored-grant discovery. Local15 inbox/workspace controls,7 Node boundary guards, syntax and TypeScript PASS; native rerun remains mandatory.

## Native final-read race candidate

The owned SQL oracle holds only the final coverage-gap query, after the protected key await and final current authority/winner/receipt reads. It requires an observed runtime Serializable reader waiting on that owned gap lock, then an actual Recall INSERT blocked on a matching KEY RangeI-N against the reader's granted RangeS-S/RangeS-U resource, and a matching current ReaderGrant UPDATE blocked by the same reader. Database/HoBT/resource description, current statement and login are checked inside SQL; raw SQL statements and lock metadata are never printed. These predicates follow the [SQL Server range-lock contract](https://learn.microsoft.com/en-us/sql/relational-databases/sql-server-transaction-locking-and-row-versioning-guide?view=sql-server-ver17). No shipping lock hints are added to force a plan.

After releasing the gate, the original200 is allowed to linearize before the blocked Recall/revoke commits. The oracle then requires ReadCommitted/no open transaction on the observed reader session, fresh403, exact full-row grant restoration, Recall/null revision2, precisely one new revision/receipt/outbox and cursor+1, preserved full original message/gap/revision/receipt/outbox prefix bytes, and stable same-event replay with unchanged graph. It runs after the read-only browser snapshot, so the deliberate Recall cannot invalidate the earlier original-text positive.

Independent review reproduced a fixture cleanup P2: a failed release command skipped drain/restore/drop; Popen failure left the created gate. Setup and every cleanup attempt are now guarded independently, preserving the first failure while attempting stdin close, bounded process drain/kill, executor drain, exact owned grant restore and gate drop. Local18 guards PASS including10 inert helper fault cases; Python compile/diff PASS. This is a native candidate, not actual SQL evidence: independent closure and hosted execution are still required. Current610 Build38005660521/Governance38005660490/actual114073742719 remains owned; six prerequisites/Governance PASS, native grant diagnosis pending.

## Listener ownership transition contract

The account-scoped lease policy now defines Acquire/Renew/Stop with a server-owned30second lease, one process OwnerId and monotonically increasing retained epoch. A lost Acquire ACK returns the same still-live owned snapshot without extending its deadline. A different owner cannot acquire a live account; renew/stop require the exact current owner/epoch and live lease. Stop expires the retained row; same-owner/same-epoch stopped ACK recovery is unchanged. Exact expiry permits takeover with epoch+1 and rejects the former owner. Missing/foreign/malformed/clock-reversed/oversized lease state cannot be treated as vacant. Epoch and clock overflow fail closed.

Transitions expose fixed coverage reasons and the last confirmed heartbeat/start/stop time for the SQL integration to preserve interrupted coverage. This pure policy is not a service authenticator, SQL lock/commit, account listener, coverage recorder or provider activation. The caller must still resolve current qualified account/Ingest grants, serialize ownership in SQL, commit coverage atomically, and publish no ACK before commit. Those shipping integration steps remain #277 acceptance work.

Local24 focused contract controls and all486 Shared.Contracts tests PASS with no skips, including first/lost ACK, foreign/current/obsolete owner, exact-expiry takeover, renewal/stop/restart, retained epoch, unknown-coverage marker, current malformed state, command combinations/empty scopes, UTC clock and overflow. No real listener/acquire/renew endpoint has executed.

## Exact inbox browser closure and current range run

Exact61097496a9cb136e530f164b9b09a6ee274edd3e Build38005660521/Governance38005660490/all8 PASS; actual114073742719/quality114076863489. Root inspected all13 retained native ingress phases, issued Code/S256 source/catalog/private Unicode, actual SQL ReaderGrant revoke with private UI/catalog clearing and same-SID403/no-store, restored grant, a held actual BFF reply discarded across logout, obsolete SID401 and fresh SID restoration, and direct375px mobile navigation. Retained full SQL/OIDC/Redis/Core202 reply-loss/reload/two-company/two-owner gates passed. Root viewed all6 sanitized PNGs from artifact11651253546, including desktop and mobile inbox; no horizontal overflow observed. The first1a7 coarse grant failure did not reproduce; its underlying cause is unproven. No shipping product fix is inferred from diagnostic changes.

Independent range165 scoped approval closes both original cleanup failures:18 prior guards plus the bounded numeric-stage guard PASS;27 generated SQL statements parsed without SQL Server execution, and exact Recall graph links tested with adapted SQLite positive/8 negatives. Independent pure-policy710 approval:24 retained plus29 actual-C# scope/expiry/replay/overflow assertions PASS. Neither is native listener or SQL range evidence.

Remote71054612f2e9447596c1d0a1a88c7ccd706cc803 Build38007014365/Governance38007014343 now owns the mandatory range candidate. Inspect terminal native results and repair the same PR; do not infer the new range result from610.

## Separately signed listener authentication checkpoint

Listener Acquire/Renew/Stop bodies use a distinct fixed `aioffice-group-listener-v1` HMAC domain and strict bounded8192-byte UTF8/JSON capture. Existing event signatures retain `aioffice-group-ingest-v1`. A command derives account authority from current enrolled source/Ingest grant and qualified connector account; body OwnerId is only a process fence. It cannot supply tenant/company/account authority, create a portal user/task, or bypass live qualification. Initial and final effective SQL permissions, service credential epoch, source/account/grant versions, exact account ID, key reference, signing time and qualification remain required around the key await. Captured command bytes cannot mutate during that await; keys and captured bodies are zeroed.

Local127 focused listener/retained event/store/reader/HTTP controls PASS, no skips. The same-identity/same-version account replacement test is an EF InMemory authority-seal test with staged dependent relinking; it is not a native SQL operator rewrite/race proof. No listener store/lease write/API/DI/provider connection or durable coverage has been added by this authentication checkpoint. Independent review, SQL serialization/current final proof and atomic coverage/commit remain required.

## Exact native final-read closure

Exact71054612f2e9447596c1d0a1a88c7ccd706cc803 Build38007014365/Governance38007014343/all8 PASS; actual114078024682/quality114080654106. Root inspected the new mandatory final-read marker and retained native/browser phases. Actual SQL observed the post-key Serializable reader waiting at the owned final coverage barrier, matching Recall INSERT KEY RangeI-N versus its granted RangeS-S/RangeS-U and matching current ReaderGrant UPDATE waiting through read commit. Original200 linearized first; observed reader session returned ReadCommitted/no transaction. Fresh403, exact restored grant/Recall-null revision2, precisely one new revision/receipt/outbox and cursor+1, preserved original protected graph prefixes/gaps and stable replay all passed. This closes the read-release candidate, not the future listener/store/runtime.

Independent ac58 authentication scoped APPROVED:127 retained plus12 own actual-authenticator probes (139 PASS/0 skips),19 guards/YAML/diff. Review also reproduced the integration seam: the same signed Acquire still authenticates at31s within120s skew, and naive lease consumption extends ownership. The following durable consumer addresses this seam; authentication approval alone is not consumer approval. Remoteac58 Build38007947431/Governance38007947436/actual114080999519 remains owned until terminal.

## Durable listener lease and operation replay checkpoint

The verified listener now seals the HMAC nonce and SHA256 of the exact captured command body. The local SQL store locks service/credential-epoch/nonce before account, resolves current qualified source/Ingest/account authority, applies the retained epoch policy, and commits lease, account interruption marker and typed command receipt together. Same nonce/body reconciles the original still-valid ACK without extension or extra coverage; expired/stopped/superseded live ACKs refuse, while Stop reconciles only the exact retained stopped row. Conflicting reuse across operations or even another independently authorized account refuses. A fresh signed operation identity is required for a new transition. Initial/final authority, qualification, signing time and final lease expiry checks surround the SQL writes; no ACK precedes commit.

The additive `20261010000621_AddGroupListenerOwnership` migration adds `GroupAccountCoverageGaps` and `GroupListenerCommandReceipts`, restrictive tenant/company/account/service FKs, fixed typed/digest metadata, operator ownership and runtime SELECT/INSERT only. Evidence cannot be updated/deleted by runtime; destructive Down refuses. No raw command, source content or credential enters receipts. Account gaps remain explicit on source/private/head views; acquiring a lease does not prove provider connection or complete reconnect history. See the durable listener decision in ADR_GROUP_INTAKE.md. Existing tables/permissions are unchanged, and migrations precede the new runtime's mandatory permission proof.

Local all1064 Persistence tests PASS/0 skips.21 store tests include100 exact replay, original Acquire-at31s signature seam/no epoch advance, fresh nonce takeover, stop/restart/obsolete owner, original renewal deadline, new-context recovery, conflict/cross-account nonce, captured digest, current registry revoke, dirty context, SQL-like initial save failure, and both save-boundary delay/current-proof refusal.4 account-gap foreign tenant/company/account and legitimate positive controls cover private body and head catalog. Model/migration scope/owner/append-only/forward-only checks and design-only EF pending-model/idempotent generation PASS. InMemory provides no transaction rollback: late-fault tests prove no ACK and staged detachment, not native atomic persistence or concurrent locking. This checkpoint has no API/DI/provider listener; mandatory actual SQL runtime proof, spool/broker recovery and full277 still remain.

### Original ACK versus shortened current lease repair

Independent c87 review closed the signed Acquire31s seam but reproduced a current-state P2: shorten the still-valid retained lease from30 to10seconds with the same owner/epoch/heartbeat, then replay its original command at1second; the old30second ACK escaped. Reconciliation now requires current expiry at least the original receipt expiry. Later legitimate renewal remains allowed; a shortened, stopped, expired or superseded current lease refuses. A new actual-authenticator/store regression preserves both original receipt and shortened authoritative row while requiring denial. This repairs the local unexposed store candidate; independent original-probe closure and native execution remain required.

## Listener persistence review closure and bounded HTTP candidate

Independent745 scoped APPROVED:85 retained/own tests,0 skips;19 guards, YAML/diff,
SQL160 migration/current permission syntax, no pending EF model changes. Both the
original signed Acquire31s replay seam and shortened-current-lease P2 are CLOSED.
Review receipt6091578126 records the exact scope. No listener HTTP/native store,
provider operation or full277 acceptance follows from persistence review.

Exactac58 Build38007947431/Governance38007947436/all8 PASS;
actual114080999519/quality114083329396. Root inspected retained group ingestion,
strict matching read-release KEY range, issued inbox and full Chromium markers.
Exact745 Build38008927610/Governance38008927679/actual114084138463 remains owned;
six prerequisites/Governance PASS and actual pending at this checkpoint.

The local default-off `/internal/group-ingress/listener` uses the enabled+DB guard,
separately authenticated HMAC, canonical single headers/no query, strict JSON MIME
and actual8192-byte capture. Its linked10second deadline covers body/auth/store;
owned timeout returns503 and asks reconciliation of the same command. Original
caller cancellation cannot produce a successful ACK. Errors contain fixed text,
all responses are no-store, captured bodies are zeroed, and SQL commit precedes ACK.

Lease mutation is explicitly saved before staging account-gap and command-receipt
inserts. Both saves remain inside the same pinned owned SQL transaction. This
avoids relying on EF ordering of unrelated tables where reader revision locks,
ingress lease reads and listener account-gap writes could form a cycle. Such a
cycle is an inferred lock-order risk, not an observed native deadlock.

Local108 focused HTTP/auth/store/retained-event tests PASS,0 skips, including20 new
HTTP controls and2 explicit write-order controls. Native SQL nonce/account lock,
rollback, revocation, expiry, restart/isolation and provider/spool/broker proofs
remain mandatory before277 acceptance. No live connector or send was attempted.

### Request-body transport privacy repair

Independent86ff review reproduced request-body IOException escaping to the
Development exception page:500 contained a private error marker and omitted
no-store after the page cleared response headers. The shared bounded reader now
maps ordinary body I/O failure to a fixed503; coded BadHttpRequest status such as
actual overflow413 stays intact. Cancellation is checked before/after each read;
original caller cancellation propagates. The capture buffer is zeroed on all exits.
Four new owned HTTP controls cover both event/listener endpoints and faults before
or after partial capture, fixed no-store503, no authentication/writes/private text,
and zeroed capture. Local112 focused tests PASS,0 skips. Independent original
probe closure remains required; no native listener execution is inferred.

Exact745 Build38008927610/Governance38008927679/all8 PASS;
actual114084138463/quality114086839468. Root inspected retained native protected
graph/concurrent100, all queued ingress revokes, strict read-release KEY range,
issued inbox and retained Chromium markers. The14-table migrations/current runtime
permission expansion executed with retained ingestion; listener commands were not
exposed in745 and therefore were not exercised by this run.

## Mandatory native listener candidate

The owned full-stack gate now invokes a separate listener proof before removing
the exclusive random-key Core override. A new disposable account/source receives
explicit operator Ingest/Reader grants; the retained manual ingress lease and
protected source graph are preserved. No private source content key is required
for listener commands or the empty metadata head's startup-gap view.

The candidate exercises the real listener HMAC HTTP route/runtime SQL: first
server-scoped lease/gap/typed digest receipt,100 concurrent exact nonce replays
with unchanged complete original table bytes, same-nonce conflict, foreign owner,
old epoch/wrong event domain/current unsafe append-only permission refusals.
A targeted receipt CHECK fault after Stop lease mutation must503 and roll back
all3 complete table fingerprints, then same-command restored retry succeeds.
Core restart must recover the exact Stop ACK and retain the next ownership epoch.

Five queued source/grant/credential/account/deletion revocations require an actual
matching held/waiting application lock and runtime Serializable request,403/no
SQL effects, exact restored registry bytes and successful fresh renewal. Captured
server deadlines are crossed in real time: original Acquire ACK refuses despite
later current renewal; an expired original signed nonce still within HMAC skew
cannot reacquire or extend; fresh owner/nonce takes epoch+1 and records uncertainty.
Sleeping runtime sessions must be ReadCommitted/no transaction. The separate
source remains free of message/task/outbox artifacts; retained source/portal graph
source fingerprints and portal Users/Tasks/Dispatches/Checkpoints cardinalities
are checked by the parent. Portal full-row equality is not asserted.

This is an unexecuted native candidate. Local22 inert guard/vector/cleanup tests,
Python syntax/YAML/diff PASS do not prove SQL execution, rollback/locking or full277.
Independent proof review and exact hosted CI remain mandatory. Cleanup attempts
release/drain/kill/shutdown/registry restoration/gate drop while preserving the
original failure; only fixed diagnostics leave the owned process. No live account,
ERP access, provider send or production activation is involved.

### Native fixture setup-reply-loss cleanup repair

Independent68b review reproduced a fixture P2: CREATE/INSERT occurred before the
cleanup guard, so applied CREATE with a lost SQL reply skipped release/restore/drop.
The temporary permission GRANT and receipt CHECK setup had the same gap. Each
setup is now inside a guarded unit; missing/partially created objects are tolerated
by conditional cleanup and the original error is preserved across cleanup faults.
The temporary login-column GRANT restores its originally absent direct override;
the role's original explicit DENY remains unchanged. This distinction follows the
actual migration's role grantee rather than inventing a login-owned DENY.

Local23 inert tests PASS, including applied CREATE with lost reply and temporary
setup/body/restore/both failures. Existing10 resource-cleanup fault cases remain.
These are fixture orchestration controls, not SQL rollback/locking execution.
Independent original-probe closure and hosted native run remain mandatory.

API54e scoped APPROVED, receipt6091749059: original86ff private500/missing no-store
P2 CLOSED using unchanged actual Program/TestServer input.114 independent tests0skip
and19 guards PASS, including two noncooperative held-stream caller abort controls.
Remote54e Build38010187105/Governance38010185277/actual114088140386 is owned;
six prerequisites/Governance PASS, actual pending. No native listener commands have
executed at this checkpoint. Full277/233 remain active.

### Effective unsafe-column fixture precondition

Independent8b9 review identified a permission-oracle inference: a direct login
GRANT while retaining the same column's role DENY may leave effective UPDATE0.
The candidate now requires observed runtime HAS_PERMS_BY_NAME column UPDATE1
before testing403. It temporarily changes the exact role column DENY to GRANT,
then restores that original DENY under guarded cleanup; original role stateD,
absent direct login override and effective UPDATE0 are checked before/after.
The permission algorithm/column-table exception is documented by Microsoft's
[permission reference](https://learn.microsoft.com/en-us/sql/relational-databases/security/permissions-database-engine?view=sql-server-ver17)
and [DENY reference](https://learn.microsoft.com/en-us/sql/t-sql/statements/deny-transact-sql?view=sql-server-ver17).
This strengthens an unexecuted fixture; no unsafe effective rights or shipping
failure has yet been observed on native SQL for this new table.

### Native proof review and predecessor CI closure

Native fixture54b scoped APPROVED, receipt6091892127:23 retained inert guards,
19 independent cleanup/oracle controls and23 SQL160 syntax checks PASS. Setup
reply-loss cleanup and effective-column oracle issues CLOSED. This approval does
not assert native execution. Pushed54b Build38011253824/Gov38011253772, actual
job114091542726 is owned through terminal.

Predecessor54e exact all8 PASS: Build38010187105/Gov38010185277,
actual114088140386/quality114090919481. Root inspected all retained native
group/read-release/issued-inbox/full Chromium markers. This predecessor had no
native listener fixture; no listener runtime acceptance follows from its green CI.

### Prefiltered encrypted spool foundation

`GroupConnectorSpoolAdmission` is a mechanical filter over trusted backend
enrollment and lease snapshots, not authentication of customer-supplied DTOs.
DM/self/report echo is refused before content validation/serialization/keys/storage;
exact enrolled external identity, current scoped Ingest authority, owned live lease
and artifact/account qualification are required. Optional edit/recall requires
fresh capability evidence. No HTTP/client/provider path is enabled by these types.

`GroupSpoolContentProtector` uses dedicated AES256-GCM/domain-separated AAD binding
tenant/company/source/account/service/credential epoch/source and grant versions/
deletion generation/external and event hashes/captured body hash/time/key id.
Strict bounded UTF8/JSON and ciphertext integrity preserve exact Unicode; released
cleartext buffers are zeroed. Recovery requires fresh matching enrollment and
current qualified lease before decryption, then reapplies full admission. Only
transport owner/epoch changes after restart; logical event metadata/text stays
unchanged and SQL commit authentication/authorization is still mandatory.

This checkpoint does not implement filesystem durability/capacity/ACK deletion,
provider session/listener, key provisioning, connector HMAC client, broker dispatch
or worker. Focused unit controls cover prefilter/revocation/tamper/zeroing/restart;
no live qualification or complete-history claim is made. Full277/278/279/233 remain.

Independent3b4 review found a known-fixture promotion gap: changing qualification
environment to ControlledAccount could admit a synthetic/owned-fixture artifact
under Live. Admission now mirrors the authenticator's explicit artifact fence:
Live refuses either known fixture marker; the owned synthetic policy requires
both markers and Synthetic environment.44 focused spool controls and36 retained
authenticator controls PASS80/0skip, including a distinct controlled-artifact
positive and synthetic-policy refusal. Original independent closure is required.

The same review reproduced optional-event revocation after decryption: an old
Edit/Recall spool was decrypted before its currently removed capability was
rechecked. EventKind is now part of authenticated context/AAD, checked for valid
enum and exact agreement with decrypted payload. Current event-specific fresh
capability is required before decryption, then full post-decrypt admission remains.
Missing/stale/future Edit/Recall controls use an invalid key to prove refusal before
the private operation; legitimate Edit/Recall stays positive.50 spool plus36
authenticator controls PASS86/0skip. Independent original-probe closure remains.

### Native permission-catalog baseline correction

Exact54b Build38011253824 actual114091542726 is terminal red after first listener
ACK/100 concurrent replay PASS: fixture required a column catalog DENY row that
was absent. SQL Server omits column permission rows equal to object permissions,
as documented in its [permission catalog reference](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-database-permissions-transact-sql).
The fixture now requires object UPDATE DENYD, captures column absent-or-D and
requires exact catalog restoration. Effective runtime UPDATE0 before/after and
observed UPDATE1 before API403 remain mandatory. No migration/runtime grant was
changed. This corrects a fixture assumption; native retry/full acceptance remains.

### Bounded encrypted file spool candidate

`GroupConnectorFileSpool` stores only protected content and fixed scoped/hash
metadata on a host-owned private volume. Tenant/company/account/service determine
the private directory, event hashes determine filenames; one exclusive file owner
serializes the account/service. Paths through reparse points, unexpected files,
foreign metadata, malformed/truncated/oversized records and changed same-event
captures refuse with fixed storage errors. No source text/key/opaque identity is
stored in filenames or record headers. AES authentication remains required on load.

Default bounds are1024 records/64MiB, with hard ceilings4096/256MiB. Capacity
refuses before writing a new stage, preserving all backlog; no age/capacity eviction
or automatic erasure is implemented. WriteThrough/Flush(true) precedes atomic
same-directory promotion. Complete staging is recovered at reopen; torn or
conflicting staging is retained and blocks recovery with coverage uncertainty.
These operations support process restart. Hard power-loss durability also depends
on the host volume/filesystem and has not been qualified by these controls.

An internal acknowledgement operation removes only the exact retained reference
after the trusted connector transport binds an authenticated committed SQL reply
to that request. A receipt DTO alone is not authentication; no public deletion API
exists. That transport is not wired yet. Linux directories/files are700/600;
Windows requires the host provisioned private root/ACL. No provider session,
production volume/Windows installer or live connector has been activated.

Local19 actual owned-temp filesystem controls plus50 admission/envelope controls
PASS69/0skip: exact encrypted Unicode close/reopen, concurrent100 one retained
file/bytes, exclusive owner, scope isolation, count/byte pressure without eviction,
complete stage recovery, torn/unknown/name/foreign/magic corruption preservation,
same-event conflicts, bound roots/limits and exact internal ACK removal. These
are file lifecycle controls, not native SQL ACK/client/broker/worker acceptance.
Independent frozen review and the shipping recovery transport remain required.

### Native listener retry closure and file-spool review

Exact `00337c2514748d16d8438998f8b19b85c9ab0058`: Build38012373146/Governance38012373142/all8 PASS, actual114095008504/quality114097930440. Root inspected the eight new listener PASS markers and retained ingress/read-range/inbox/full Chromium proof. The runtime gate executed concurrent100 same-nonce full-byte stability, effective column rights0->1 before403->exact restored0, actual second-save transaction rollback and Core restart, five observed Serializable account-lock registry revocations, original ACK expiry under later renewal, expired signed nonce refusal, fresh takeover uncertainty and clean pooled isolation. The prior54b catalog assertion failure is closed; transport/file spool/provider/broker acceptance is still separate.

File spool `20cd591088430635b9bb46635f5441d962eca9ac` independently scoped APPROVED:19 retained+9 independent owned-temp filesystem controls,28 PASS/0skip, YAML/diff PASS. No file-spool blocker found. This approval does not qualify Windows ACL provisioning, forced process death/power-loss, trusted commit-response deletion or live operation.

### Scoped signing/ACK transport candidate

`GroupConnectorTransportClient` pins a host-configured origin and scoped service/
credential-epoch secret reference; no request/model secret selector, portal JWT,
user impersonation, cookie, proxy or redirect is used. Live origins require HTTPS;
HTTP loopback is limited to the explicit owned synthetic policy. Service HMAC
uses the existing independent event/listener domains. A disposable prepared
request retains one exact body/nonce/signing time for a deliberate identical retry.
Key/captured body buffers are zeroed; no body/error/key diagnostics are logged.

Event admission retains its actual qualified lease snapshot. Preparation checks
that lease/profile before and after asynchronous signing-key resolution; the
backend still authenticates fresh authority and commits SQL before its response.
Successful responses require strict bounded UTF8/JSON, unique decoded property
names, exact required fields, no-store and exact source/account/owner/epoch/time
semantics. Non-success never reads upstream diagnostic content. Each operation
owns a10second deadline plus caller cancellation; noncooperative late headers/
streams are disposed and aliased read targets are zeroed again on late completion.
Unavailable/canceled transport supplies no commit ACK and must retain the spool.

Local42 transport controls plus50 spool/19 file controls PASS111/0skip, including
independent golden HMAC vectors, exact retry, profile/lease refusal before and
after key resolution, foreign/malformed/duplicate/oversized replies, status-only
denials, strict listener ownership, caller/deadline held headers and two held-body
late success/fault controls. These are owned injected HTTP dependencies; no real
connector transport/provider/live qualification has executed. No DI/worker or
filesystem ACK integration is activated by this client; fresh trusted enrollment,
automatic recovery/broker delivery and full277 remain mandatory. Client hosts
also need UTC clock synchronization for conservative short-lease checks.

### Exact retained replay and transport review repairs

Combined transport/replay frozen `9d498b4574e92aa65d56d9c5c666a1286407326d` scoped APPROVED, public receipt6092460850:92 independent controls PASS/0skip,76 retained+16 own, clean compile/YAML/diff. Unchanged Windows process-env alias probe and all six earlier probes are CLOSED. This approves the default-off library scope, not native execution or full277. Pushed9d Build38015307994/Gov38015307999/actual114104147229 is owned through terminal CI; new native fixture is excluded from9d.

Independent frozen `71eec0e` review closed all six original transport denial probes;90 retained/own controls passed. One new Windows env-reference alias P2 was reproduced: resource names differing only by case resolve the same process variable but record equality treated them as different signing/spool references. The purpose-separation guard now compares provider identity without case, env resources conservatively without case on every host, and other provider resources ordinally. A distinct spool reference remains positive. Local145 retained transport/replay/envelope/file controls PASS; unchanged original alias probe still requires closure.

Pushed file checkpoint `20cd591` exact all8 PASS: Build38013792478/Gov38013792484/actual114099512222/quality114101762910. Root inspected all8 listener markers and retained group/read-range/issued inbox/full Chromium evidence. This CI did not contain the local .NET transport/replay/native harness candidate, so it supplies no native client/spool acceptance.

### Native .NET spool/client fixture candidate

The separate `GroupIntake.RuntimeProof` executable and `smoke-group-spool.py` exercise shipping client/spool libraries against the real owned Core/SQL stack. They are not in application images. Python and executable guards run before resources and constrain the fixture to GitHub CI, the exact ephemeral root, loopback API and synthetic enrollment/random owned keys. No customer settings or provider is accessed; private scratch data and exceptions are not emitted.

The candidate observes Core SQL200 in a loopback proxy, holds all ACK bytes, kills only the owned .NET child after commit, then verifies one encrypted retained file/one graph effect. It checks full six-table durable bytes and ciphertext through Core restart, waits the actual original30second lease expiry, acquires fresh owner/epoch2 with explicit expiry uncertainty, reconciles the original typed commit ACK and removes the exact file without a second effect. A second captured event receives fresh backend403 under SQL grant revocation; exact restore enables backlog/cursor2, with pooled isolation clean. The parent retains original source full fingerprints and portal four-table cardinalities. Cleanup is scoped and preserves the first failure.

Local7 inert .NET guard tests and27 Python guard tests/syntax pass; executable build is clean, direct unowned entry refuses before config/network, and changed-file format/workflow YAML/diff pass. The three new native PASS markers remain **unexecuted** until frozen review and hosted actual-stack CI. This candidate supplies no current backend enrollment-fetch/provider/DI/recovery-poller/broker/full277 acceptance.

Independent frozen `bbec72d` review reproduced missing listener qualification before/after signing-key resolution, future event/listener commit timestamps and impossible Renew coverage accepted as success. Initial Acquire now uses the shared current scoped receive qualification without fabricating a lease, before and after the key await. Both typed replies require commit time no later than host UTC; listener coverage matches the backend reconciliation invariant. Controlled Live preparation remains positive. Original independent probes still require closure before approval.

`GroupConnectorSpoolTransport.ReplayAsync` joins one original file reference to its signed request and authenticated typed commit response, then deletes that exact retained capture. Current source/grant/deletion/service epoch, the actual client's qualification policy and qualified lease are required before file load, key resolution and private decryption, and rechecked after key resolution. The dedicated key reference is fixed host configuration scoped to account/service/key ID; a file cannot choose a secret reference or reuse the signing reference. Restart changes only transport owner/epoch, preserving event/text. A single10second replay deadline spans key/sign/send, together with caller cancellation. Unknown commit state retains the backlog; there is no externally supplied receipt parameter.

Local50 transport+25 replay+50 envelope+19 file controls PASS144/0skip, clean focused build: actual encrypted files persist through injected committed-reply loss/close-reopen/new owner, original commit reconciliation, all denied or unknown response cases, canceled/noncooperative key and HTTP dependencies, current scope/version/deletion/lease/optional qualification refusal before keys, tampered cipher refusal and protection of a newer recaptured file against an older in-flight ACK. The stateful HTTP handler proves one **simulated** commit effect; it is not real SQL or forced process death. Fresh backend enrollment/lease retrieval, real .NET client/spool/SQL restart evidence, provider qualification/Windows ACL/volume provisioning, recovery polling and reference-only broker/worker delivery remain mandatory. No connector is activated by this library unit.

### Pending exact-head native and browser CI closure

Frozen native588 independent review passed7 .NET guards,26 Python guards,20 SQL160 syntax statements and AST/YAML/diff. One cleanup P2 was reproduced: a thread-constructor error after socket allocation before try skipped socket cleanup. Allocation now occurs inside the guarded try; all allocated resources are conditionally cleaned while preserving the first error.27 retained Python guards cover six inert partial-startup faults and a later cleanup failure. Original independent closure is required before pushing; native execution remains unverified.

Approved library9d CI terminated with all six prerequisite gates and governance PASS, but actual114104147229 and quality114106277827 RED at unchanged Chromium submission-real-core202-body-replay-response. Root inspected retained native ingress/listener and committed202 header-loss replay PASS. The body-replay cause remains unproven. A diagnostic-only fixture change distinguishes click/response/202 body and fixed refusal status categories without private content, retrying, accepting non202 or weakening exact receipt/effect assertions. Own the next approved checkpoint through actual terminal CI and repair same PR.

### First native run and owned source-key repair

Frozen823 native fixture/cleanup/browser diagnostic scope was independently APPROVED6092680140. Exact Build38016765431/Gov38016765424 has six prerequisites/governance PASS, but actual114108652763/quality114110948673 RED before observed SQL commit at the held native reply boundary. Retained ingress/read/listener markers PASS; no native PASS marker or current Chromium execution. The old native upstream status was not captured.

Root found that the separate synthetic source had no entry in Core's fixed source-content-key configuration. The parent owned override now adds row1 with a separately generated random source-content key, preserves row0, keeps private0600 permissions, and recreates only its owned Core before native Acquire. No production fallback/key/grant behavior changes. An explicit enrollment callback is required before resources.29 Python guards include inert invalid/duplicate source enrollment refusal, original configuration preservation and exact owned command/private-permission checks. Proxy diagnostics emit only fixed phase/403/503/other labels; forwarding disables proxies/redirects and does not read error bodies. Exact commit ACK, lost-reply/process-death, full6 preservation and restore oracles are unchanged. Independent repair review and hosted execution remain mandatory.

### Current backend enrollment candidate

The separately authenticated metadata endpoint/client now derives current
source/account/Ingest authority and receive qualification from final backend
rows under the existing owned Serializable transaction. Declared source scope
must match before key resolution; final authority/qualification is checked after
the key await. The response is metadata only, no credential reference or private
source text. It neither creates a lease nor manufactures a portal identity.
An explicit wire DTO avoids deserializing the qualification implementation class.

The pinned host client signs the separate enrollment domain, validates strict
bounded no-store replies, exact scope/ordinal external identity/current service
epoch and qualified profile, and rejects future or older-than10-second metadata.
`ReplayWithCurrentAuthorityAsync` fetches that metadata and renews its actual owned
lease before loading a retained capture/resolving its dedicated key. Source/grant/
deletion/version mismatches fail before private load. Metadata age is checked
again after the key await and before deletion, within one10-second caller-linked
deadline. Final backend SQL authority remains required; snapshots do not promise
instantaneous revocation propagation during an in-flight request.

Local240 focused auth/client/replay/Core HTTP/file tests plus50 retained envelope
controls and2 additional enrollment body-I/O controls yield292 unique PASS/0skip,
clean compilation/changed-file formatting. Tests include an independently
assembled enrollment HMAC, real endpoint registration/no-source-key bootstrap,
prekey registry/scope denial, postkey revocation/final observation capture,
cross-domain misuse, unknown/duplicate/invalid UTF8 transport, current backend
grant denial/restoration, controlled Live receive and exact10-second age positive,
metadata/renewal failure preserving bytes/no spool key, after-key stale/backward
clock refusal and noncooperative outer deadline. These use owned SQLite/HTTP
fixtures or injected transport; fresh enrollment/native SQL execution and frozen
security review remain pending. This unit is excluded from823 and its native
source-key repair. No provider/DI recovery loop/broker/full277 acceptance.

### Reviewed enrollment and native current-authority extension

Frozen `f45984358ee0fc69c25ad2997106d825ccf564c5` metadata/API/recovery
unit is independently scoped APPROVED, receipt6093046005. The reviewer ran310
retained controls plus16 own boundary probes,0 skipped: maximum escaped identity/
display/all13 observations fit8KiB, observations are immutable copies, malformed
or oversized metadata is refused, metadata aging after Renew prevents private
load/key resolution, and age/cancellation after event reply preserves the exact
capture. This approval covers default-off library/HTTP security; actual native
SQL metadata/provider/DI recovery loop/broker/full277 remain outstanding.

The follow-on test-only native executable fetches current enrollment through the
shipping client instead of constructing authority from fixture configuration.
Initial Acquire uses that result; capture fetches current enrollment and saves
the actual Renew receipt. Operational replay fetches current metadata and renews
before loading/decrypting. A counted environment resolver proves actual backend
grant403 occurs before spool-key resolution, with unchanged encrypted bytes and
full six-table SQL fingerprints; exact restored grant permits the original item.

The owned loss proxy forwards only fixed enrollment/listener/event paths and
canonical signed headers. Metadata and listener responses forward bounded actual
Core200/no-store JSON bytes unchanged. Only an observed real event commit holds
all client ACK bytes. The parent requires two actual metadata and two actual
Renew forwards before forced child death, validates the last actual scoped Renew
response observed in the proxy and waits its expiry before acquiring a different
owner/epoch2. The child's saved lease is only the first Renew; operational replay
issues another Renew. Independent review reproduced the earlier-expiry P2, now
repaired by using the last actual response and retaining the earlier receipt as
an ordering bound. Original independent closure remains required. Existing
exact original ACK, full6/restart/cipher preservation, one-effect and clean pooled
isolation oracles remain. No fixture response fabricates backend authority.

Local32 Python guard tests include actual-handler inert byte forwarding versus
held event ACK, pre-upstream path/size refusal and exact scoped lease/owner/epoch/
duration oracles. Seven .NET owned guard tests, clean executable Release build
and changed-file formatting pass. These controls do not execute native SQL or
forced process death; frozen extension review and exact hosted proof are pending.

### fd4 native execution and retained browser failure

Remote `fd4f268619372b4dbd4c317dbf02d29a83519b5a` Build38017961934 /
Gov38017961861 finished with six prerequisite gates/governance PASS and
actual114112369937/quality114114935346 RED. Root inspected all three new actual
.NET spool/SQL markers PASS:100 encrypted captures/forced child death after
observed commit, full6/cipher preservation through Core restart/fresh epoch2/
original ACK reconciliation, and actual SQL grant403 byte preservation/restored
cursor2/clean pooled isolation. All retained ingress/read/listener markers PASS.
This executed the earlier mechanically configured native enrollment, not the
follow-on metadata-fetch extension.

Both retained actual Chromium committed202 header-loss and body-loss replay
controls PASS. The earlier9d body-response cause remains unproven. Later Chromium
fails with an unhandled15-second response waiter in the submission company-switch
helper invoked when restoring the original company after held private intent.
A waiter could reject while selectOption was pending before the parent awaited
it, bypassing fixed-stage handling and owned restoration. The fixture now observes
both waiter rejections immediately and still awaits the original promises;
missing auth request/callback, non-CodeS256, non303 or wrong current company still
fails. Fixed select/auth-request/callback/workspace/current-session stages identify
the actual remaining boundary. Original timeout/callback requirements are kept;
the underlying absent callback cause has not yet been established.

Nine local Node guard tests PASS, including actual extracted helper rejection
ownership while selection is pending and retained CodeS256/303/current-company
positive and negative controls. Frozen delta review and renewed exact native/
Chromium closure remain mandatory. No all8/merge/full277 approval is claimed.

### d862 current metadata native execution and next browser boundary

Exact `d86223e47dbf391d635e273efa91055bf7037c65`, scoped review6093126875,
Build38019377330/Gov38019377336 has six prerequisites/governance PASS and
actual114116728873/quality114119061264 RED in later Chromium. Root inspected all
three current native markers PASS: actual SQL enrollment and two actual Renew
responses before spool/process death, last-Renew expiry/fresh owner2/original
exact ACK/full6/cipher preservation through Core restart, and actual revoked
grant403 before any spool-key resolution with exact restore/cursor2/clean pooled
isolation. All retained native group/read/listener controls and both Chromium
Core202 header/body-loss retries PASS. This establishes the owned native current
metadata path; it does not qualify a live provider or activate DI/broker.

Current Chromium fails at the broad historical-owner-read/current-source-denial
stage before later company-switch acceptance. Its precise cause is unproven.
The same-PR diagnostic delta now separates disabled-source SQL, historical owner
read/status/body, denied submit/click/response/required403/retry visibility/full
unchanged graph, and restored submit/required202/exact receipt/one completed
worker graph. Waiters observe any response at the same exact POST path and still
require original403/202 statuses; early rejection is owned. Original20-second
waits, source restoration in finally, exact question/fingerprint/graph and
CodeS256/owner isolation checks are retained. Only fixed status/stage labels are
emitted, no private response or dependency diagnostics. Retained9 Node guards,
syntax/AST/YAML/diff and frozen scoped delta review are required before push;
renewed actual full Chromium remains mandatory.

## Reference-only durable worker inbox foundation

The new strict `GroupIngressDispatchReference` names only version, scoped source,
original outbox event/message/revision and committed sequence. Maximum2048 bytes,
fatal UTF8, decoded duplicate-property rejection, required exact JSON fields,
canonical broker message ID and a separate fixed message type reject authority,
portal identity, destinations, keys and source text in broker hints.

The worker binding is trusted host configuration. The SQL inbox store resolves
current active company, exact original physical source/account, enabled service
and credential epoch, and a separate current Extract grant under a Serializable
source lock. Ingest and Notify grants do not authorize delivery. It checks the
exact existing outbox/revision/original ingress receipt and committed cursor,
rechecks authority and effective permissions around save/commit, and returns only
an exact original reference and durable receipt time. Metadata projections do not
materialize encrypted source or resolve keys. No portal user/task or batch cursor
is created. A repeated delivery still requires current authority before returning
the original inbox receipt.

Publisher AvailableAt is a retry reservation. A broker hint can arrive before
that reservation expires or producer confirmation is saved; the original revision
must still be committed in UTC at or before the current clock. This is covered by
an explicit positive reservation test and retained future-revision denial.

Additive migration `20261010031308_AddGroupIngressInbox` has scoped Restrict foreign
keys, unique source committed sequence, positive audited versions and UTC time.
The operator owns the table; runtime SELECT/INSERT is allowed and every column's
UPDATE plus DELETE/ALTER/TAKEOWNERSHIP is denied. The runtime verifier now includes
all15 group tables. Down refuses destructive rollback in favor of forward repair.

Local212 focused tests PASS0skip (143 Persistence/69 Contracts), including100
exact duplicate receipts,13 current Extract denials, malformed SQL graph controls,
foreign host/cancellation before database, strict broker parsing and model rights.
Clean Persistence build0warnings0errors and EF no pending model changes PASS;
migration SQL was generated using an inert local design-time configuration without
opening a SQL connection. This foundation remains unreviewed until frozen review;
no actual SQL concurrency/rollback or RabbitMQ/DI/worker delivery is claimed.

Pushed browser diagnostic predecessor `b62e19b2ab0c379132b841c6e2625e4da722d86c`
has scoped review6093317472, Build38020840049/Gov38020840088 in progress at this
checkpoint. Its required403/202, exact operation/fingerprint/graphs and source
restoration remain unchanged. Terminal results stay owned on PR283. Full277,
automatic SQL brain278, internal IT reporting279 and full233 remain active.

## Reference publication and consumer boundary candidate

Foundation21d has independent scoped approval6093416207:241 local tests PASS0skip
including29 own controls and nine SQL160 parses/model/YAML/diff. This remains
foundation evidence; no concurrent native SQL or RabbitMQ acceptance was claimed.

The source-locked outbox dispatcher commits a five-second retry reservation before
external publication. A bounded ten-second publish uses the original immutable
EventId/reference. Broker confirmation is recorded separately and is not worker
acceptance. Rows remain eligible until SQL has the durable inbox receipt. Lost
confirms/process interruption preserve backlog; delayed confirmations cannot replace
a newer reservation. Fresh current host Extract authority is checked around the
SQL reservation, before publication and around confirmation save/commit. A revocation
can occur after that pre-publication check; references contain no private content
and the consumer independently enforces current authority before SQL receipt/ACK.

RabbitMQ group references use a separate deterministic tenant/company/service
queue, persistent mandatory messages and publisher confirms. The inbound body cap
is2048 bytes before handler resources; JSON/type/ID/host checks precede SQL scope
creation. Consumer callbacks own their original channel, with explicit bounded
reconnection rather than racing library recovery. ACK follows only an exact durable
SQL receipt and current caller fence. Malformed/refused deliveries are NACKed without
an immediate requeue loop; the SQL producer retries retained references after the
durable delay. Shutdown leaves unsettled deliveries to owned channel closure.
Fixed failure logs exclude exception bodies, payloads, keys and connection strings.

Local31 controls PASS0skip:13 outbox controls cover lost confirm/fresh dispatcher,
consumer commit before lost confirm, grant/epoch/source denial, revoke/restore,
delayed old confirm, uncommitted graph, foreign/canceled-before-DB and a genuinely
noncooperative ten-second publisher.18 boundary controls cover held receipt completion,
exact duplicate receipt, strict pre-handler broker refusals, wrong/future/nonUTC
receipt, cancellation after commit and fixed queue/transport bounds. Clean Worker
build0warnings0errors and changed-file whitespace PASS. These are inert local
controls; no DI registration, real broker, concurrent SQL or live connector is
proved. Frozen scoped review and mandatory owned native acceptance remain.

### b62 native success and recurring browser body boundary

Exactb62 Build38020840049/Governance38020840088 is terminal: six prerequisites and
Governance PASS; actual114121265377/quality114123892704 RED. Root inspected ALL3
current metadata/Renew/native spool markers and retained group/listener/read proof
PASS. Core202 header-loss Chromium retry PASS. The body-loss replay received actual
202 headers, then failed at `submission-real-core202-body-replay-202-body`; its
precise cause remains unproven. Historical-source substages and later company
switch were not re-exercised on this run. No shipping cause/fix is inferred.

The required receipt diagnostic now demands bounded response completion with no
stream error, then an actual nonempty body at most4096 bytes, fatal UTF8 and JSON.
Each refusal gets a fixed stage; known browser protocol resource-unavailability
is classified without exposing exception text/URLs/payloads. Original required202,
exact receipt/company/operation/fingerprint/IDs/time and full unchanged graph,
source restoration and no additional retry remain.10 retained/current Node guards
PASS including extracted actual helper controls for stream failures/deadlines,
body failures/protocol/empty/overflow/UTF8/JSON and positive exact receipt. Native
classification remains mandatory.

Independent a4 publication review reproduced an EF identity-map P2: an unchanged
pretracked outbox attempt0 can overwrite a separate context's committed attempt5
with1. Source SQL locking does not refresh that tracked instance. Both reservation
and confirmation now select fresh AsNoTracking under the owned source lock; only
matching unchanged prior outbox entries are detached, then the fresh current row
is attached for mutation. Unrelated tracked entities remain. Two separate-context
regressions cover reservation increment6 and confirmation preserving a newer
attempt7/retry/confirmation. The repaired frozen snapshot still requires independent
review, followed by actual native SQL/broker proof; InMemory is not rollback or
concurrency acceptance. Full277/278/279/233 remain active.

### Publication review repair candidate

Frozen a4 independent review withheld approval for three reproduced P2 categories.
In addition to stale tracked reservation/confirmation, consumer ACK/NACK used the
host stopping token instead of the delivery's ten-second deadline, and broker
cancellation left an open-channel attempt waiting forever.31 retained and five
additional independent controls passed; no additional domain/privacy finding.

ACK and NACK now use the delivery deadline and bounded WaitAsync even for a
noncooperative settlement. Late faults are observed without private output; own
expiry retires only the original channel/connection attempt. A durable SQL receipt
or unreceived SQL backlog is preserved. The actual RabbitMQ.Client7.2.2
UnregisteredAsync event, raised by server HandleBasicCancelAsync, completes that
owned attempt for bounded reconnect. Channel/connection shutdown and callback
failures also remain owned. Callbacks never settle on a replacement channel.

Three additional controls invoke the actual private shipping receive method with
the real inert SQL inbox and deliberately stalled ACK/NACK, and invoke actual SDK
consume-OK/server-cancel with otherwise open inert resources. They require the
ten-second token/bounded retirement, one original-channel settlement attempt,
retained durable inbox/backlog and wakeup on cancellation. The newer confirmation
regression covers both null/unknown and confirmed newer attempts. These are local
inert receiver/SDK controls, not real network or concurrent SQL evidence. Renewed
frozen review and mandatory owned native SQL/RabbitMQ restart proof remain.

### Default-off host registration candidate

e341 publication repairs have independent scoped APPROVED6093549235:47.NET
controls PASS0skip and10 retained Node guards plus15 own diagnostic controls PASS.
The original three P2 categories are independently closed. Build38022812535 and
Governance38022812541 are the renewed exact-head runs. .NET job114127247828 failed
before build/test at migration `AddGroupIngressInbox` CHARSET: generated UTF8 BOM
violated repository utf-8 policy. The local repair removes only that BOM. Actual
stack114127247688 remains owned through terminal, including the strict Chromium
completed-stream/body diagnostic; its underlying failure is not inferred.

Core now separately registers reference publication; Agent.Worker registers the
group reference consumer. With the switch absent or not exactly `true`, neither
adds group resources. Enabled configuration requires group intake, database,
canonical nonzero host GUIDs and positive canonical epoch before any registration.
Both roles may share exactly one binding; replacing existing host authority refuses.
The worker checks effective group rights before running. Current SQL Extract checks
and the immutable original graph remain enforced independently on every receipt.

18 inert controls PASS0skip cover default-off without DB/identities, invalid or
missing host configuration before registration, both role-specific DI resolution,
shared authority/replacement refusal, native SQL query translation without opening
a connection, and SQL-backed current-grant/due/inbox/restart-wrap selection. Hosted
services are resolved but never started in these controls. No real broker/native
SQL or production activation is claimed. Frozen independent review and mandatory
owned delivery/crash/restart/revocation/rollback acceptance remain next.

### Owned native reference delivery proof candidate

Host-registration aad has scoped independent approval6093691699:63.NET controls
PASS0skip (55 retained+8 own), including actual33-source bounded pass/wrap/failure
continuation, changed epoch refusal, default-off before factory access and opaque
binding replacement denial. Both actual catalog query variants parse SQL160 with
zero errors and closed connections. The generated migration repair is exactly
three-byte BOM removal, with all remaining bytes unchanged.

The next nonshipping executable and coordinator consume the separate owned source
returned by the retained native spool proof, whose two references originated in
actual Core HTTP/SQL commits. They do not manufacture revision/receipt graphs.
Guards precede configuration/resources; only fixed disposable SQL runtime
`sql/AIOfficeLocal/aioffice_runtime` and owned Docker network are accepted. Secrets
stay in inherited environment; only scoped IDs enter stdin. Container termination
requires the exact random owned label and validated container ID. Fixed diagnostics
do not disclose payloads, keys, credentials, SQL exception details or URLs.

Candidate oracles require actual shipping publication, one durable SQL inbox commit
before deliberately held broker ACK, observed unacknowledged delivery, owned child
death, real redelivery and original exact receipt with one effect. They also require
100 concurrent duplicate receipts, native insert rollback, current Extract refusal,
an observed same-source SQL waiter revoked before release, and effective unsafe
column refusal with explicit owned restore. Core/Worker default-off DI is then
enabled only inside this fixture for the second retained event and restarted.

Only this new source's three delivery fields may change. The coordinator preserves
the complete other five graph tables and every immutable outbox field, every original
receipt byte, batch cursor and portal cardinalities. The parent's original source
full6 oracle remains byte-exact. All pipeline disable paths attempt baseline Worker
restoration even if Core restoration fails. Queue observations use the documented
[RabbitMQ list_queues fields](https://www.rabbitmq.com/docs/next/man/rabbitmqctl.8)
and official [CLI JSON formatter](https://github.com/rabbitmq/rabbitmq-cli/blob/master/DESIGN.md).

Local12 guard controls PASS0skip,34 retained/current Python controls PASS and
clean proof build/changed-file format PASS. This is not native acceptance until the
owned exact-head run executes each oracle. Frozen scoped review remains required
before push. Live connector, production volume and full277/278/279/233 remain pending.

### Native proof review repairs and terminal predecessor

Independent review of1a0 withheld approval on three concrete proof P2s: SQL setup
reply loss/locker drain could skip restoration; override write/chmod could skip
baseline Worker restoration; unchanged SQL after restart did not prove resumed
delivery. All three now have local repair candidates. Temporary SQL owns setup
before its reply and restores each statement independently. Locker creation,
stdin and executor are inside the cleanup boundary; release/drain/kill/shutdown,
grant restore and gate drop each remain attempted, preserving the first failure.
Worker baseline restoration owns override writes and chmod as well as Core errors.

The restart oracle requires a live consumer and another real persistent mandatory
publication of an already accepted original reference. It then requires one new
broker delivery and ACK, empty queue, the exact original full inbox/outbox graph,
unchanged cursor and portal counts. Owned management statistics are bounded to
32KiB and ten seconds across headers and body; only three numeric counters leave
the native child. The broker itself is retained across the application restart.

Local12 .NET guard tests PASS0skip and37 Python tests PASS. Additional fault
controls execute the actual cleanup/oracle functions with inert collaborators,
including lost setup reply, every locker setup/drain boundary, independent restore
failures, failed override writes/chmod, dead consumer, missing resumed ACK,
contradictory delivery count, pending queue and changed receipt. Clean build and
changed-file format PASS. Renewed frozen review and real native execution remain.

Predecessor e341 Build38022812535 is terminal RED; Governance38022812541 PASS.
Actual114127247688 and quality114130002171 are RED. All three current native spool
metadata/Renew/interruption markers and retained group/listener/read proof PASS.
Chromium headers-loss retry PASS; body-loss retry received202 headers and failed
the bounded completed-stream wait. Later historical/company proofs were not
re-exercised. The underlying browser/request cause remains unproven. .NET job
114127247828 failed migration CHARSET; approved aad removes only its UTF8 BOM.

The separate Chromium diagnostic now reads `response.request().failure()` on a
failed/timed-out finished wait. Only exact fixed browser error categories are
retained: aborted, reset, truncated chunk, length mismatch or failed. Unknown or
private strings retain the generic refusal. This does not accept a body, substitute
a receipt, add a retry or relax any SQL effect assertion. Playwright1.63's
[client request-failed handler](https://raw.githubusercontent.com/microsoft/playwright/v1.63.0/packages/playwright-core/src/client/browserContext.ts)
does not resolve the finished promise, unlike its request-finished handler.

Ten retained Node guard tests PASS, including six new exact-category/private-string
controls in the actual receipt helper. A local hermetic loopback Chromium probe
using the actual transpiled bounded body reader reproduced both cancelled202 and
truncated202 as browser request failures with a pending finished promise. Three
fresh complete202 responses surrounding those failures remained readable/valid.
This uses bundled Playwright1.62.1 and local Chromium; it is not the hosted1.63
application/SQL fault reproduction or proof of the underlying e341 cause. The next
exact hosted run must still pass complete bytes, strict UTF8/JSON, all seven original
receipt fields, current issued SID and the unchanged full effect graph.

Independent repaired native review b5 has scoped APPROVED6093886641. All three
original1a0 P2s are closed, including the original dead-consumer false-positive
counterexample and setup/drain/write/chmod restoration faults. Reviewer24.NET
controls PASS0skip (12 retained+12 own bounded metrics controls),37 retained Python
and21 independent actual-extracted controls PASS;24 actual SQL templates parse
SQL160 with zero errors. New entrypoints refuse before open stdin/credentials
without owned flags. Shipping src/apps/CI bytes remain approved aad. This approves
the proof code and inert controls; real native SQL/RabbitMQ execution and full277
acceptance remain required. The restarted delivery requires positive
[acknowledgement-mode delivery counters](https://www.rabbitmq.com/docs/4.1/http-api-reference),
with redeliveries included in delivery totals, plus a new ACK and exact SQL graph.

Latest branch audit still has only Draft283 and239. Main is4cfeda58;239 remains
22f2866e, all9 gates green but conflicts with main and lacks clean Windows/UAC/
reboot/startup/repair/backup/signing acceptance. It is not merge eligible.

### Latest-lease recovery follow-on (excluded from approved native checkpoint)

The operational replay API now has an additive result containing the exact event
receipt and the actual backend-renewed lease. Its receipt-only API delegates to
the same kernel, preserving all existing call sites and guards. A host processing
multiple retained items can carry the latest acknowledged lease rather than a
startup snapshot that may have expired while the backend kept renewing ownership.
The result is constructed internally only after current enrollment, owned Renew,
exact committed event response and retained-file acknowledgement. It grants no
SQL/model/provider authority and does not activate a listener or recovery loop.

Local36 spool-transport controls PASS0skip and changed CSharp format PASS. The new
two-capture control renews at20seconds, proves the original30second snapshot refuses
at31seconds before key/Renew and preserves every retained byte, then successfully
replays with the returned50second lease and verifies the next61second lease.
Independent frozen143 review has scoped APPROVED6093966487:36 retained plus20
independent controls PASS0skip, including current Renew refusal before keys, late
ACK/cancellation preservation and receipt-only/additive equivalence. Actual
operational loop/restart evidence remains. This shipping follow-on is excluded
from the ee native/diagnostic push checkpoint.

### Capture/recovery session candidate

One host-owned session now serializes capture and recovery under the same generated
process owner and actual returned lease. It clones bounded host enrollment, checks
exact open spool account/service, filters unenrolled/DM/self/echo before HTTP/key
work and rechecks fresh qualification/expiry and cancellation immediately before
encrypted append. Success still requires the authenticated SQL event ACK; unknown
replies preserve the original capture. Recovery traverses32 event hashes fairly,
does not delete refused/obsolete versions and heartbeat never claims connection.

Local89 combined spool-transport/file controls PASS0skip. New session controls cover
original reply loss, same-owner retry, actual policy new-owner fencing/expiry/epoch2,
33-item fair traversal with a retained poison capture, revoked/changed authority
before decryption, all5 spool ownership mismatches,7 enrollment configuration
denials, noncooperative metadata/listener/key/event cancellation and late replies,
cancellation after key decode, and cancellation while waiting behind active capture.
These are isolated controls with actual lease policy and file encryption, not
native HTTP/SQL/RabbitMQ or provider acceptance. Freeze/review this candidate, then
prove the host/recovery path against owned runtime resources before acceptance.

Exact remote checkpoint ee3676771d9c8ff0b513ddfeea73d5662bda8d5b reached terminal
RED Build38025424177; Governance38025424210 and all six prerequisites PASS.
Actual114135146988/quality114137785020 FAIL. It excludes local143/session changes.
Root inspected all retained group/read/listener and three current spool markers
PASS. New native reference hold/commit/kill/redelivery/exact ACK,100 duplicates,
rollback/current Extract revoke and observed queued revoke PASS. Unsafe setup
observed effective column UPDATE=1, then the child failed the unsafe refusal oracle.
DI/restart and main Chromium have not been re-exercised on this head.

The native harness expected InvalidOperationException for unsafe permission, while
the actual permission verifier deliberately returns UnauthorizedAccessException.
The test-only classifier now accepts only that authorization type for deny/unsafe,
and DbUpdateException for rollback. Unexpected operation, transport or cancellation
cannot qualify.17 guard/classifier controls PASS0skip and changed format PASS.
Shipping permissions and all effective-right/full-graph/restore oracles are unchanged.
The original child exception was not logged; repaired native execution must still
prove unsafe refusal plus restored DI delivery/restart and complete main browser.

### Worker recovery host candidate

The session and refusal classifier have scoped independent approvals:
0766094089395 (89 retained+9 own98 controls) and9956094089704 (17 retained+8 own25
controls), all PASS0skip. Exact remote995286fae8447c08cb87611a503b8fe83431103c is
actively closing Build38026673330/Governance38026673652. Governance and all six
prerequisites PASS; actual114138866854 is running. New host changes are excluded.

Worker recovery is explicitly default-off and shares one lazy runtime with capture.
It freezes bounded trusted configuration, enforces reference-role process authority
in both registration orders, checks group permissions at startup and rotates actual
session recovery. Fast input refusal and cancelled operations cannot open a volume
or resolve keys. Unknown replies survive stop/disposal. Fixed logs and five-second
refusal backoff avoid private exception output; stopping cancels that backoff.

Local60 host/retained-registration controls PASS0skip. Configuration/URI/secret
alias/source-list/storage-root/profile and replacement denials run before resources.
DI resolution is inert. Two lifecycle controls inject an actual encrypted file/session,
lease policy and bounded fake HTTP into the runtime: shutdown cancels held event ACK,
preserves exact bytes through late reply/reopen, and the hosted loop settles the exact
file before stopping. This isolated injection does not prove the native factory,
real HTTP, SQL fence, process restart or production volume. Frozen scoped host review
and separate owned native capture/recovery process proof remain mandatory.

### Exact244 terminal restart observation and bounded same-PR repair

Build38033648198/actual114159514486/quality114162493033 failed;
Governance38033648226 and six prerequisites passed. All retained native group,
listener, read, spool, managed process/startup and six reference stages passed.
The new proof reached its post-consumer observation after exact persistent
original-reference inspection before/after the owned RabbitMQ restart, unchanged
SQL graph and no republication. ACK+1 was observed; the following immediate
snapshot did not satisfy delivery+1 and consumer1. No numeric counters were
emitted, so the actual failure cause remains unproven. Chromium was skipped.

The same-PR candidate observes all three exact conditions together in one
management snapshot within the existing30s bound. It adds fixed scalar failure
counter deltas only, preserves all ownership/restoration and original-reference
checks, and still requires empty queue, count2, exact full8 graph, portal counts
and clean pooled isolation.43 local Python controls passed, including a transient
ACK-before-delivery/consumer positive and disjoint, overshoot and missing-consumer
refusals. Shipping source and native executable bytes are unchanged. Independent
frozen review and renewed actual native/browser execution remain required; this
is no full277 or production acceptance.
