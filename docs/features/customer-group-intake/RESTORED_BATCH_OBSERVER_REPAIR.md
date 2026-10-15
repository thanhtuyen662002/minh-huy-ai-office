# Restored batch observer repair

## Preserved finding

Frozen proof40/commit `4649040` is **WITHHELD** for P2 RBP40-1.
The original store executes raw `SqlQuery<StoredPlan>` containing
`FROM aioffice.GroupAutomaticInputPlans`. EF preserves that spelling inside
the scoped command. Proof40 looked for a bracketed table and left its original
read counter at zero, so its positive commit phase would refuse even after
successful recovery. No actual SQL failure or production isolation defect is
inferred from this offline reproduction.

Independent receipt SHA256
`0b9a21f702aa8388ad90953f242b1068f53a179ba10d11fc63169db3986aaf58`
binds the exact frozen source, an offline EF translation using the actual
private DTO, and the compiled unchanged observer returning zero. Root read
the entire report/envelope and rehashed all60 artifacts. Original40 and its
local full5350 remain immutable; their local tests did not detect this defect.

## Separate repair44

The per-invocation interceptor now recognizes the actual raw original FROM;
accepted-result and terminal reads retain their existing generated EF shapes.
It still resets before fixed-processor execution. Completion must observe
all three categories. Terminal recovery must observe terminal reads while
both original and accepted-result reads remain cold. These counters observe
commands; every production store independently owns SQL authority.

Seven new controls include offline EF translation with the actual private
StoredPlan DTO and full raw SELECT, three missing-reader refusals, and three
terminal recovery cases. The translated unbracketed reader must be recognized;
an always-positive flag or removing the requirement cannot pass the negatives.
No SQL connection opens during translation. Focused7 and full5357
`[6,584,25,4742]` pass locally. The unchanged Python driver10 and executable
guard8 evidence from40 remains separate inherited evidence.

Production source, existing Gateway/client/options/configuration, stored
originals, accepted-result precedence and runtime output markers are unchanged.
Repair44 requires its own exact frozen source/durable review. Future native145
still needs actual SQL execution of the restored-batch aggregate plus all144
old required markers, all8/Governance, full quality results and six originals.
The current remote e8/native144 run cannot qualify this local source.

Actual restored-batch SQL remains NOT_RUN_UNQUALIFIED. First host/model
originals, multiple chunks, edit/recall and own-history invalidated-original
continuation, scanner/shipped Worker, queue/restart/no-click and internal IT
notification remain separate work. Preserve current source/brain/claim and
permission denials. Production catalog is empty; real model evaluation is
NOT_RUN_UNEVALUATED. No main merge or issue278/279/233 completion is implied.
