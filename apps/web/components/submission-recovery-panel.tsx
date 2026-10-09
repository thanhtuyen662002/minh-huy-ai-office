"use client";
import { useEffect, useRef, useState } from "react";
import { verifiedSubmissionIntent, verifiedSubmissionPage, type SubmissionIntent, type SubmissionPage } from "../lib/task-submission-intent";
import { readSubmissionPayload } from "./use-task-submission";

type Props = { companyId: string; userId: string; generation: number; isCurrent: (generation: number) => boolean;
  validate: (generation: number) => Promise<boolean>; request: (generation: number, url: string, init?: RequestInit) => Promise<Response>;
  onUnauthorized: () => void; onResume: (intent: SubmissionIntent) => boolean; disabled?: boolean };
const button = "rounded-lg border border-slate-200 px-3 py-2 text-sm disabled:opacity-50 dark:border-white/15";
const labels = ["Đã lưu, chưa vào hàng đợi", "Đã nhận · xem Công việc", "Đã hết hạn gửi", "Cần kiểm tra thông tin"];

/** Private owner reads only. Selecting an intent never creates or resubmits a task. */
export function SubmissionRecoveryPanel({ companyId, userId, generation, isCurrent, validate, request, onUnauthorized, onResume, disabled }: Props) {
  const [page, setPage] = useState<SubmissionPage | null>(null), [offset, setOffset] = useState(0);
  const [loading, setLoading] = useState(false), [error, setError] = useState("");
  const scope = `${companyId}:${userId}:${generation}`, scopeRef = useRef(scope); scopeRef.current = scope;
  const mounted = useRef(false), serial = useRef(0), active = useRef<AbortController | null>(null);
  const current = (value: number) => mounted.current && scopeRef.current === scope && value === serial.current && isCurrent(generation);
  function cancel() { serial.current++; active.current?.abort(); active.current = null; }
  async function read(nextOffset: number, operationId?: string) {
    if (!mounted.current || !isCurrent(generation) || operationId && disabled) return;
    cancel(); const value = serial.current, controller = new AbortController(); active.current = controller;
    setLoading(true); setError(""); if (!operationId) { setPage(null); setOffset(nextOffset); }
    async function confirm() {
      if (!current(value)) return false;
      const valid = await validate(generation);
      if (!current(value)) return false;
      if (!valid) setPage(null);
      return valid;
    }
    try {
      if (!await confirm()) return;
      const signal = AbortSignal.any([controller.signal, AbortSignal.timeout(10000)]);
      const path = operationId ? `/api/local/tasks/intents/${operationId}?companyId=${encodeURIComponent(companyId)}`
        : `/api/local/tasks/intents?companyId=${encodeURIComponent(companyId)}&offset=${nextOffset}&limit=25`;
      const response = await request(generation, path, { cache: "no-store", signal });
      if (!current(value)) return;
      if (response.status === 401) { setPage(null); onUnauthorized(); return; }
      if (response.status === 403) { setPage(null); if (await confirm()) setError("Không còn quyền xem yêu cầu. Hãy xác nhận lại quyền truy cập công ty."); return; }
      if (response.status === 404 && operationId) { if (await confirm()) setError("Chưa tìm thấy yêu cầu thuộc tài khoản của bạn. Hãy tải lại danh sách."); return; }
      if (!response.ok) throw new Error("Unavailable");
      const payload = await readSubmissionPayload(response, signal, operationId ? 65536 : 1048576);
      if (!current(value)) return;
      if (operationId) {
        const intent = await verifiedSubmissionIntent(payload);
        if (!current(value)) return;
        if (!intent || intent.companyId.toLowerCase() !== companyId.toLowerCase() || intent.operationId.toLowerCase() !== operationId.toLowerCase()) throw new Error("Invalid intent");
        if (!await confirm()) return;
        if (intent.state === 3) { setError("Thông tin yêu cầu cần được kiểm tra. Hãy liên hệ quản trị viên."); return; }
        if (!onResume(intent) && current(value)) setError("Bạn đang giữ một yêu cầu khác. Quay lại Trợ lý AI để kiểm tra hoặc chủ động tạo yêu cầu mới trước.");
      } else {
        const received = await verifiedSubmissionPage(payload);
        if (!current(value)) return;
        if (!received || received.companyId.toLowerCase() !== companyId.toLowerCase() || received.offset !== nextOffset || received.limit !== 25) throw new Error("Invalid page");
        if (await confirm()) setPage(received);
      }
    } catch {
      if (current(value)) { setPage(null); if (await confirm()) setError("Chưa tải được yêu cầu đã lưu. Hãy thử tải lại; thao tác này không gửi công việc."); }
    } finally { if (active.current === controller) active.current = null; if (current(value)) setLoading(false); }
  }
  useEffect(() => {
    mounted.current = true; setPage(null); void read(0);
    return () => { mounted.current = false; cancel(); };
    // Company, user and session generation own every private response.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scope]);

  return <section aria-label="Yêu cầu đã lưu" className="rounded-2xl border border-slate-200 bg-white p-5 dark:border-white/10 dark:bg-[#11182a]">
    <div className="flex flex-wrap items-center justify-between gap-3"><h2 className="text-lg font-semibold">Yêu cầu đã lưu</h2>
      <button type="button" className={button} onClick={() => void read(offset)}>Tải lại yêu cầu</button></div>
    <p className="mt-2 text-sm text-slate-500">Khôi phục yêu cầu của bạn sau khi tải lại hoặc đăng nhập lại. Xem danh sách không gửi công việc; yêu cầu chưa gửi được giữ trong 24 giờ.</p>
    {loading ? <p role="status" className="mt-4">Đang tải yêu cầu…</p> : null}
    {error ? <p role="alert" className="mt-4 text-rose-600">{error}</p> : null}
    {page ? <><ul className="mt-4 space-y-3">{page.items.map(intent => <li key={intent.operationId} className="rounded-xl border border-slate-100 p-3 dark:border-white/10">
      <p className="break-words whitespace-pre-wrap">{intent.question ?? "Thông tin yêu cầu chưa có"}</p>
      <p className="mt-2 text-sm text-slate-500">{labels[intent.state]}{intent.createdAtUtc ? ` · ${new Date(intent.createdAtUtc).toLocaleString("vi-VN")}` : ""}</p>
      {intent.state === 0 ? <><p className="mt-1 text-xs text-slate-500">Hạn gửi: {new Date(intent.expiresAtUtc!).toLocaleString("vi-VN")}</p>
        <button type="button" className={`${button} mt-3`} disabled={loading || disabled} onClick={() => void read(offset, intent.operationId)} aria-label={`Lấy yêu cầu đã lưu ${intent.question}`}>Lấy yêu cầu đã lưu</button></> : null}
      {intent.state === 1 ? <p className="mt-2 text-sm">Tiến độ và kết quả đã lưu nằm trong Công việc của tôi bên dưới.</p> : null}
      {intent.state === 2 ? <p className="mt-2 text-sm">Yêu cầu này chưa được gửi. Bạn có thể đặt câu hỏi mới ở Trợ lý AI.</p> : null}
      {intent.state === 3 ? <p className="mt-2 text-sm">Hãy liên hệ quản trị viên; yêu cầu này chưa thể gửi lại.</p> : null}
    </li>)}</ul>{page.items.length === 0 ? <p className="mt-4">Chưa có yêu cầu đã lưu trong trang này.</p> : null}
      <div className="mt-4 flex flex-wrap items-center gap-3"><button type="button" className={button} disabled={loading || offset === 0} onClick={() => void read(Math.max(0, offset - 25))}>Trang yêu cầu trước</button>
        <span className="text-sm">Trang {Math.floor(offset / 25) + 1}</span><button type="button" className={button} disabled={loading || !page.hasMore || offset + 25 > 10000} onClick={() => void read(offset + 25)}>Trang yêu cầu sau</button></div>
      {page.hasMore && offset + 25 > 10000 ? <p className="mt-3 text-sm">Đã đến giới hạn danh sách. Hãy liên hệ quản trị viên để tìm yêu cầu cũ.</p> : null}</> : null}
  </section>;
}
