# Remote branch integration audit

Observed 2026-10-08 against main `39e668ebebe1f8bd19803199b8db393f12a4edb6`. GitHub branch/PR state and fetched Git ancestry were inspected. This is a dated snapshot; current PR HEAD, acceptance and required workflow results remain authoritative.

Of 128 remote branches outside main, 121 exact branch HEADs match merged PR HEADs. One additional branch is already an ancestor of main. Four branches are historical snapshots or a superseded implementation, and two retain active Draft leases. No further eligible unmerged implementation was identified.

| Branch | Observed HEAD | Evidence and action |
| --- | --- | --- |
| `erp/144-health-evidence-expiry-horizon` | `91bf7a6f33ed72108bb4a27ef7ab07dc7c0d8741` | `git merge-base --is-ancestor` succeeds; no exclusive commits remain. Already integrated. |
| `backup/50-pre-lead-reconcile-20260920` | `d0ed814f317907acc6bcbcf59bf8e395d8b7dac6` | No PR; history records the earlier reconciliation/divergence checkpoints. Preserve historical backup. |
| `backup/51-pre-datasource-reconcile-20260920` | `1dbca3f2729b81072e8b21bd6288d87ed28771e4` | No PR; history contains the prior replay/reconciliation and duplicate migration removal. Preserve historical backup. |
| `runtime/19-ci-base-identity-snapshot` | `b209fbca1a07bcbc6bc5714db8978fc199d2afa8` | No PR; earlier identity base snapshot, with foundation work delivered on reviewed replacement branches. Preserve snapshot. |
| `qa/52-deployment-security-gates` | `948e2699797794e4a84f3b6e3297f0a3fe6807e7` | Closed [PR53](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/53) explicitly records replacement after its parent was squash merged. [PR54](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/54), from `qa/52-deployment-security-gates-main`, merged the replacement at `6595527a720dd1b6afbbcfda615c07ed99c48939`. Keep the superseded branch as history. |
| `lead/238-windows-installer` | `595a587368786af93206ae8f3249ffd2a84cb250` | [PR239](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/239) remains Draft for clean Windows lifecycle and signing. Current verified native/retained gates do not establish those external acceptance requirements. |
| `codex/262-membership-access` | Active HEAD changes during implementation | [PR263](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/263) owns existing member access. New real SQL controls passed; actual browser/exact current gates and full review are required before merge. |

The audit compared GitHub branches with up to 400 merged PRs by branch name and exact HEAD, inspected all unmatched branch PR state/history, and checked Git ancestry for the unmatched candidates. A merge of an archival or superseded branch would require a new demonstrated requirement and reviewed compatibility evidence. No branch was deleted or force updated by this audit.
