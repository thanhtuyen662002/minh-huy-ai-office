export type CustomerSlaStatus =
  | Readonly<{ state: "available"; data: Readonly<{ tierLabel: string; priorityLabel: string; policyVersion: number; effectiveAt: string }> }>
  | Readonly<{ state: "forbidden" }>
  | Readonly<{ state: "unavailable" }>;

function ownValue(value: object, key: PropertyKey): unknown {
  try {
    const descriptor = Object.getOwnPropertyDescriptor(value, key);
    return descriptor && "value" in descriptor ? descriptor.value : undefined;
  } catch {
    return undefined;
  }
}

function canonicalText(value: unknown): value is string {
  return typeof value === "string" && value.length > 0 && value.trim() === value;
}

function canonicalUtc(value: unknown): value is string {
  if (!canonicalText(value)) return false;
  const shape = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{3})?Z$/;
  if (!shape.test(value)) return false;
  const instant = Date.parse(value);
  return Number.isFinite(instant) &&
    new Date(instant).toISOString() === value.replace(/Z$/, value.includes(".") ? "Z" : ".000Z");
}

function mapStatus(payload: unknown): CustomerSlaStatus {
  if (payload === null || typeof payload !== "object" || Array.isArray(payload)) return { state: "unavailable" };
  const serviceLabel = ownValue(payload, "serviceLabel");
  const schedulerPriority = ownValue(payload, "schedulerPriority");
  const priorityCeiling = ownValue(payload, "priorityCeiling");
  const policyVersion = ownValue(payload, "policyVersion");
  const effectiveAt = ownValue(payload, "effectiveAt");

  if (!canonicalText(serviceLabel) ||
      !Number.isSafeInteger(schedulerPriority) || (schedulerPriority as number) < 0 ||
      !Number.isSafeInteger(priorityCeiling) || (priorityCeiling as number) < 0 ||
      (schedulerPriority as number) > (priorityCeiling as number) ||
      !Number.isSafeInteger(policyVersion) || (policyVersion as number) <= 0 ||
      !canonicalUtc(effectiveAt)) return { state: "unavailable" };

  return { state: "available", data: {
    tierLabel: serviceLabel,
    priorityLabel: `${schedulerPriority} / ${priorityCeiling}`,
    policyVersion: policyVersion as number,
    effectiveAt,
  }};
}

export async function fetchCustomerSlaStatus(fetcher: typeof fetch = fetch): Promise<CustomerSlaStatus> {
  try {
    const response = await fetcher("/api/sla/status", {
      method: "GET",
      credentials: "same-origin",
      headers: { Accept: "application/json" },
    });
    if (response.status === 401 || response.status === 403) return { state: "forbidden" };
    if (!response.ok) return { state: "unavailable" };
    return mapStatus(await response.json());
  } catch {
    return { state: "unavailable" };
  }
}
