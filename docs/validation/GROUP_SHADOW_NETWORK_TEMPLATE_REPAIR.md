# Owned shadow network template repair

## Terminal failure and evidence

PR #288 remote HEAD `fcb758f2f2ef8b0d4a2a405b14593b711a40774a` finished
Build 38131017513 with failure; Governance 38131017482 succeeded. Five prerequisite
jobs succeeded. .NET job 114445185337 has the two proven diagnostic export defects
described in `GROUP_SHADOW_TEST_PORTABILITY_REPAIR.md`; local commit
`64234e77e92308d2b7cb32bf1375dd547db3cace` repairs them and its complete four-path
source review is approved in receipt 6108073693. That repair was held unpushed
until the meaningful native run reached its terminal outcome.

Native job 114445185134 failed at the third observation in
`require_shadow_sql_network`: `docker inspect --type container`. Its network
identity and unique running SQL container selection had passed. The full original
native log is 83,303 bytes, SHA256
`3ebea88686273e7844816154e54c0cf9d55e1910d2779be3bb4cede00df97656`.
The frozen 120-marker parser reports 99 required markers once, 21 missing, no
duplicated required marker, 151 overall PASS lines and four terminal failure
markers. No new shadow migration summary or final root/cursor summary was emitted;
the following browser phase did not run. No artifact was emitted. Earlier passing
steps do not qualify this failed HEAD or the new migration.

Required quality job 114452436304 failed with the expected .NET and local-runtime
failures; its other five results were success. Its complete 3,529-byte log has
SHA256 `c3a411e07d02a304146c93ac972fdbfc92c942656065941e6f5922009f3c5867`.
The lead read the complete native and quality logs and retained the .NET diagnosis.

## Portable projection and precise limits of diagnosis

The prior template uses `{{join .Aliases ","}}`. Docker CLI v28.0.4 registers
`join` as `strings.Join`, which requires a typed string slice. Container inspection
can fall back to a raw JSON map, where arrays are interface slices. The hosted
image identified in this run lists Docker client/server 28.0.4. This makes a
template type incompatibility a concrete portability defect and a supported
inference for this failure. The hosted process's stderr and exit value were
intentionally not retained; its exact original diagnostic remains unproven.
An independent bounded loopback metadata probe using the available Docker CLI
29.8.1 accepts both templates. That counterevidence is retained and does not
reproduce the original hosted failure or execute Docker/SQL workloads.

Primary implementation and image references:

- [Docker CLI v28.0.4 template functions](https://github.com/docker/cli/blob/v28.0.4/templates/templates.go)
- [Docker CLI v28.0.4 typed/raw inspection](https://github.com/docker/cli/blob/v28.0.4/cli/command/inspect/inspector.go)
- [Exact hosted image inventory](https://github.com/actions/runner-images/blob/ubuntu24/20261004.327/images/ubuntu/Ubuntu2404-Readme.md)
- [Docker formatting functions](https://docs.docker.com/engine/cli/formatting/)

The repair projects only `json .Aliases`. The existing six-field frame and exact
container ID, running state, project/service labels, full network ID, stderr and
process outcome checks are preserved. Alias JSON is limited to 2,048 characters
before parsing and must be a list of one through eight distinct strings, each
matching the existing 1–128 ASCII character grammar and including `sql`.
Malformed JSON, extra documents, nested or scalar values, empty/null values,
nonstring members, duplicates, excessive count/length and the old CSV frame fail
with fixed diagnostics. No environment, credential or raw inspect result is logged.
No ownership/permission guard or generated operator credential boundary is relaxed.

## Verification and next action

Five focused network controls pass, including all original malformed identity
cases, the exact template argument, additional JSON refusal cases and the valid
eight-alias boundary. Ten original frontier coordinator controls, 144 stack guards
and six terminal controls pass: 165 cold controls in total. The coordinator's
original 13-child, credential isolation, complete graph and cleanup assertions
remain unchanged; its synthetic alias frame now uses JSON. The five network
controls are added to the existing CI cold-control step so they execute before
the long SQL phase. These results exercise the coordinator with controlled process
observations; they are not native Docker, SQL or model acceptance.

Freeze and independently review this repair, then push both source repairs on
the same PR. Own the new exact HEAD through all eight jobs and Governance,
the complete native 120-marker inspection, .NET 3,843 tests, quality results and
all six original browser images if emitted. Inspect any new terminal failure
immediately on this branch. Do not retry the defective old HEAD as infrastructure
noise. Real model evaluation remains `NOT_RUN_UNEVALUATED` through the existing
AI Gateway. Full issue #278, #279, ERP qualification, production #233 and merge
acceptance remain incomplete.
