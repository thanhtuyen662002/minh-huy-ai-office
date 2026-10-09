# Remote branch integration audit

Observed 2026-10-08 against accepted main `002e5cd9c1fd95eabc02dd3580bb8e964409764b`. GitHub paginated branch state, up to 400 merged PRs and fetched Git ancestry were inspected. This is a dated snapshot; current PR HEAD, acceptance, review and required workflow results remain authoritative.

Of 130 remote branches outside main, 123 exact branch HEADs match merged PR HEADs. One additional branch is already an ancestor of main. Four branches are historical snapshots or a superseded implementation, and two retain active Draft leases. No further eligible unmerged implementation was identified.

| Branch | Observed HEAD | Evidence and action |
| --- | --- | --- |
| `erp/144-health-evidence-expiry-horizon` | `91bf7a6f33ed72108bb4a27ef7ab07dc7c0d8741` | `git merge-base --is-ancestor` succeeds against accepted main002. Already integrated. |
| `backup/50-pre-lead-reconcile-20260920` | `d0ed814f317907acc6bcbcf59bf8e395d8b7dac6` | Prior reconciliation/divergence backup without a PR. Preserve historical backup. |
| `backup/51-pre-datasource-reconcile-20260920` | `1dbca3f2729b81072e8b21bd6288d87ed28771e4` | Prior replay/reconciliation and duplicate migration removal backup without a PR. Preserve historical backup. |
| `runtime/19-ci-base-identity-snapshot` | `b209fbca1a07bcbc6bc5714db8978fc199d2afa8` | Earlier identity base snapshot; foundation work was delivered on reviewed replacement branches. Preserve snapshot. |
| `qa/52-deployment-security-gates` | `948e2699797794e4a84f3b6e3297f0a3fe6807e7` | Closed [PR53](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/53) records replacement after its parent was squash merged. [PR54](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/54) delivered the replacement. Keep superseded history. |
| `lead/238-windows-installer` | `989f771b04e9f710103a242d0b6122913f3a127b` | [PR239](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/239) remains Draft for clean Windows lifecycle and signing. Independently reviewed integration and all nine hosted gates passed; accepted ERP main002 still needs integration and renewed gates/review. |
| `codex/266-company-selection` | `b67b4e498902eb743e14d47da5a2a9c80ecf4597` | [PR267](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/267) owns the current company discovery/switching lease. Runtime and independent review of the latest fixes remain required; the current live HANDOFF supersedes this observed HEAD. |

[PR263](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/263) and [PR265](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/265), which were active in earlier audits, subsequently merged after independent frozen implementation/runtime reviews and exact PR/main gates. Main002 Build37801975078/Governance37801976469 and actual SQL/Chromium113396238709 passed. Their feature acceptance does not complete full product #233.

No branch was deleted or force updated. Integrating an archival or superseded branch requires a new demonstrated requirement and reviewed compatibility evidence.

## Subsequent delivery and active leases

PR267/issue266 subsequently merged at main `a75f31a2761ffe0afcbf067425eb8324789b3554` after independent full frozen implementation/proof/final documentation reviews and exact PR/main all8 gates. Main Build37815044494/Governance37815044554/actual113441393702/quality113444186237 pass. The dated130-branch snapshot above remains historical evidence, not a current active-lease list.

PR269/issue268 subsequently delivered at accepted main `3fcfa9a8541491c6a4d349bc31fd02d87d12284e`. Full frozen reviews6073002478/6073139900, exact PR Build37874870695/Gov37874870540 and main Build37875625822/Gov37875625829/all8/actual113643444397/quality113645840920 passed. The issue is closed; administrator implementation has no remaining lease.

## Current audit: 2026-10-09

Paginated GitHub branches and up to400 merged PR heads were refreshed against accepted main3fc. Of132 non-main remote branches,125 exactly match merged PR heads, one is a verified main ancestor, four are the historical/superseded refs above, and two have open Draft leases. No additional eligible implementation was found. No branch was deleted or force updated.

| Active branch | Observed HEAD | Evidence and next gate |
| --- | --- | --- |
| `lead/238-windows-installer` | `22f2866e5e4f95589d81484636ad665c5b4b4a6a` | PR239 integrates acceptedmain3fc; full scoped frozen review6076386907 and exact Build37897294589/Gov37897294556/all9/native113711446847/actual113711446868 PASS. Current pinned unsigned artifact11601020775 and both browser images independently inspected. Keep Draft for actual clean Windows lifecycle and signing. |
| `codex/270-task-history` | `70465e72cee05459dc541cef1a1fa836a6603ba5` | PR271 owns issue270; all8 exact Build37900176803/Gov37900176836/actual113720632034/quality113723223489 PASS, including new native SQL and two-user/two-company shipping Chromium. Root inspected all3 current sanitized images11602387209. Full frozen implementation/security/runtime/all3 images independently APPROVED6077185197; final documentation review/exact PR-main delivery remain. |

Queued272 depends on accepted270 main and has no competing implementation lease. Preserve historical refs; new review and compatibility evidence are required for any future use. Full product233 remains active.

## Latest candidate and lease snapshot

Observed2026-10-09 against accepted main9155b987d028ad097e36b5cfebdc2ee8efe6fcb5. The earlier branch counts remain dated historical evidence. PR271 subsequently delivered atmain420 with reviews6077185197/6077227682 and exact PR/main all8; docs-only PR280 delivered atmain9155 with independent6085798484 and exact PR/main all8. No275-279 implementation was delivered by that plan.

| Current PR | Observed HEAD | Scope and next gate |
| --- | --- | --- |
| 274 / codex/272-task-submission-recovery | 276885809cd6a13fd31c0201efe159c669fec8fe | Full frozen shipping/security/runtime/all4images independently APPROVED6087833026; exact Build37978945377/Gov37978945389/all8/actual113984241483/quality113988410306 PASS. All new native/Chromium recovery and authority controls passed; artifact11641216062 inspected by root/reviewer. Final documentation review/exact PR gates then merge/main closure remain. |
| 239 / lead/238-windows-installer | 22f2866e5e4f95589d81484636ad665c5b4b4a6a | Internally approved/all9; keep Draft for actual clean Windows/UAC/reboot/retention/repair/backup/signing acceptance. |
| 281 / lead/275-auto-it-routing-correction | 0df0c7df4dfe017dbdeb0039c8e49ceb618919ca | Metadata/checks observed only; docs-only candidate has no independent content review or integration approval from this run. Content review is deferred until current274 delivery and user-requested attachment reading. No auto-IT feature delivery is inferred. |

User sequence remains current274 merge/main verification, then reading the attached second-brain document and tracked integration. Full233/customer/production acceptance remains active. No archival ref was deleted, force updated or merged.
