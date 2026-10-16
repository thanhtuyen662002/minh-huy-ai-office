"use client";
import { useEffect, useRef, useState } from "react";
import { parseTaskHistoryDetail, parseTaskHistoryPage, TaskHistoryDetail, TaskHistoryPage, taskHistoryStatus } from "../lib/task-history";

type Props = { companyId: string; userId: string; generation: number; isCurrent: (generation: number) => boolean;
  validate: (generation: number) => Promise<boolean>; request: (generation: number, url: string, init?: RequestInit) => Promise<Response>;
  onUnauthorized: () => void };
const button = "rounded-lg border border-slate-200 px-3 py-2 text-sm disabled:opacity-50 dark:border-white/15";
const time = (value: string) => new Date(value).toLocaleString("vi-VN");

export function TaskHistoryPanel({ companyId, userId, generation, isCurrent, validate, request, onUnauthorized }: Props) {
  const [page, setPage] = useState<TaskHistoryPage | null>(null);
  const [detail, setDetail] = useState<TaskHistoryDetail | null>(null);
  const [offset, setOffset] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [selected, setSelected] = useState<string | null>(null);
  const scope = `${companyId}:${userId}:${generation}`, currentScope = useRef(scope);
  currentScope.current = scope;
  const mounted = useRef(false), serial = useRef(0), controllers = useRef(new Set<AbortController>());
  const current = (value: number) => mounted.current && currentScope.current === scope && serial.current === value && isCurrent(generation);
  function cancel() { serial.current += 1; for (const controller of controllers.current) controller.abort(); controllers.current.clear(); }

  async function read(nextOffset: number, taskId: string | null = null) {
    if (!mounted.current || !isCurrent(generation)) return;
    cancel(); const value = serial.current, controller = new AbortController(); controllers.current.add(controller);
    setLoading(true); setError(""); setDetail(null); setSelected(taskId);
    if (!taskId) { setPage(null); setOffset(nextOffset); }
    async function confirm() {
      const valid = await validate(generation);
      if (!valid && current(value)) { setPage(null); setDetail(null); setSelected(null); }
      return valid && current(value);
    }
    try {
      if (!await confirm()) return;
      const path = taskId ? `/api/local/tasks/${encodeURIComponent(taskId)}/history?companyId=${encodeURIComponent(companyId)}`
        : `/api/local/tasks?companyId=${encodeURIComponent(companyId)}&offset=${nextOffset}&limit=25`;
      const response = await request(generation, path, { cache: "no-store", signal: controller.signal });
      if (!current(value)) return;
      if (response.status === 401) { setPage(null); setDetail(null); setSelected(null); onUnauthorized(); return; }
      if (response.status === 403) {
        setPage(null); setDetail(null); setSelected(null); await validate(generation);
        if (current(value)) setError("Không còn quyền xem công việc. Hãy xác nhận lại quyền truy cập công ty.");
        return;
      }
      if (response.status === 404 && taskId) {
        if (await confirm()) setError("Không tìm thấy công việc thuộc tài khoản của bạn. Hãy tải lại danh sách.");
        return;
      }
      if (!response.ok) throw new Error("Unavailable");
      const body: unknown = await response.json();
      if (!current(value)) return;
      if (taskId) {
        const received = parseTaskHistoryDetail(body);
        if (!received || received.companyId !== companyId || received.task.taskId !== taskId) throw new Error("Invalid detail");
        if (await confirm()) setDetail(received);
      } else {
        const received = parseTaskHistoryPage(body);
        if (!received || received.companyId !== companyId || received.offset !== nextOffset || received.limit !== 25) throw new Error("Invalid page");
        if (await confirm()) setPage(received);
      }
    } catch {
      if (current(value)) {
        setPage(null); setDetail(null);
        if (await confirm()) setError("Chưa tải được công việc. Hãy thử tải lại; kết quả đã lưu sẽ được giữ nguyên.");
      }
    } finally {
      controllers.current.delete(controller);
      if (current(value)) setLoading(false);
    }
  }

  useEffect(() => {
    mounted.current = true; setPage(null); setDetail(null); setSelected(null); void read(0);
    return () => { mounted.current = false; cancel(); };
    // The authenticated company/user/generation owns this private archive.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scope]);

  return <section aria-label="Công việc của tôi" className="rounded-2xl border border-slate-200 bg-white p-5 dark:border-white/10 dark:bg-[#11182a]">
    <div className="flex flex-wrap items-center justify-between gap-3"><h2 className="text-lg font-semibold">Công việc của tôi</h2>
      <button type="button" className={button} onClick={() => void read(offset)}>Tải lại công việc</button></div>
    <p className="mt-2 text-sm text-slate-500">Xem công việc đã lưu của bạn trong công ty đang chọn. Tải lại hoặc mở kết quả không chạy lại công việc.</p>
    {loading ? <p role="status" className="mt-4">Đang tải công việc…</p> : null}
    {error ? <div className="mt-4"><p role="alert" className="text-rose-600">{error}</p>
      <button type="button" className={`${button} mt-3`} onClick={() => void read(offset, selected)}>Thử tải lại</button></div> : null}
    {page ? <><div className="mt-5 overflow-x-auto"><table className="w-full text-left text-sm"><thead><tr>
      <th className="p-2">Công việc</th><th className="p-2">Trạng thái</th><th className="p-2">Thời gian tạo</th><th className="p-2">Kết quả</th>
    </tr></thead><tbody>{page.items.map(task => <tr key={task.taskId} className="border-t border-slate-100 dark:border-white/10">
      <td className="max-w-lg break-words p-2">{task.summary ?? "Thông tin công việc chưa có"}
        {task.metadataUnavailable ? <p className="mt-1 text-xs text-amber-700">Thông tin đã lưu cần được kiểm tra.</p> : null}</td>
      <td className="p-2">{taskHistoryStatus(task.status)}</td><td className="p-2">{time(task.createdAtUtc)}</td>
      <td className="p-2"><button type="button" className={button} onClick={() => void read(offset, task.taskId)} aria-label={`Xem công việc ${task.summary ?? task.taskId}`}>Xem công việc</button></td>
    </tr>)}</tbody></table></div>{page.items.length === 0 ? <p className="mt-4">Chưa có công việc trong trang này. Bạn có thể đặt câu hỏi ở Trợ lý AI.</p> : null}
    <div className="mt-4 flex flex-wrap items-center gap-3"><button type="button" className={button} disabled={offset === 0} onClick={() => void read(Math.max(0, offset - 25))}>Trang trước</button>
      <span className="text-sm">Trang {Math.floor(offset / 25) + 1}</span><button type="button" className={button} disabled={!page.hasMore || offset + 25 > 10000} onClick={() => void read(offset + 25)}>Trang sau</button></div>
    {page.hasMore && offset + 25 > 10000 ? <p className="mt-3 text-sm">Đã đến giới hạn danh sách. Hãy liên hệ quản trị viên để được hỗ trợ tìm công việc cũ.</p> : null}</> : null}
    {detail ? <article aria-label="Chi tiết công việc" className="mt-6 rounded-xl border border-slate-200 p-4 dark:border-white/15">
      <div className="flex flex-wrap items-center justify-between gap-3"><h3 className="font-semibold">Chi tiết công việc</h3>
        <button type="button" className={button} onClick={() => void read(offset, detail.task.taskId)}>Cập nhật trạng thái</button></div>
      <p className="mt-3 break-words">{detail.task.summary ?? "Thông tin công việc chưa có"}</p>
      <p className="mt-2 text-sm">{taskHistoryStatus(detail.task.status)} · Cập nhật {time(detail.task.updatedAtUtc)}</p>
      <p className="mt-2 break-all text-xs text-slate-500">Mã công việc: {detail.task.taskId}</p>
      {detail.result?.kind === "answer" ? <><div className="mt-4 whitespace-pre-wrap break-words leading-7">{detail.result.answer}</div>
        <p className="mt-4 text-xs text-slate-500">{detail.result.provider} · {detail.result.model} · Token ghi nhận: {detail.result.totalTokens}</p></> : null}
      {detail.result?.kind === "connection" ? <p className="mt-4">Đã kiểm tra kết nối chỉ đọc. Công việc này chưa tạo câu trả lời AI.</p> : null}
      {detail.resultUnavailable ? <p role="status" className="mt-4 text-amber-700">Công việc đã hoàn thành nhưng chưa đọc được kết quả đã lưu. Hãy tải lại hoặc liên hệ quản trị viên với mã công việc.</p> : null}
      {detail.task.metadataUnavailable ? <p className="mt-4 text-amber-700">Thông tin đã lưu cần được kiểm tra. Hãy liên hệ quản trị viên với mã công việc.</p> : null}
      {detail.task.status === 5 ? <p className="mt-4">Công việc không hoàn thành. Hãy kiểm tra quyền nguồn dữ liệu; bạn có thể tạo câu hỏi mới ở Trợ lý AI.</p> : null}
      {detail.task.status !== null && detail.task.status < 5 ? <p className="mt-4">Công việc chưa hoàn thành. Dùng Cập nhật trạng thái để xem tiến độ đã lưu.</p> : null}
    </article> : null}
  </section>;
}
