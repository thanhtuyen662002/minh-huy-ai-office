# Group connector qualification: live evidence remains unverified

Issue276/PR282 follows the user-provided automatic IT workflow requirements and
is delivered on main4cfeda58. Issue277/PR283 adds the owned synthetic core runtime;
its current acceptance evidence is in [GROUP_INGRESS.md](GROUP_INGRESS.md).
No account login, controlled receive/send or live activation occurred. Core contracts
can progress using an explicitly synthetic connector; synthetic evidence cannot
enable a live receive/send profile.

## Pinned reference candidate

`zca-js`2.2.0, upstream commit `d22c28fcabd70375c144980e9e310c38f8142590`, inspected
2026-10-10 through upstream GitHub/package.json and maintainer documentation.
[Source](https://github.com/RFS-ADRENO/zca-js/tree/d22c28fcabd70375c144980e9e310c38f8142590),
[message](https://zca-js.tdung.com/en/listeners/message),
[send](https://zca-js.tdung.com/en/apis/sendMessage).
This is a pinned inspection candidate, not an installed product dependency or
evidence that the user's account works. Upstream describes an unofficial personal
account API and a single web listener; account restrictions and listener collisions
must be handled explicitly. No official personal-account sandbox is assumed.

| Observation | Actual account status | Required profile |
| --- | --- | --- |
| Permitted group text receive | Unverified | Receive |
| Exact provider message identity | Unverified | Receive |
| Exact sender identity | Unverified | Receive |
| Reply identity | Unverified | Optional; retain coverage limits |
| Self/outbound echo correlation | Unverified | Receive |
| Listener collision detection | Unverified | Receive |
| Observable disconnect/coverage gaps | Unverified | Receive |
| Verified edits | Unverified | Optional; no fabricated revision support |
| Verified recalls | Unverified | Optional; no fabricated deletion support |
| Current group membership | Unverified | Receive and send |
| Internal IT text sending | Unverified | Send |
| Provider acceptance references | Unverified | Send |
| Possibly accepted send reconciliation | Unverified | Send |

The executable policy binds scope/account/provider/package/commit and copies an
immutable set of observations. Mandatory support requires controlled evidence IDs
and fresh UTC observations (initial30day ceiling). Missing/unsupported/unverified
requirements, artifact/account aliases and synthetic qualification all refuse live
profiles. A provider response is not proof of recipient delivery or exactly-once.

## Code evidence and remaining integration

Initial routing26 tests/full422 Contracts and all8 initial840 PR gates PASS, including
retained actual SQL/OIDC/broker/worker/Chromium. Independent initial review ran33
tests including7 own identity/epoch/limit/version probes with no blocker. Additional
qualification/workflow/source/report contracts run locally:66 new/full462 Contracts
PASS. Full frozen ee30 review independently APPROVED at receipt6088762933,
with475 passing tests including13 own counterexamples, independent Python hash
golden and YAML/diff checks. Accepted main281/1b0 has all8 gates PASS
(Build37985845222/Gov37985845186/actual114007505342/quality114011846100).
Frozen ee30 Build37986918810/Gov37986918797/all8/actual114011097674/quality114014463755
PASS. PR282 subsequently integrated the contract checkpoint on main4cfeda58.
That issue276 checkpoint delivered no group SQL store, migration, ingress endpoint,
queue pipeline, memory worker or sender. Issue277 implements and proves the owned
SQL ingress/listener/spool/reference queue/read path; its synthetic evidence cannot
qualify this table's live account observations. Issues278/279 still must deliver
the automatic brain/notes and separate internal IT no-click reporting flow.
