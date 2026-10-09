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

The current open Draft leases are installer239 and administrator269. Installer239 includes accepted main at integration4b and final documentation723 with all9 hosted jobs green and full scoped independent integration/runtime/docs approval receipt6072831478; clean Windows and signing remain. Administrator269 owns issue268 from accepted maina75; d85 all8 and actual SQL/two-session Chromium including Core-to-BFF committed header/body loss/one-audit replay pass. Final frozen review/documentation/exact PR/main closure remains before merge eligibility. Queued270 task history has no implementation lease yet. Preserve the historical backup/superseded refs; no new requirement authorizes integrating them.
