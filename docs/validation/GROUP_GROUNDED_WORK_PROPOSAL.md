# Private grounded work proposal checkpoint

Issue278 / Draft288. This is a concrete bounded parser and a schema for the existing StructuredGeneration Gateway. It does not persist notes, activate an automatic worker or qualify semantic model accuracy.

## Boundary

`GroupGroundedWorkProposal.Parse` consumes a sealed host source preparation. Model output has a fixed version, notes and one source disposition for every model-eligible candidate. Tenant/company/group/batch remain host-owned. Model fields cannot set IT status, committed SLA, assignee, destination, binding, verification or authority. Relation text remains an unverified hint; no request link/update is authorized by it.

The entire decoded output passes the existing versioned secret quarantine first. The parser then enforces strict UTF8<=64000 bytes, the quarantine depth/node/character bounds, exact closed object shapes and unique decoded property names. At most20 notes,10 references per note and100 total references are accepted. Field limits bound title/problem/outcome/missing/deadline/hint/quote lengths, and format/control-only strings are refused. These bounds apply jointly; a proposal can fail the node or byte bound before another individual maximum.

Every evidence reference must contain the exact canonical host GUID and current prepared revision. Quotes are nonempty visible literal UTF16 substrings of the original candidate; they are not normalized or reconstructed. Foreign, omitted, host-quarantined/recalled or wrong-revision candidates cannot be reintroduced. Duplicate evidence/note entries are refused. Several sources may support one note and one source may support several distinct notes. Each candidate has exactly one disposition: work iff referenced by a note, otherwise no_work. This covers model-eligible candidates only; host preparation receipts and full raw allocation/terminal coverage remain separate obligations.

Titles/problems/outcomes are explicitly AI interpretations. Literal quote matching does not prove their meaning, negation handling or actionable classification. Customer requested deadline wording must occur within validated evidence and retains the supporting reference. It is never converted into an IT commitment, business resolution or runtime completion. Default formatting is private; typed results and their collections cannot be forged or mutated by callers.

The schema is compatible with the existing strict StructuredGeneration profile and reusable through the existing `IAiGateway`/`OrderedFailoverAiGateway`. No AI client/configuration/provider adapter is added. No model input assembler, qualified tokenizer/profile, same-group note/glossary context, provider release fence, SQL note effects, worker DI or UI is introduced. A later release/writer must recheck the attached source context/live claim and all contributing current note/glossary dependencies; parsing is not a future authorization.

## Evidence and remaining work

Local41 new controls passed through the actual ingest/allocation/claim/reader/preparation code with owned InMemory fixtures. Full Persistence passed1809, zero skipped and no warnings/errors. Tests retain exact Unicode/FEFF quotes, customer deadline distinction, no SQL graph effects, many-to-many evidence,100-source disposition completeness, exact/adjacent20-note/100-reference/64000-byte bounds, collection immutability, foreign IDs/revisions, missing/contradictory coverage, nested decoded duplicate names, unsupported IT authority fields, malformed/private output and later Recall release denial.

One test runs the actual existing ordered Gateway/Responses adapter with an injected synthetic HTTP response to prove schema compatibility and store=false. This is a mechanical adapter control, **not** an actual model call or semantic evaluation. Proposed120 primary/40 independent windows remain NOT RUN / UNEVALUATED; actual>=100/>=30 real eligible windows and semantic thresholds remain required after input/policy/prompt/schema/profile/labels are frozen. Model-free controls cannot satisfy those gates.

Predecessor2199a50 has scoped independent approval6098494666 and is pushed. Build38059643216/native114235105757 is IN_PROGRESS; six prerequisites and Governance38059643200 SUCCESS. Root verified its exact .NET job2319 passed tests. Renewed native claims/source/preparation/listener timing and six original browser images remain unaccepted until terminal logs/artifact inspection. Earlier686 red and cecee withheld evidence remain historical; do not infer runtime closure or full278/279/233 completion from this local parser.
