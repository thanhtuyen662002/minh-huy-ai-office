"use client";

import { useMemo, useState } from "react";

type CustomerAuditItem = Readonly<{
  auditId: string;
  taskId: string;
  resource: string;
  action: string;
  risk: string;
  authorized: boolean;
  decisionReason: string;
  occurredAtUtc: string;
  executionId: string | null;
}>;

const canonical = (value: unknown): value is string =>
  typeof value === "string" && value.length > 0 && value.trim() === value;

const own = (value: object, key: PropertyKey): unknown => {
  try {
    const descriptor = Object.getOwnPropertyDescriptor(value, key);
    return descriptor && "value" in descriptor ? descriptor.value : undefined;
  } catch {
    return undefined;
  }
};

function readItems(value: unknown): CustomerAuditItem[] | null {
  if (!Array.isArray(value)) return null;
  const length = own(value, "length");
  if (typeof length !== "number" || !Number.isSafeInteger(length) || length < 0 || length > 100) return null;

  const items: CustomerAuditItem[] = [];
  const byId = new Map<string, string>();
  for (let index = 0; index < length; index += 1) {
    const item = own(value, String(index));
    if (item === null || typeof item !== "object") return null;

    const auditId = own(item, "auditId");
    const taskId = own(item, "taskId");
    const resource = own(item, "resource");
    const action = own(item, "action");
    const risk = own(item, "risk");
    const authorized = own(item, "authorized");
    const decisionReason = own(item, "decisionReason");
    const occurredAtUtc = own(item, "occurredAtUtc");
    const executionId = own(item, "executionId");

    if (!canonical(auditId) || !canonical(taskId) || !canonical(resource) || !canonical(action) || !canonical(risk) || !canonical(decisionReason) || !canonical(occurredAtUtc)) return null;
    if (typeof authorized !== "boolean") return null;
    if (executionId !== null && executionId !== undefined && !canonical(executionId)) return null;
    if (Number.isNaN(Date.parse(occurredAtUtc))) return null;

    const normalizedExecutionId = executionId ?? null;
    const fingerprint = JSON.stringify([taskId, resource, action, risk, authorized, decisionReason, occurredAtUtc, normalizedExecutionId]);
    const prior = byId.get(auditId);
    if (prior !== undefined) {
      if (prior !== fingerprint) return null;
      continue;
    }
    byId.set(auditId, fingerprint);
    items.push({ auditId, taskId, resource, action, risk, authorized, decisionReason, occurredAtUtc, executionId: normalizedExecutionId });
  }

  return items.sort((left, right) => right.occurredAtUtc.localeCompare(left.occurredAtUtc) || left.auditId.localeCompare(right.auditId));
}

const normalizeFilter = (value: string) => value.trim().replace(/\s+/g, " ").toLocaleLowerCase("vi-VN");

export function CustomerAuditPresentation({ items: input }: { items: unknown }) {
  const [filter, setFilter] = useState("");
  const items = useMemo(() => readItems(input), [input]);
  const normalizedFilter = normalizeFilter(filter);
  const visibleItems = useMemo(() => {
    if (!items || !normalizedFilter) return items;
    return items.filter((item) => normalizeFilter(`${item.action} ${item.resource}`).includes(normalizedFilter));
  }, [items, normalizedFilter]);

  if (!items || !visibleItems) {
    return <section role="alert"><h2>Không thể mở nhật ký hoạt động</h2><p>Dữ liệu nhật ký không hợp lệ hoặc không còn nằm trong phạm vi máy chủ đã xác thực.</p></section>;
  }
  if (items.length === 0) {
    return <section aria-label="Nhật ký hoạt động"><h2>Nhật ký hoạt động</h2><p>Chưa có hoạt động nào.</p></section>;
  }

  return <section aria-label="Nhật ký hoạt động">
    <h2>Nhật ký hoạt động</h2>
    <label htmlFor="customer-audit-filter">Lọc theo hành động hoặc tài nguyên</label>
    <input id="customer-audit-filter" type="search" value={filter} onChange={(event) => setFilter(event.currentTarget.value)} autoComplete="off" />
    {filter ? <button type="button" onClick={() => setFilter("")}>Xóa bộ lọc</button> : null}
    <p role="status" aria-live="polite">Hiển thị {visibleItems.length} / {items.length} hoạt động</p>
    {visibleItems.length === 0 ? <p>Không có hoạt động phù hợp với bộ lọc.</p> : <ol>{visibleItems.map((item) => <li key={item.auditId}><p>{item.action} · {item.resource}</p><p>{item.authorized ? "Được phép" : "Bị từ chối"} · {item.risk}</p><p>{item.decisionReason}</p><time dateTime={item.occurredAtUtc}>{item.occurredAtUtc}</time>{item.executionId ? <p>Mã thực thi: {item.executionId}</p> : null}</li>)}</ol>}
  </section>;
}
