# Scoped protected work note schema checkpoint

Issue278 / Draft288. Migration `20261010150902_AddGroupWorkNotes` is an additive expansion of the existing SQL Server model. It creates ten tables without altering existing source, allocation, claim or portal task tables. This checkpoint does not implement note stores, model release, worker DI or extraction completion.

## Persistence boundaries

Every primary key and group relationship retains tenant/company/group scope; foreign keys use Restrict. Customer request heads carry business status/version and the exact current revision, while immutable revisions carry protected private content. AI source-backed interpretation and IT-authored confirmation are distinct origins. New unconfirmed records cannot acquire resolved status, an assignee or an IT SLA. Customer deadline wording remains private evidenced content, never a committed deadline. Nullable AI claim metadata is explicitly checked with IS NOT NULL to avoid SQL CHECK accepting UNKNOWN.

Request codes, origin batch/operation/candidate ordinal, original chunk receipts, selected source dispositions and outbox identities are independently scoped and unique. Evidence binds exact source message/revision and exact protected request revision; literal quotes remain inside the encrypted payload, selected by evidence ordinal. A per-chunk receipt/disposition is not a complete raw allocation ledger or a terminal extraction frontier. Those completion and supersession rules remain required.

Request/glossary content stores the existing purpose-bound brain AEAD envelope (30..64029 bytes), bounded key ID and ciphertext envelope hash. There are no plaintext title/problem/quote/deadline fields or private plaintext hashes. Current revision heads deliberately have no reverse foreign key to avoid circular inserts: stores must atomically establish and validate the exact immutable revision chain. Source version/deletion generation must be fenced before private reads, keys, release or effects.

NotesCommitted outbox/items contain only references to the committed chunk and exact request revisions. Delivery updates are restricted to availability/attempt/publication metadata; destination and private body are absent. The future internal IT reporter must independently authorize its route/audience and reread the protected revisions. No portal task or customer outbound relation is added.

Editor grants are separate from Reader/audience grants and require company membership. Glossary entries/revisions are operator-published, versioned and protected. C# construction defaults IsEnabled/AllowExtraction to false; SQL has no implicit defaults and requires explicit complete operator rows. Runtime cannot publish or enable this registry.

## Runtime permissions

The migration freezes literal ownership and grants/denies. Registry tables are SELECT-only; immutable revisions/evidence/receipts/dispositions/outbox items are SELECT/INSERT-only. Request heads and outbox have only explicitly allowlisted mutable columns. Identity/lineage columns, all other column updates, DELETE/DDL/CONTROL/ownership are forbidden. GroupWorkNotePermissionVerifier composes the existing source/runtime proof and checks current effective rights, including every column and operator ownership. This permission proof itself conveys no product/private authorization.

The new owned CI proof reads scoped empty metadata through the actual EF model and verifier, then injects two effective column escalations (request identity and editor enablement) through the existing owned operator fixture. Each must produce the actual authorization refusal, restore the exact original permission catalog digest/effective denial, and recover the positive empty-schema proof. Existing source/inbox/allocation graph hashes remain unchanged. Unrelated SQL/transport errors cannot satisfy the refusal.

## Verification and limits

Local full Persistence suite:1831 passed,0 failed,0 skipped. Focused model/protection controls include scoped FK/Restrict/no-shadow boundaries, finite private envelopes, confirmation/null constraints, lineage/receipt/disposition/outbox uniqueness, separate editor authority, additive frozen rights and SQL Server160 parsing of generated migration/verifier. EF reports no pending model changes and generates the idempotent script without connecting to or changing a database.61 Python guard controls pass, including actual AST permission restoration/refusal safety; these inert controls are not native SQL acceptance.

Local Docker is unavailable. New migration and work-schema/work-unsafe modes have not yet executed on actual SQL. Protected reader/store authority, source-lock-before-savepoint staged-effect rollback/expiry witness retirement/MARS controls, atomic notes/coverage/completion/outbox, context/tokenizer, automatic DI/UI and frozen real-model evaluation remain required.278/279/full233 remain ACTIVE; real model NOT RUN / UNEVALUATED.
