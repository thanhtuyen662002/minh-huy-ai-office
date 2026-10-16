# Original dependency mismatch checkpoint

Status: narrow implementation checkpoint; source review and actual SQL qualification pending. Runtime activation is not enabled. This decision does not complete issue278 or authorize a merge of an incomplete product slice.

## Decision

An immutable original V2/V3 plan keeps its initial allocation cutoff and selected input. A validated current source/brain metadata mismatch must not be relabeled a benign edit, silently replace that plan, reset model spending, create an attention note, or claim terminal completion. Missing, unreadable, revoked, corrupt and unsupported changed-authority dependencies remain failures.

The first unit supports only a batch with zero committed contributors and zero linked note/raw/selected/outbox effects. It appends one `GroupOriginalInvalidations` row keyed by the existing tenant/company/binding/batch. The row binds the original plan hash and original claim, current checkpoint claim, domain-separated current dependency observation, timestamp and consistency digest. Full scoped restrictive foreign keys retain the original and both claim receipts. Runtime permissions allow SELECT/INSERT and deny mutation; migration rollback refuses destructive deletion.

The metadata-only owner runs in its own clean SQL Server Serializable transaction under the existing transaction-owned source APPLOCK. It authenticates the actual original and claims, inspects the same validated reader metadata without resolving keys or invoking a provider, refuses committed/partial effects, and rechecks metadata, rights, current claim, original bytes, empty effect graph and the actual written row before commit. Trusted exact evidence-to-revision and item-to-outbox/revision foreign keys prevent an empty parent graph from concealing orphan effects. An existing checkpoint is authenticated and returned without resurrecting the original when metadata later matches again. Started/failed/admitted/accepted attempts are retained as spent history; they are never counted as committed contributors.

`CheckpointSha256` is an unkeyed consistency digest. It cannot independently authenticate a resealed dependency observation. The writer obtains and compares actual validated SQL metadata before and after insertion. Replay reports quarantine only and grants no execution capability.

Existing original/journal/result/note/no-work/terminal writers observe any checkpoint presence in their owned SQL fences, including legacy and null-original paths. Presence blocks use even if checkpoint non-key fields are corrupt. This activity observation does not replace claim authority or erase expiry. Accepted-result reads retain their existing Shared source-lock capability. Completed-terminal-FIRST recovery remains in its separate existing owner and does not read the checkpoint table.

An authentic observed expiry wins over mismatch/activity. The checkpoint writer rolls back its staged row to a savepoint, detaches it, reauthenticates the matching current claim and commits only the existing expiry witness. It preserves the first observation across UTC rewind, including before the original issue time. Revoked/foreign/replacement claims cannot be retired through an old handle.

## Limits and activation gate

This is not a continuation generation. Partial batches are refused. No new scanner, Worker registration, provider client, replacement plan, coverage merge, business action, terminal format or frontier advancement is introduced. A terminal presence returns only `RequiresTerminalRecovery`; completion must be authenticated by the existing terminal owner.

Do not write checkpoints while older automatic writer binaries remain active: they do not know this activity fence. Expand the schema first; drain and replace automatic writers before separately reviewed activation. Native SQL proof must cover durable insertion and lost-reply replay; retained attempts; partial/orphan refusal; each actual writer and an unconsumed permit; key/provider races; rollback after flush; expiry/rewind/replacement; permission failures; and unchanged terminal-FIRST recovery. Pure/synthetic tests and offline SQL parsing do not qualify those cases.

Versioned continuation, global/residual spending limits, committed contributor preservation, complete raw coverage, no-click Worker/scanner activation, internal IT delivery and full product acceptance remain subsequent work. Real model evaluation uses the existing AI Office Gateway/configuration and remains NOT_RUN_UNEVALUATED until an accessible provider is supplied.
