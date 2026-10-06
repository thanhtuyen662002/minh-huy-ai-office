"use client";

import { FormEvent, useEffect, useRef, useState } from "react";
import { LocalDataSource, parseDataSources } from "../lib/local-ai-workspace";
import { isRegistrationInput, parseRegistrationPage, RegistrationInput, RegistrationOption } from "../lib/source-registration";

type Props = {
  companyId: string; generation: number;
  isCurrent: (generation: number) => boolean;
  validate: (generation: number) => Promise<boolean>;
  request: (generation: number, url: string, init?: RequestInit) => Promise<Response>;
  onRegistered: (sources: readonly LocalDataSource[]) => void;
  onCancel: () => void;
  onUnauthorized: () => void;
};

export function SourceRegistrationPanel({ companyId, generation, isCurrent, validate, request, onRegistered, onCancel, onUnauthorized }: Props) {
  const [options, setOptions] = useState<readonly RegistrationOption[]>([]);
  const [bindingId, setBindingId] = useState("");
  const [logicalName, setLogicalName] = useState("");
  const [environment, setEnvironment] = useState("Production");
  const [purpose, setPurpose] = useState("");
  const [concurrency, setConcurrency] = useState("2");
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [attempt, setAttempt] = useState<RegistrationInput | null>(null);
  const mounted = useRef(false);
  const lock = useRef(false);
  const current = () => mounted.current && isCurrent(generation);
  const selector = `companyId=${encodeURIComponent(companyId)}`;

  async function load() {
    if (lock.current || !current()) return;
    lock.current = true;
    setLoading(true); setError("");
    try {
      if (!await validate(generation) || !current()) return;
      const items: RegistrationOption[] = [];
      let offset = 0;
      while (true) {
        const response = await request(generation, `/api/local/data-sources/registration-options?${selector}&offset=${offset}&limit=100`, { cache: "no-store" });
        if (!current()) return;
        if (response.status === 401) { onUnauthorized(); return; }
        if (!response.ok) throw new Error("Không tải được nguồn đã cấp quyền. Hãy thử lại.");
        const page = parseRegistrationPage(await response.json());
        if (!current()) return;
        if (!page || page.offset !== offset || page.limit !== 100 || (page.hasMore && page.items.length === 0))
          throw new Error("Danh sách nguồn không hợp lệ.");
        items.push(...page.items);
        if (items.length > 1000 || new Set(items.map(item => item.bindingId)).size !== items.length)
          throw new Error("Danh sách nguồn đã thay đổi. Hãy tải lại.");
        if (!page.hasMore) break;
        offset += page.items.length;
      }
      if (!await validate(generation) || !current()) return;
      setOptions(items); setBindingId(items[0]?.bindingId ?? "");
    } catch (problem) {
      if (current()) { setOptions([]); setBindingId(""); setError(problem instanceof Error ? problem.message : "Không tải được nguồn."); }
    } finally {
      lock.current = false;
      if (current()) setLoading(false);
    }
  }
  useEffect(() => {
    mounted.current = true;
    void load();
    return () => { mounted.current = false; };
    // The parent keys this panel by company/session generation. It is never
    // allowed to retain an operation across an authority change.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (lock.current || !current()) return;
    const option = options.find(item => item.bindingId === bindingId);
    const payload = attempt ?? {
      bindingId, bindingVersion: option?.versionToken ?? "", operationId: crypto.randomUUID(),
      logicalName: logicalName.trim(), environment: environment.trim(), purpose: purpose.trim(), maxConcurrency: Number(concurrency),
    };
    if (!isRegistrationInput(payload)) { setError("Chọn nguồn, nhập đủ thông tin và số đồng thời từ 1 đến 1024."); return; }
    lock.current = true;
    setSaving(true); setError("");
    try {
      if (!await validate(generation) || !current()) return;
      // Freeze the exact body before sending. Even a lost successful response
      // must be retried with the same operation ID and metadata.
      setAttempt(payload);
      const response = await request(generation, `/api/local/data-sources/read-only-registration?${selector}`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload),
      });
      if (!current()) return;
      if (response.status === 401) { onUnauthorized(); return; }
      if (response.status === 403) {
        await validate(generation);
        if (current()) { setAttempt(null); setOptions([]); setBindingId(""); throw new Error("Quyền hoặc nguồn đã bị thu hồi. Hãy tải lại danh sách."); }
        return;
      }
      if (!response.ok) {
        if (response.status === 400 || response.status === 409) setAttempt(null);
        throw new Error(response.status === 409 ? "Tên nguồn hoặc thao tác bị trùng. Hãy làm mới danh sách nguồn."
          : "Chưa xác nhận được đăng ký. Thử lại sẽ dùng cùng mã thao tác.");
      }
      const returned = parseDataSources([await response.json()])?.[0];
      if (!current()) return;
      if (!returned || !returned.allowRead || returned.allowWrite || returned.kind !== "sql-server")
        throw new Error("Chưa xác nhận được đăng ký. Hãy thử lại.");
      if (!await validate(generation) || !current()) return;
      const refreshed = await request(generation, `/api/local/data-sources?${selector}`, { cache: "no-store" });
      if (!current()) return;
      if (refreshed.status === 401) { onUnauthorized(); return; }
      const sources = refreshed.ok ? parseDataSources(await refreshed.json()) : null;
      if (!current()) return;
      if (!await validate(generation) || !current()) return;
      if (!sources?.some(source => source.id === returned.id)) throw new Error("Máy chủ đã nhận đăng ký nhưng chưa xác nhận lại được danh sách. Hãy thử lại.");
      onRegistered(sources);
    } catch (problem) {
      if (current()) setError(problem instanceof Error ? problem.message : "Chưa xác nhận được đăng ký. Hãy thử lại.");
    } finally {
      lock.current = false;
      if (current()) setSaving(false);
    }
  }
  const frozen = saving || Boolean(attempt);
  return (
    <form aria-label="Đăng ký nguồn chỉ đọc" onSubmit={submit} className="mt-6 rounded-2xl border border-indigo-200 p-5 dark:border-indigo-400/25">
      <h3 className="font-semibold">Thêm nguồn ERP chỉ đọc</h3>
      <p className="mt-2 text-sm text-slate-500">Chọn kết nối đã được quản trị viên hạ tầng cấp quyền cho công ty này.</p>
      {error ? <p role="alert" className="mt-3 text-sm text-rose-600">{error}</p> : null}
      {!loading && options.length === 0 ? <p className="mt-3 text-sm">Chưa có kết nối được cấp quyền. Liên hệ quản trị viên hạ tầng rồi tải lại.</p> : null}
      <div className="mt-4 grid gap-4 sm:grid-cols-2">
        <label className="text-sm">Kết nối được cấp quyền<select aria-label="Kết nối được cấp quyền" value={bindingId} disabled={frozen || loading} onChange={event => setBindingId(event.target.value)} className="mt-1 w-full rounded-lg border p-2 dark:bg-slate-900">
          <option value="">{loading ? "Đang tải…" : "Chọn kết nối"}</option>
          {options.map(option => <option key={option.bindingId} value={option.bindingId}>{option.label}</option>)}
        </select></label>
        <label className="text-sm">Tên nguồn mới<input aria-label="Tên nguồn mới" value={logicalName} maxLength={200} disabled={frozen} onChange={event => setLogicalName(event.target.value)} className="mt-1 w-full rounded-lg border p-2 dark:bg-slate-900" /></label>
        <label className="text-sm">Môi trường nguồn<input aria-label="Môi trường nguồn" value={environment} maxLength={50} disabled={frozen} onChange={event => setEnvironment(event.target.value)} className="mt-1 w-full rounded-lg border p-2 dark:bg-slate-900" /></label>
        <label className="text-sm">Mục đích nguồn mới<input aria-label="Mục đích nguồn mới" value={purpose} maxLength={200} disabled={frozen} onChange={event => setPurpose(event.target.value)} className="mt-1 w-full rounded-lg border p-2 dark:bg-slate-900" /></label>
        <label className="text-sm">Số tác vụ đồng thời mới<input aria-label="Số tác vụ đồng thời mới" type="number" min={1} max={1024} value={concurrency} disabled={frozen} onChange={event => setConcurrency(event.target.value)} className="mt-1 w-full rounded-lg border p-2 dark:bg-slate-900" /></label>
      </div>
      <div className="mt-4 flex flex-wrap gap-3">
        <button type="submit" disabled={loading || saving || (!attempt && options.length === 0)} className="rounded-lg bg-indigo-600 px-4 py-2 text-sm text-white disabled:opacity-50">{saving ? "Đang đăng ký…" : attempt ? "Thử lại đăng ký" : "Đăng ký nguồn"}</button>
        <button type="button" disabled={loading || frozen} onClick={() => void load()} className="rounded-lg border px-4 py-2 text-sm">Tải lại kết nối</button>
        <button type="button" disabled={saving} onClick={onCancel} className="rounded-lg border px-4 py-2 text-sm">Đóng đăng ký</button>
      </div>
    </form>
  );
}
