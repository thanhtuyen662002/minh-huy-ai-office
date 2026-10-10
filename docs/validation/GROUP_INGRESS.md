# Group ingress: implementation and evidence

Issue277/PR283 implements the automatic customer-source ingress accepted by
issue276. Contracts PR282 is delivered on main4cfeda58; PR283 now targets main.
The active PR HANDOFF records the exact candidate and workflow IDs.

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
