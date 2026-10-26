"use client";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { parseMemberPage, type CompanyMember } from "../lib/company-members";
import { type CustomerRequestDetail as Detail, type CustomerRequestPage, type CustomerRequestPatch,
  parseCustomerRequest, parseCustomerRequestDetail, parseCustomerRequestPage, requestConflict, requestStatuses } from "../lib/customer-requests";
import { CustomerRequestDetail, requestButton as button, requestTime } from "./customer-request-detail";

type Props = { tenantId: string; companyId: string; userId: string; generation: number;
  isCurrent: (generation: number) => boolean; validate: (generation: number) => Promise<boolean>;
  request: (generation: number, url: string, init?: RequestInit) => Promise<Response>; onUnauthorized: () => void };
type Selection = { status: string; customer: string; offset: number; code?: string };
type State = { scope: string; page: CustomerRequestPage | null; detail: Detail | null; members: CompanyMember[] | null;
  memberError: string; loading: boolean; saving: boolean; conflict: boolean; error: string; notice: string; selection: Selection };
const initialSelection = (): Selection => ({ status: "", customer: "", offset: 0 });
const empty = (scope: string, selection = initialSelection()): State => ({ scope, selection, page: null, detail: null, members: null,
  memberError: "", loading: false, saving: false, conflict: false, error: "", notice: "" });
class ReadFailure extends Error { constructor(readonly status: number) { super("Customer request unavailable"); } }

