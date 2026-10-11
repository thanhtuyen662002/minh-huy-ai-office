# Same-batch brain input prerequisite — issue 278 / Draft PR 288

## Scope

This checkpoint adds a metadata-only prerequisite to
`GroupWholeBatchDependencyReader`. It runs inside the reader's existing owned
Serializable SQL transaction after original contributor claims/effects have
been authenticated and before the retained source, brain and final authority
checks. It does not create a terminal receipt, advance a cursor or authorize a
later write. A future terminal writer must repeat the whole reader inside its
own effect transaction.

The existing `AIOGEFF1` effect digest and version-one brain/dependency manifest
bytes are unchanged. The sealed effect ledger now exposes detached original
request IDs in candidate order and the original commit UTC timestamp. Later IT
edits and publication progress do not replace the original creation proof.

## Origin and ordering checks

The union of explicitly selected request dependencies is read from
`GroupCustomerRequests` using tenant/company/binding predicates, no tracking,
ordered IDs, a metadata-only projection and `TOP 2001`. At most 2,000 distinct
origin rows are accepted: 100 original contributors times 20 selected brain
revisions. This is an aggregate metadata bound; each original brain read still
has its unchanged 20-revision/256,000-encrypted-byte limit. No protected content,
current business fields or additional brain selection is read here.

Every requested origin must be present exactly once, have the same scope,
nonempty original batch/operation, candidate ordinal 1–40 and zero UTC offset.
Its request ID must match the existing `aioffice-group-note-id-v1` deterministic
identity, including external-batch origins. Substituting an origin batch,
operation or ordinal cannot turn a same-batch request into an accepted external
dependency. Origin creation cannot occur after the dependent contributor's
commit.

For same-batch inputs, the origin must match an authenticated original effect's
request ID, operation, candidate ordinal and exact commit timestamp. Unknown
same-batch origins and self-dependencies are refused. Dependencies form a
directed graph between original contributors; deterministic topological order
accepts equal commit timestamps and refuses cycles. Glossary dependencies and
valid external-batch origins add no same-batch edges. `OwnDependencyCount`
counts distinct contributor-to-contributor edges, not selected request rows.

Actual bounded enumeration is used; caller collection counts are not trusted.
Cancellation remains cancellation. Origin/graph errors use a fixed diagnostic
with no inner exception or supplied private data. The origin reader also
requires SQL Server, an existing Serializable/savepoint transaction on the same
connection, no ambient transaction, no pending tracked changes and no MARS.
It does not acquire standalone authorization or own a transaction.

## Local verification

The exact promoted five source/test files pass 304 selected repository tests:
24 new own-input controls and 280 retained coverage/effect/digest/dependency
controls, with zero failures or skips. The controls cover a five-contributor
equal-time chain, deterministic ordering, original immutable projections,
external and glossary inputs, self/unknown/future/forged/foreign origins,
two- and three-contributor cycles, missing/duplicate/extra origins, actual
enumerator disposal, private diagnostics, cancellation and absent owned SQL
units.

Actual EF-generated two-ID and 2,000-ID queries are parsed with SQL160 while the
connection remains closed. The maximum query stays within SQL Server's 2,100
parameter bound. Five C# whitespace checks pass. Structural fixture ciphertext
is a shape carrier; it is not a real decryption or customer-data proof.

The older stash `9371e02516428c8d30e9d8e49a0bb2868775cd08` is retained,
superseded and not applied. Only the latest independently staged, tested and
formatted five files were promoted onto reviewed parent
`464607ac961d409ef02d658f9379fc56e39316ce`.

## Remaining qualification

Actual nonempty-brain/multiple-contributor/own-input SQL execution and
adversarial races are **NOT_RUN_UNQUALIFIED**. The 116-marker native CI profiles
on older heads have one contributor, two selected sources and zero selected
brain revisions; they do not prove this new graph or origin query.

Owned terminal transaction/replay, expiry rollback, authenticated contiguous
SQL frontier, gap/retry attention, shipped automatic DI/API/UI and full
278/279/233 acceptance remain incomplete. Actual model evaluation is
**NOT_RUN_UNEVALUATED**. Model execution must reuse AI Office's existing AI
Gateway and provider configuration; no independent AI client or secret store is
introduced. This checkpoint is not merge or production acceptance.
