# Fixed combined note writer

Issue278 / Draft288 remains active. `GroupNoteCommitStore.CommitAutomaticAsync`
accepts only the sealed combined plan for one exact preparation. It uses the
same transaction/key/fence/savepoint/replay engine as the existing standalone
AI `CommitAsync` method, whose entry guards remain unchanged.

An internal immutable projection retains all selected source outcomes and
orders AI interpretations before host observations. AI entries retain their
protected literal quotes, AiExtracted/SourceBackedAiInterpretation provenance
and LiteralSourceQuote evidence. Host entries derive UnsupportedMedia or
SecretQuarantine only from the sealed host plan, use HostAttention/HostObserved
provenance and HostMetadataAttention references, and remain unconfirmed
NeedsClarification. They contain no quarantined source text, model verdict,
assignee, IT commitment, destination or assertion of retry exhaustion.

The combined writer stages one immutable receipt, exactly one disposition per
selected source, each request/protected revision/evidence set, one metadata
NotesCommitted outbox and all items in the same owned SQL transaction. Mixed
plans use the Notes receipt outcome; host-only plans use Attention. Stable IDs,
exact original replay, changed-proposal/new-nonce refusal, current claim/source/
brain fences, source lock, late-key disposal and expiry-witness-only rollback
use the existing fixed engine. Replay reads retain a sentinel beyond40 notes
and beyond300 evidence rows (100 AI plus at most200 host references); no
collection is truncated. Aggregate protected-envelope bytes remain bounded.

This method requires1..40 notes/1..100 selected heads and denies a coverage gap,
zero-note completion, a foreign dependency/worker, MARS, caller/ambient SQL
transaction, dirty tracker and cancellation before key access. The separate
legacy AI method retains1..20 notes/all-model-text/no-media/no-gap limits.
Coverage gaps need a durable carrier and a later reviewed effect path. Neither
writer establishes whole raw-ledger/frontier completion or automatic scheduling.

## Current verification and limits

Fifteen new actual public-entry controls cover adversarial guards and the
permitted media, quarantined-media and empty-media paths through a deliberately
denied SQL connection. Three new actual reader/preparation/parser projection
controls cover separate provenance/metadata evidence, host-only Attention and
the maximum40 notes/all100 sources/299 evidence rows. Thirteen retained legacy
entry controls pass; focused total31, zero failures/skips. These controls do
not execute SQL writes or establish model semantics.
The full local persistence suite passes2115 tests, zero failures/skips;
changed C# formatter and strict durable YAML checks pass.

Native acceptance on parent5ff2855 is closed: Build38080032149,
Governance38080032175 and all8 gates including native114294879898 and
quality114299950359 SUCCESS. Root parsed FULL native log: all102 required
markers exactly once,158 PASS,zero failure markers. Root parsed FULL .NET:
2636=6/520/25/2085,zero failures/skips. Root visually inspected all6 ORIGINAL
Chromium images from artifact11680029776. This qualifies that frozen parent
scope, including the existing standalone SQL effect proof, only.

Later host-reader preparation577, combined plan57a and capacitybbbb have scoped
source approvals6101445392,6101478951,6101577796; root fetched each FULL receipt.
Native104 host/boundary proof, automatic combined SQL commit/replay/expiry/
authority-race proof and source review of this writer remain pending. A green
future104 run alone does not qualify the new automatic method: the existing
native note calls exercise the standalone wrapper through the shared engine.
Automatic DI/API/UI, gaps/raw coverage/terminal completion, retry truth,
context/tokenizer, real100/30 model evaluation and279 remain required.
Actual model is `NOT_RUN_UNEVALUATED`, using the existing Gateway/config loader.
