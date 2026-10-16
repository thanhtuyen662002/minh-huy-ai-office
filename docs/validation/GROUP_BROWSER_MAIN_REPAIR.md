# Original restored-source receipt — main closure

Issue #286 follows merged #284/#285 and retains parent #277 ownership.

## Observed evidence

Approved exact PR285 `f7b9d662529a22b233e75e512a41d657959213cd` passed
Build38039993746/Gov38039993829/all8/actual114178146673/quality114181327516.
Root and independent full review6096145298 inspected current logs and all six
original artifact11666290618 images; selected80 markers/missing0,136 overall
PASS,2027.NET/1334web. Scoped source review6095938448 remains valid.

Merged main `aeb534baebc1e13d59a174b644f5cc7f8e8eb4a1` has an identical complete
tree. Exact Build38041707509/actual114183088426/quality114185755608 failed;
Gov38041707526 and all six prerequisites passed. Native step12 passed both the
shipping application restart and literal pending persistent RabbitMQ restart.

Chromium failed at `submission-historical-source-restored202-body`. The original
response status202 passed, then Playwright `restoredResponse.json()` failed
before receipt comparison/worker assertions. Both preceding real Core committed202
header/body-loss and stable original replay controls passed. Current logs do not
identify whether body acquisition, JSON parsing or request abort caused the error.
Do not attribute it to source restoration, expiration or a proven shipping defect.

## Narrow change and controls

The historical restored-source caller now uses its existing `requiredReceipt`
reader, with the historical phase prefix. Original completed stream and body
acquisition each retain their existing20s bound; body must be nonempty,<=4096bytes,
fatal UTF8 and JSON. Only fixed refusal categories reach logs.

Status202, original operation/fingerprint equality, completed worker and one exact
graph remain mandatory. No replacement receipt, new request, retry, API fallback
or wider timeout is introduced. All shipping src/apps/migrations/workflow and
native reference/restart proof bytes are unchanged.

Local36 Node controls pass: retained23 plus13 new controls execute the actual
nested reader and historical caller through inert browser/SQL callbacks. They
cover a completed positive, non202, failed/stalled stream, stalled body, empty/
oversized/invalid UTF8/invalid JSON/unavailable/failed body, wrong receipt and
changed-graph refusal. The inert timer preserves observed20000ms requests and
compresses only unit execution; it does not change native timeout policy.

These controls do not establish a browser root cause or native acceptance.
Frozen review, exact new PR all8/Gov/native/Chromium and merged-main closure are
required before #277 closes or #278 implementation begins. Private synthetic
120 primary/40 independent proposed labels are unevaluated preparation; #278,
#279, installer239, readonly customer ERP, live providers and full233 remain.
