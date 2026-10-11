# Immutable dependencies of an automatic committed chunk

Issue278/Draft288 remains incomplete. Current remote ec3e8f79 owns
Build38093894476/native114335702045 ACTIVE112; Gov38093894474 and six prerequisite
jobs SUCCESS. ROOT fetched the FULL .NET job114335701871 log through the completed
job endpoint:2774=[6,520,25,2223], zero failures/skips and four successful runs.
Native500/history/suffix501/current112/all8 remain unqualified until terminal
logs and six ORIGINAL browser images. Do not supersede meaningful active CI.

## Atomic versioned receipt metadata

Expand GroupWorkCommitReceipts with DependencyManifestVersion(default0) and
nullable varbinary(6902) DependencyManifest. Additive migration20261010231206
retains every existing row/key/FK and requires either version0/null or
version1/non-null218..6902 bytes. Explicitly DENY UPDATE on both new columns;
the existing append-only effective-permission verifier also checks every column.
Destructive down migration is refused; delivery uses reviewed forward migration.

Fresh automatic note and automatic NoWork effects capture version1 in the same
original receipt transaction/savepoint as dispositions/raw links/notes/outbox.
No additional effect rows, source cursor, provider/client/configuration or IT
business state are introduced. Legacy entry paths retain version0/null.
Version0 receipts remain replayable under the existing complete legacy fences;
absence never qualifies dependency validation or future terminal advancement.
Version1 replay requires the entire closed original bytes, including dependencies,
instead of only a selected-source count/hash. Missing/wrong-version/changed bytes
refuse. Existing final source/brain/authority/expiry fences remain mandatory.

## Closed bounded format and exact contributors

Format1 has a fixed162-byte header: domain/version magic, tenant/company/source,
batch/operation, allocation cutoff, existing complete authority fingerprint,
exact source/account coverage fingerprint, and two bounded counts. At most100
source descriptors occupy56 bytes each; at most20 brain descriptors occupy57
bytes each. Minimum218 and maximum6902 are exact. GUIDs and positive revisions,
closed request/glossary kinds, strict canonical contributor ordering and complete
input length are checked; duplicates, overflow, truncation or trailing bytes
cannot become a partial manifest. Parsed collections are immutable and private.

Source fingerprints retain both cutoff and current heads, original exact receipt,
opaque metadata, disposition and protected-envelope fingerprint. Brain
fingerprints retain current business/glossary heads, original protected revision,
evidence and contributing source heads. Coverage retains every bounded source and
account interruption. Fingerprinting processes private metadata and ciphertext
digests only; it excludes decrypted message/note/glossary content. Temporary
serialization buffers are cleared. Manifest bytes contain scoped GUID/revision
metadata and fingerprints, with no plaintext body, source quote, credential
reference, key/token or endpoint.

Internal source and brain reconstruction fences accept a parsed immutable
manifest only with a current sealed claim and caller-owned serializable,
savepoint-capable SQL transaction. They recheck current authority, scoped original
allocation and every exact contributing metadata/cipher/coverage dependency.
No key, plaintext, commit, expiry retirement or frontier mutation is performed.
An expired verdict belongs to the future consumer's rollback/witness unit.

## Local verification and remaining work

40 focused controls PASS: actual scoped InMemory ingress/source/brain capture,
100 sources plus20 brain contributors reaching exactly6902 bytes without loss,
canonical selection stability, ten metadata/cipher mutations,15 malformed
frames, five strict replay/legacy cases, four duplicate/order cases, unowned
transaction/cancellation refusal and actual EF/migration/SQL160 checks. These
are model-free controls, not native SQL or model quality evidence. Existing
shared-store/reader behavior is checked by the complete Persistence suite:
2263 PASS, zero failures/skips.

Native immutable receipt fields, source/brain reconstruction under SQL locks,
rollback/replay, independent-process races and manifest corruption qualification
remain required. Current remote112 predates this source/schema change and cannot
qualify it. No terminal consumer/frontier is enabled by this checkpoint. Whole
terminal coverage must account for every raw allocation and validate every
contributing chunk before advancing contiguously. The manifest covers explicit
reader selections; relevance/selection completeness, provider/profile/tokenizer
pinning, token/context chunking, retry truth, honest gaps/backlog/zero-message
carrier and no-click DI/API/UI are still incomplete. Realmodel is
NOT_RUN_UNEVALUATED. Full278/279/233, customer production and merge acceptance
remain withheld.
