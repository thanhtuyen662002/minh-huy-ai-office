# Authenticated latest terminal leaf — issue 278 / Draft 288

This checkpoint adds a pure comparison helper. The caller supplies its actual descending TOP(2) snapshot and actual cursor; the helper does not query SQL or establish that this is still the latest snapshot.

An absent chain permits an absent cursor or an exact scoped version-zero/through-zero/null-batch metadata cursor with zero-offset UTC. This pure zero form does not change the existing SQL CHECK or initialize a database cursor. Positive or unchained cursors refuse, including a genuine later terminal batch that cannot prove an earlier completed prefix.

For a root, require exactly one row and no predecessor key. For a successor, require exactly two rows, authenticate the previous complete envelope with its scoped retained key, and compare every predecessor projection actually relied upon: scope, version, through, signed UTC including zero offset, envelope version, KeyId and full-envelope fingerprint. Its other SQL projections are not claimed as reconstructed historical evidence. Authenticate all 26 fields of the latest carrier with the existing row proof and compare cursor version/through/batch/signed UTC exactly.

Both input enumerations and byte arrays are bounded before copies. Extra/null rows, stale or rolled-back cursor, swapped/missing predecessors, changed projections, checksum-only forgery, missing/disposed/wrong scoped keys and private iterator errors refuse with one fixed inner-free diagnostic. Cancellation and actual enumerator disposal remain observable. Returned authenticated metadata is detached.

Private focused verification: 94 PASS = 15 new leaf controls + 79 retained integrity/carrier/key controls, zero fail/skip/compiler warnings; both new C# files pass actual-project format verification. Shared focused verification: 112 PASS = 15 new + 97 retained, zero fail/skip/compiler warnings; both files also pass shared format verification. These compute real HKDF/HMAC over synthetic metadata. They do not prove current SQL rights, original history, trusted genesis, an entire prior prefix, retained-key inventory, key-await rereads, cursor writes or worker DI.

The predecessor MAC supplies an inductive anchor only after an owned store has enforced original history/next-prefix/fresh authority/immutable enrollment before every Sign. Pairwise valid higher-version signatures or SQL FKs alone are insufficient. The owned store remains the next implementation step.

Parent 77f78278d189a9a60b784e3af78fa1388c14c7fc has source/schema approval6106328325 independently ROOT FULL read9424chars, SHA25646b59059a045f234f4f4f0db98acbd11dda4128f3255639e6e9a645a4d8c02a5. Fresh Build38118871590/native114409281277 and Governance38118871592 are owned; Governance is successful, Build is nonterminal at preparation. Earlier013 remains ROOT closed scoped117 with all8/Gov/full logs and six original images. Its actual terminal qualification excludes historical/key/chain/store SQL and this helper.

Actual new history/chain/key/frontier SQL remains NOT_RUN_UNQUALIFIED; actual model through the existing Gateway remains NOT_RUN_UNEVALUATED. Full278/279/233/customer/production/merge remain incomplete.
