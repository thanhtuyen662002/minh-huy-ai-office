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
