# Durable submission recovery acceptance

Issue272 / PR274 under full product233. Candidate `276885809cd6a13fd31c0201efe159c669fec8fe` implements immutable preparation, explicit same-operation execution and read-only recovery. It subsequently delivered at main `09b7d36b23b554fcaa2b3534ddc6faa166cac651`; see final delivery below. This report does not mark the full product production complete.

## Exact passing candidate

[Build37978945377](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37978945377) and [Governance37978945389](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37978945389) passed. All8 Build jobs passed, including actual SQL/OIDC/broker/API/worker/FE/Chromium113984241483 and required quality113988410306. Hosted verification includes1287.NET tests (860 persistence,396 contracts,25 configuration,6 observability) and1276 web tests including actual Redis.

Full frozen276 shipping/security/runtime/all4images is independently APPROVED, [receipt6087833026](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/274#issuecomment-6087833026). Earlier shipping/static a843 [receipt6086781182](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/274#issuecomment-6086781182), bd3 proof delta [receipt6087479625](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/274#issuecomment-6087479625) and276 bounded recovery [receipt6087625337](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/274#issuecomment-6087625337) remain recorded. Final documentation review and exact PR/main delivery gates remain.

## Shipping paths

- `src/Shared.Contracts/TaskSubmissionIntentContracts.cs`: exact version1 byte fingerprint, opaque operation and fixed server attempts/lifetime/quota.
- `src/Platform.Persistence/TaskSubmissionIntentService.cs`, `TaskSubmissionIntentPermissionVerifier.cs`, `TaskSubmissionAdmission.cs`, `PilotTaskStoredRequest.cs` and migration20261009085621: additive immutable owner intents, atomic existing task/outbox graph, strict raw UTF16/hash/original-event compatibility and fresh authority.
- `src/Core.Api/TaskSubmissionIntentEndpoints.cs`: bounded strict request bytes/JSON/selectors, authority-owned scope and sanitized no-store responses.
- `apps/web/lib/task-submission-intent.ts` and `task-submission-bff.ts`: fixed Core paths, issued-SID/body/hash/deadline fences and exact Core Unicode edge-whitespace policy preserving FEFF.
- `apps/web/components/use-task-submission.ts`, `submission-recovery-panel.tsx` and `local-ai-workspace.tsx`: synchronous immutable operation, explicit exact retry, GET-only discovery/restored composer and final generation/private clearing.

## Actual disposable SQL

- Immutable prepare/replay has fixed24h lifetime and creates no execution. Exact4000 UTF16/supplementary/U+FFFD/leading-trailing-soleFEFF and composed/decomposed fingerprints survive; adjacent overflow fails without effects.
- Two concurrent same-operation POSTs returned202/202 in this run. Independent task/step/message identities, global deltas[1,1,3,1,1,1,0], one completed worker attempt/dispatch/checkpoint and unchanged settlement bytes pass. The proof also accepts only the exact bounded503 uncertainty contract followed by fresh exact GET and one deliberate identical replay;26 independent caller-contract refusal/binding/call-bound controls passed. This does not infer the cause of an earlier generic503.
- Every protected intent column and destructive/ownership operation is refused to the runtime. Actual unsafe column grant, impersonation, ordinary owner chain, EXECUTE AS OWNER and trigger controls close every intent boundary; restoration recovers positive access.
- Malformed raw intent/original-event UTF16/hash/source/question/key/attempts/duplicate/unknown evidence cannot become accepted execution. Fixed expiry, complete outbox-write rollback, restored same-operation retry,99/100/101 quota and compatible reserved legacy-key adoption pass.
- All15 actual queued prepare/new-execution/committed-replay revocations pass for membership, global user, source enabled, source read and binding. The valid write-only read-revocation fixture keeps its schema constraint enabled and restores exact original source bytes.
- All4 actual final owner detail/list membership/global-user fences pass. Under unchanged RCSI1, an owned transaction-only TRUNCATE holds TaskEvents Sch-M; BOTH shipping permission queries return1 as the runtime while the lock is held. The proof observes the exact real TaskEvents/PayloadJson Sch-S wait before authority revocation, then requires empty403/restored200 and identical full intent/execution/settlement bytes after rollback. The earlier ALTER metadata wait was rejected. The compiled-reader contingency was not integrated or used.

## Actual shipping Chromium

Both owners acquire real provider Code/S256 issued sessions. The owned loopback proxy fully consumes the actual scoped Core202 receipt after commit before dropping headers or partial body; no synthetic acceptance or authority is supplied.

- Header and partial-body loss produce honest uncertainty. Explicit fingerprint-only retry preserves the issued SID, original operation/task/step/message/CreatedAt and one settled graph/worker/dispatch/checkpoint with no added settlements.
- Actual preparation200 reply loss preserves the exact FEFF-edged request. Reload, logout and new Code/S256 SID recover it through GET only. Selection restores the composer; source revocation gives403 without effects; restored explicit send creates one completed task.
- Two provider owners and two companies enforce reciprocal foreign-owner404, including the administrator, with no foreign input in lists.
- An actual successful private200 held across a provider company switch is discarded. Switching back recovers the original through GET only with identical durable bytes.
- External membership loss clears the private recovery panel/input and shows sign-in. Restored membership plus a new provider session recovers GET-only without extra submit or durable effects.

All new and retained controls passed in113984241483 on2026-10-09; new browser cases completed between19:25:14Z and19:25:39Z.

## Current images and scope

Artifact11641216062, `browser-workspace-276885809cd6a13fd31c0201efe159c669fec8fe`,806962 bytes, contains workspace.png, administrators.png, task-history.png and task-submission.png. Root and independent reviewer inspected all4, including original1440x5809 submission image: controls/text are readable, long scalar fixture wraps, no horizontal clipping or credentials. These are synthetic owned-fixture screens, covered by full frozen approval6087833026.

Live Medcom/Novo inventory/stock-movement adapters and reconciliation, qualified customer read-only credentials, production identity/deployment/backup and actual Windows lifecycle/signing remain separate acceptance. Santino is deferred. All62 requirements and233 remain active. After this PR is merged and main verified, read the user-provided second-brain document and track its integration through the normal issue/Draft/CI/review process.

## Final delivery: 2026-10-10 local time

Final PR head `f3cf3cc7eb8daa2737473f67d298b86858ee00c1` has independent final6doc
review [6087900218](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/274#issuecomment-6087900218),
exact Build37980837971/Gov37980837996/all8/actual113990625301/quality113994648735 PASS.
PR274 merged2026-10-09T19:46:10Z, issue272 closed, at main09b7. Fetched main tree
matches finalf3. [Main Build37982478564](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37982478564)
and Governance37982478631/all8/actual113996168399/quality114000211598 PASS.
Current1287.NET/1276web-realRedis, all15 queued cases, all4 final private reads,
all new/retained SQL and shipping Chromium cases PASS. Main browser new cases
completed19:56:14Z–19:56:39Z. Root inspected all4 original main images from
artifact11641407860/807412bytes: readable/sanitized/no horizontal clipping.
No272 acceptance remains. User attachment was read only after this closure;
#276–#279 track the next automatic SQL brain and IT-only notification work.
# Issue278 exact c8 failure and document observation repair

Exactc8b67775c91822ef9f0e28e2a3651669002d5641 Build38061609455/native114240837316/Quality114245095778 is terminal RED; six prerequisites and Governance38061609472 are SUCCESS. SQL/worker completed, including original held-key current-authority revocation and durable expiry/rollback proof. Root log inspection finds88/93 required markers exactly once,144 PASS and3 terminal failure markers. The five missing markers are browser completion/owner/company/restored receipt/membership recovery. No browser artifact is available. Root hosted.NET verified2365 passed [6,520,25,1814], zero skipped; this does not make the overall run green.

Chromium failed at `submission-historical-source-restored-replay-request-aborted`. Independent frozen review reproduces a diagnostic gap: Code/S256 navigation replaces the document, removing the passive fetch lifecycle observer installed in the original window. The CDP observer survives, but the missing window evidence collapses timeout versus owner abort into the generic refusal. Product abort cause remains UNPROVEN; this is not evidence that a receipt was valid.

Reinstall the same guarded bounded passive observer after the new issued SID fence and before historical replay. It forwards exact fetch arguments/response, reads no private body/header/credential, sends/retries/cancels nothing and retains its eight-request bound.26 actual helper/AST controls pass, including original/new document timeout and owner-abort classifications; every aborted receipt still fails. Original20s finished/body gates, body<=4096/strictUTF8/202/original operation/fingerprint/SID, one graph, worker and idle oracles remain unchanged. Renewed exact-head native/browser/all8/original images are required; this repair is not runtime acceptance.
