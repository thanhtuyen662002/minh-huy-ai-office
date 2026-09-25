type CustomerSlaPresentationData = Readonly<{
  tierLabel: string;
  priorityLabel: string;
  policyVersion: number;
  effectiveAt: string;
}>;

function ownDataValue(value: object, key: PropertyKey): unknown {
  try {
    const descriptor = Object.getOwnPropertyDescriptor(value, key);
    return descriptor && "value" in descriptor ? descriptor.value : undefined;
  } catch {
    return undefined;
  }
}

function hasCanonicalText(value: unknown): value is string {
  return typeof value === "string" && value.length > 0 && value.trim() === value;
}

function readPresentation(value: unknown): CustomerSlaPresentationData | null {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return null;

  const tierLabel = ownDataValue(value, "tierLabel");
  const priorityLabel = ownDataValue(value, "priorityLabel");
  const policyVersion = ownDataValue(value, "policyVersion");
  const effectiveAt = ownDataValue(value, "effectiveAt");

  if (!hasCanonicalText(tierLabel) || !hasCanonicalText(priorityLabel)) return null;
  if (!Number.isSafeInteger(policyVersion) || (policyVersion as number) <= 0) return null;
  if (!hasCanonicalText(effectiveAt)) return null;

  // Presentation timestamps must use the server contract's canonical UTC instant
  // shape. Date.parse also accepts locale/offset forms, which would let browser
  // data widen the presentation contract.
  const canonicalUtcInstant = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{3})?Z$/;
  if (!canonicalUtcInstant.test(effectiveAt)) return null;

  const effectiveInstant = Date.parse(effectiveAt);
  if (!Number.isFinite(effectiveInstant) || new Date(effectiveInstant).toISOString() !== effectiveAt.replace(/Z$/, effectiveAt.includes(".") ? "Z" : ".000Z")) return null;

  return { tierLabel, priorityLabel, policyVersion: policyVersion as number, effectiveAt };
}

export function CustomerSlaPresentation({ data }: { data: unknown }) {
  const presentation = readPresentation(data);
  if (!presentation) {
    return (
      <section aria-label="Trạng thái SLA" className="rounded-2xl border border-black/10 p-6 dark:border-white/15">
        <h2 className="text-lg font-semibold">SLA & ưu tiên</h2>
        <p role="status" className="mt-2 leading-7 opacity-75">Trạng thái SLA hiện chưa khả dụng từ máy chủ.</p>
      </section>
    );
  }

  return (
    <section aria-label="Trạng thái SLA" className="rounded-2xl border border-black/10 p-6 dark:border-white/15">
      <h2 className="text-lg font-semibold">SLA & ưu tiên</h2>
      <dl className="mt-4 grid gap-3 sm:grid-cols-2">
        <div><dt className="text-sm opacity-60">Gói SLA</dt><dd className="font-medium">{presentation.tierLabel}</dd></div>
        <div><dt className="text-sm opacity-60">Mức ưu tiên</dt><dd className="font-medium">{presentation.priorityLabel}</dd></div>
        <div><dt className="text-sm opacity-60">Phiên bản chính sách</dt><dd className="font-medium">{presentation.policyVersion}</dd></div>
        <div><dt className="text-sm opacity-60">Hiệu lực từ</dt><dd className="font-medium">{presentation.effectiveAt}</dd></div>
      </dl>
    </section>
  );
}
