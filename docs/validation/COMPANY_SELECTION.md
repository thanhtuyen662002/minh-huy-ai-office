# Authoritative company selection runtime evidence

Issue #266 / [PR267](https://github.com/thanhtuyen662002/minh-huy-ai-office/pull/267), under full product #233. Actual runtime and independent frozen review have passed; final evidence HEAD and exact PR/main delivery remain required. This is not production or full product acceptance.

## Verified implementation

- Shipping source: `f03f082800d3324e0574b0b510d74bf6c9d93785`.
- Exact [Build37809426615](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37809426615) and [Governance37809426734](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37809426734): SUCCESS.
- All eight Build jobs: SUCCESS, including actual SQL/OIDC/broker/worker/FE/Chromium `113422110270` and required quality `113425115443`.
- Hosted .NET: 1,086 tests (685 Persistence, 370 Contracts, 25 configuration, six observability). Hosted web: 951 tests including the actual shared Redis coordinator controls; type-check/build PASS.
- Sanitized browser artifact `11564687136`, `browser-workspace-f03f082800d3324e0574b0b510d74bf6c9d93785`, archive 72,696 bytes. Downloaded screenshot visually inspected by the owning lead: authoritative company picker, original company/source restored, empty private chat/draft and no credential/provider body shown. This owner inspection does not substitute for independent review.

## Actual SQL controls

`scripts/smoke-local-stack.py` exercised the shipping Core/API/BFF against the disposable SQL instance:

- Native case and trailing-space provider/subject aliases cannot discover company data. The ordinal identity fence remains after binary SQL candidate filtering; original stored identity is restored and fingerprinted.
- A real second enrollment in another tenant with the same selectable company GUID denies the whole directory at API and BFF, with `no-store` and no private diagnostic. Cleanup removes only the proof's added rows; original identity/authority are checked afterward.
- Real `nvarchar` isolated high/low/reversed/mispaired surrogates are refused at API and BFF. The fixture asserts exact stored bytes; valid supplementary Unicode and a literal U+FFFD survive unchanged.
- Company names are projected as bytes within the same bounded tenant-composite query and strictly decoded. The owned SQL negative found direct string reading repaired the name before managed validation; the raw-byte path closes that boundary without a second lookup.
- Retained source grants, member mutations/immutable audits/concurrency, worker revocation/dead-letter/checkpoints/restart and ERP credential qualification adversaries all pass on the same run.

## Actual Chromium/provider controls

`scripts/smoke-browser-oidc.mjs` used the real provider Code/S256 flow and issued opaque cookies:

- The authoritative directory contains the two enrolled companies and excludes an unassigned owned company; its public response has only company ID/name and `no-store`.
- Fresh visible private chat is submitted through the shipping UI and accepted as a durable task immediately before switching. A private draft and original source are present before the transition.
- Switching A to B issues a fresh SID; presented obsolete binding/SID and the old company selector are refused. The fresh chat/draft/original source option disappear. B lists its own source/member and excludes A's records; A's actual tasks are 404 under B, and submitting A's source under B is denied.
- Reload retains B from the private server session. Switching B to A clears B's private draft, restores A's own source and keeps A's original durable task readable. Original membership fingerprint remains unchanged.
- A cached B option is retained in the DOM while real SQL revokes its membership. Fresh directory removes B; the provider callback returns401 without `Set-Cookie`, preserves A's active SID, and A recovers while B remains refused. Denied callback navigation is settled before root navigation; assertion stages remain distinct.
- Only the proof's added B membership is explicitly restored. A's member panel is visible before switching; B's member panel shows its own member and excludes A's member. Switching back clears B's panel. Existing original memberships/roles/tasks are preserved.
- Retained callback replay/wrong-state/oversized mutation, same-SID role revocation, logout/re-login, Redis expiry/private clearing and task/company boundaries also pass.

## Execution boundary and remaining review

The adversarial stack gate now refuses execution outside the exact owned GitHub CI `RUNNER_TEMP/aioffice-local` directory before reading configuration or invoking Docker. `scripts/test-owned-stack-guard.py` covers the positive owned path plus seven false-flag/retained/nested/unrelated paths without resources; it is a required local-runtime workflow step. Exact proof/workflow checkpoint `7647a3d8208febc1b0310fa2f794e63e76a5e9f4` passed [Build37811540458](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37811540458) and [Governance37811540124](https://github.com/thanhtuyen662002/minh-huy-ai-office/actions/runs/37811540124), all eight jobs including actual runtime `113429366483` and quality `113432454238`. Browser artifact `11566115491` is 72,710 bytes; its 77,661-byte sanitized image was downloaded and independently visually inspected.

Independent frozen2f review closed the earlier no-store and malformed UTF-8 findings through the real BFF helper, including final shared-session revoke, foreign-selector and maximum valid escaped-Unicode controls. It found isolated surrogate names and a chat proof made vacuous by earlier expiry. The reviewer subsequently resumed and independently approved shipping f03 and proof/workflow/docs764: 28 .NET, 92 distinct web and three owned-CI guard tests passed, including independently reproduced high/low surrogate denials, valid Unicode/max-size positives and six real BFF helper probes. Exact764 native SQL identity/ambiguity/stored-byte Unicode and strengthened two-company Chromium logs and sanitized artifacts were inspected. No security/correctness/runtime blocker remained within #266. Final documentation delta and exact PR/main gates still require closure.

Next: close the current live final documentation HEAD's gates and independent delta review, merge the approved #266 scope, and close exact main CI. Full member invitations/role administration, dedicated customer ERP credentials and qualified inventory/stock-movement adapters, production infrastructure, clean Windows/signing and all other #233 requirements remain.