export function CustomerRequestPanel({ tenantId, companyId, userId, generation, isCurrent, validate, request, onUnauthorized }: Props) {
  const scope = `${tenantId}:${companyId}:${userId}:${generation}`;
  const [state, setState] = useState(() => empty(scope));
  const [statusFilter, setStatusFilter] = useState(""), [customerFilter, setCustomerFilter] = useState("");
  const live = useRef(scope); live.current = scope;
  const mounted = useRef(false), serial = useRef(0), controller = useRef<AbortController | null>(null), saving = useRef<object | null>(null);
  const current = (value: number) => mounted.current && live.current === scope && serial.current === value && isCurrent(generation);
  function cancel() { serial.current++; controller.current?.abort(); controller.current = null; }
  async function confirm(value: number) {
    const valid = await validate(generation);
    if (!valid && current(value)) setState(empty(scope));
    return valid && current(value);
  }
  async function get(url: string, value: number, signal: AbortSignal, init?: RequestInit) {
    if (!await confirm(value)) return null;
    const response = await request(generation, url, { ...init, cache: "no-store", signal });
    if (!current(value)) return null;
    if (response.status === 401) { setState(empty(scope)); onUnauthorized(); return null; }
    if (!response.ok) throw new ReadFailure(response.status);
    const body: unknown = await response.json();
    return await confirm(value) ? body : null;
  }
  const root = `/api/local/customer-requests`, companyQuery = `companyId=${encodeURIComponent(companyId)}`;
  async function directory(value: number, signal: AbortSignal) {
    try {
      const members: CompanyMember[] = [];
      for (let offset = 0; offset <= 1000; offset += 100) {
        const body = await get(`/api/local/company/members?${companyQuery}&offset=${offset}&limit=100`, value, signal);
        if (body === null || !current(value)) return;
        const page = parseMemberPage(body);
        if (!page || page.companyId.toLowerCase() !== companyId.toLowerCase() || page.offset !== offset || page.limit !== 100
          || page.items.some(member => members.some(old => old.userId === member.userId))) throw new Error();
        members.push(...page.items);
        if (!page.hasMore) { setState(old => ({ ...old, members })); return; }
      }
      throw new Error(); // Never offer an incomplete directory as the full set of assignees.
    } catch (error) {
      if (current(value)) setState(old => ({ ...old, members: null, memberError: error instanceof ReadFailure && error.status === 403
        ? "Bạn không có quyền xem danh bạ thành viên. Vẫn có thể đổi trạng thái; quản trị viên có thể phân công người phụ trách."
        : "Không tải được đầy đủ danh bạ thành viên. Tải lại phiếu để thử lại; vẫn có thể đổi trạng thái." }));
    }
  }
  async function read(selection: Selection, notice = "") {
    if (!mounted.current || !isCurrent(generation) || saving.current) return;
    cancel(); const value = serial.current, abort = new AbortController(); controller.current = abort;
    setState({ ...empty(scope, selection), loading: true, notice });
    const members = directory(value, abort.signal);
    try {
      const query = new URLSearchParams({ companyId, offset: String(selection.offset), limit: "25" });
      if (selection.status) query.set("status", selection.status);
      if (selection.customer) query.set("customer", selection.customer);
      const body = await get(selection.code ? `${root}/${encodeURIComponent(selection.code)}?${companyQuery}` : `${root}?${query}`, value, abort.signal);
      if (body === null || !current(value)) return;
      const detail = selection.code ? parseCustomerRequestDetail(body, selection.code) : null;
      const page = selection.code ? null : parseCustomerRequestPage(body, selection.offset, 25);
      if (!detail && !page) throw new Error();
      setState(old => ({ ...old, detail, page, loading: false }));
    } catch (error) {
      if (current(value)) {
        cancel(); // Also discard a directory response racing a ticket refusal.
        setState({ ...empty(scope, selection), notice, error: error instanceof ReadFailure && error.status === 403
          ? "Bạn không có quyền xem yêu cầu khách hàng của công ty này."
          : error instanceof ReadFailure && error.status === 404 ? "Phiếu không còn khả dụng hoặc tính năng chưa được bật."
            : "Không tải được yêu cầu khách hàng. Hãy tải lại để thử lại." });
      }
    } finally {
      if (current(value)) setState(old => ({ ...old, loading: false }));
      await members;
    }
  }
  async function save(patch: CustomerRequestPatch) {
    if (saving.current || state.loading || state.conflict || !state.detail || !isCurrent(generation)) return;
    const operation = {}; saving.current = operation; cancel();
    const value = serial.current, abort = new AbortController(); controller.current = abort;
    const code = state.detail.request.code;
    setState(old => ({ ...old, saving: true, error: "", notice: "" }));
    try {
      const body = await get(`${root}/${encodeURIComponent(code)}?${companyQuery}`, value, abort.signal,
        { method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify(patch) });
      if (body === null || !current(value)) return;
      if (!parseCustomerRequest(body, code)) throw new Error();
      saving.current = null;
      await read(state.selection, "Đã lưu thay đổi.");
    } catch (error) {
      if (!current(value)) return;
      if (error instanceof ReadFailure && [403, 404].includes(error.status)) {
        setState({ ...empty(scope, state.selection), error: error.status === 403 ? "Bạn không còn quyền cập nhật phiếu này."
          : "Phiếu không còn khả dụng. Hãy tải lại danh sách." });
      } else {
        setState(old => ({ ...old, saving: false, conflict: !(error instanceof ReadFailure && error.status === 400),
          error: error instanceof ReadFailure && error.status === 409 ? requestConflict
            : error instanceof ReadFailure && error.status === 400 ? "Thông tin cập nhật không hợp lệ. Kiểm tra trạng thái và người phụ trách."
              : "Chưa xác nhận được thay đổi đã lưu. Tải lại phiếu để xem trạng thái mới trước khi cập nhật tiếp." }));
      }
    } finally {
      if (saving.current === operation) saving.current = null;
      if (current(value)) setState(old => ({ ...old, saving: false }));
    }
  }
  const latest = useRef(read); latest.current = read;
  useEffect(() => {
    mounted.current = true; saving.current = null; setStatusFilter(""); setCustomerFilter("");
    void latest.current(initialSelection());
    return () => { mounted.current = false; cancel(); };
    // Scope replacement clears all data and cancels both reads and writes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scope]);
  const visible = state.scope === scope && isCurrent(generation) ? state : empty(scope);
  const disabled = visible.loading || visible.saving;
  function filter(event: FormEvent) {
    event.preventDefault();
    void read({ status: statusFilter, customer: customerFilter.trim(), offset: 0 });
  }
  return <section aria-label="Yêu cầu khách hàng" aria-busy={disabled}
    className="min-w-0 space-y-4 rounded-2xl border border-slate-200 bg-white p-5 dark:border-white/10 dark:bg-[#11182a]">
    <div className="flex flex-wrap items-center justify-between gap-3"><h2 className="text-lg font-semibold">Yêu cầu khách hàng</h2>
      <button type="button" className={button} disabled={disabled} onClick={() => void read(visible.selection)}>Tải lại phiếu</button></div>
    <p role="note" className="rounded-xl bg-amber-50 p-3 text-sm text-amber-900 dark:bg-amber-400/10 dark:text-amber-200">
      AI tóm tắt từ Zalo · Nội dung chưa được nhân viên xác nhận.</p>
    {visible.loading ? <p role="status">Đang tải yêu cầu khách hàng…</p> : null}
    {visible.saving ? <p role="status">Đang lưu thay đổi…</p> : null}
    {visible.error ? <p role="alert" className="text-sm text-rose-600 dark:text-rose-200">{visible.error}</p> : null}
    {visible.notice ? <p role="status">{visible.notice}</p> : null}
    {visible.selection.code ? <button type="button" className={button} disabled={disabled}
      onClick={() => void read({ ...visible.selection, code: undefined })}>Về danh sách</button> : <>
      <form onSubmit={filter} aria-label="Lọc yêu cầu khách hàng" className="flex flex-wrap items-end gap-3">
        <label className="grid gap-1 text-sm">Lọc trạng thái<select className={button} value={statusFilter} disabled={disabled}
          onChange={event => setStatusFilter(event.target.value)}><option value="">Tất cả trạng thái</option>
          {Object.entries(requestStatuses).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
        <label className="grid gap-1 text-sm">Khách hàng<input className={button} type="search" maxLength={200} value={customerFilter}
          disabled={disabled} onChange={event => setCustomerFilter(event.target.value)} placeholder="Tên khách hàng" /></label>
        <button type="submit" className={button} disabled={disabled}>Lọc phiếu</button>
      </form>
      <p className="text-sm text-slate-500">Sắp xếp mới nhất trước.</p>
    </>}
    {visible.page ? <>
      {visible.page.items.length ? <div className="overflow-x-auto"><table className="w-full text-left text-sm" aria-label="Danh sách yêu cầu khách hàng">
        <thead><tr>{["Mã phiếu", "Khách hàng", "Nhóm Zalo", "Tóm tắt", "Trạng thái", "Người phụ trách", "Ngày tạo"].map(label => <th scope="col" className="p-3" key={label}>{label}</th>)}</tr></thead>
        <tbody>{visible.page.items.map(item => <tr key={item.id} className="border-t border-slate-100 align-top dark:border-white/10">
          <td className="p-3"><button className={`${button} whitespace-nowrap`} type="button" disabled={disabled}
            onClick={() => void read({ ...visible.selection, code: item.code })}>{item.code}</button></td>
          <td className="p-3 break-words">{item.customerName}</td><td className="p-3 break-words">{item.sourceGroupName}</td>
          <td className="max-w-sm p-3"><p className="line-clamp-3 whitespace-pre-wrap break-words">{item.summary}</p></td>
          <td className="p-3">{requestStatuses[item.status]}</td><td className="p-3 break-all">{item.assigneeUserId === null ? "Chưa phân công"
            : visible.members?.find(member => member.userId === item.assigneeUserId)?.displayName ?? item.assigneeUserId}</td><td className="p-3">{requestTime(item.createdAt)}</td>
        </tr>)}</tbody></table></div> : <p>Chưa có yêu cầu khách hàng phù hợp với bộ lọc.</p>}
      <div className="flex flex-wrap items-center gap-3"><button type="button" className={button} disabled={disabled || visible.selection.offset === 0}
        onClick={() => void read({ ...visible.selection, offset: visible.selection.offset - 25 })}>Trang trước</button>
        <span>Trang {Math.floor(visible.selection.offset / 25) + 1}</span>
        <button type="button" className={button} disabled={disabled || !visible.page.hasMore || visible.selection.offset >= 100000}
          onClick={() => void read({ ...visible.selection, offset: visible.selection.offset + 25 })}>Trang sau</button></div>
      {visible.memberError ? <p role="note" className="text-sm text-amber-700 dark:text-amber-200">{visible.memberError}</p> : null}
    </> : null}
    {visible.detail ? <CustomerRequestDetail key={`${scope}:${visible.detail.request.code}:${visible.detail.request.rowVersion}`}
      detail={visible.detail} members={visible.members} memberError={visible.memberError} disabled={disabled || visible.conflict}
      onSave={patch => void save(patch)} /> : null}
  </section>;
}
