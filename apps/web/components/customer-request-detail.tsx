"use client";
import { useState, type FormEvent } from "react";
import { Sparkles, X } from "lucide-react";
import type { CompanyMember } from "../lib/company-members";
import { type CustomerRequestDetail as Detail, type CustomerRequestPatch, type RequestStatus, requestStatuses } from "../lib/customer-requests";

export const requestButton = "inline-flex h-9 items-center justify-center gap-2 rounded-lg border bg-card px-3 text-sm font-medium hover:bg-muted disabled:pointer-events-none disabled:opacity-50";
export const requestPrimaryButton = "inline-flex h-9 items-center justify-center gap-2 rounded-lg bg-primary px-3 text-sm font-medium text-primary-foreground hover:opacity-90 disabled:pointer-events-none disabled:opacity-50";
export const requestField = "h-9 rounded-lg border bg-card px-2.5 text-sm disabled:opacity-50";
export const requestTime = (value: string) => new Date(value).toLocaleString("vi-VN");
const relative = new Intl.RelativeTimeFormat("vi", { numeric: "auto" });
export function requestAge(value: string) {
  const minutes = Math.round((Date.parse(value) - Date.now()) / 60000);
  if (Math.abs(minutes) < 60) return relative.format(minutes, "minute");
  if (Math.abs(minutes) < 60 * 24) return relative.format(Math.round(minutes / 60), "hour");
  return relative.format(Math.round(minutes / 60 / 24), "day");
}
export const statusTone: Record<RequestStatus, { chip: string; dot: string }> = {
  new: { chip: "bg-indigo-50 text-indigo-700 dark:bg-indigo-400/15 dark:text-indigo-200", dot: "bg-indigo-500" },
  needs_clarification: { chip: "bg-amber-50 text-amber-800 dark:bg-amber-400/15 dark:text-amber-200", dot: "bg-amber-500" },
  in_progress: { chip: "bg-sky-50 text-sky-800 dark:bg-sky-400/15 dark:text-sky-200", dot: "bg-sky-500" },
  done: { chip: "bg-emerald-50 text-emerald-800 dark:bg-emerald-400/15 dark:text-emerald-200", dot: "bg-emerald-500" },
  rejected: { chip: "bg-muted text-muted-foreground", dot: "bg-slate-400" },
};
export function StatusChip({ status }: { status: RequestStatus }) {
  return <span className={`inline-flex h-6 shrink-0 items-center rounded-full px-2.5 text-xs font-medium ${statusTone[status].chip}`}>{requestStatuses[status]}</span>;
}
export function initials(name: string) {
  const words = name.trim().split(/\s+/);
  return (words.length > 1 ? words[0][0] + words[words.length - 1][0] : name.slice(0, 2)).toUpperCase();
}
const priority = { high: "Cao", medium: "Trung bình", low: "Thấp" };
const kind = { bug: "Lỗi", feature: "Tính năng", question: "Câu hỏi", data: "Dữ liệu" };
const heading = "text-sm font-semibold";
type Props = { detail: Detail; members: readonly CompanyMember[] | null; memberError: string; disabled: boolean;
  onSave: (patch: CustomerRequestPatch) => void; onClose: () => void };

export function CustomerRequestDetail({ detail, members, memberError, disabled, onSave, onClose }: Props) {
  const ticket = detail.request;
  const [status, setStatus] = useState<RequestStatus>(ticket.status);
  const [assignee, setAssignee] = useState(ticket.assigneeUserId ?? "");
  const active = members?.filter(member => member.userActive && member.membershipActive) ?? [];
  const name = (id: string | null) => id === null ? "Chưa phân công" : members?.find(member => member.userId === id)?.displayName ?? id;
  const statusLabel = (value: string | null) => value && Object.hasOwn(requestStatuses, value) ? requestStatuses[value as RequestStatus] : value ?? "—";
  const changed = status !== ticket.status || assignee !== (ticket.assigneeUserId ?? "");
  function save(event: FormEvent) {
    event.preventDefault();
    if (disabled || !changed) return;
    const patch: CustomerRequestPatch = { rowVersion: ticket.rowVersion };
    if (status !== ticket.status) patch.status = status;
    if (members && !memberError && assignee !== (ticket.assigneeUserId ?? "")) patch.assigneeUserId = assignee || null;
    onSave(patch);
  }
  const move = (next: RequestStatus) => { if (!disabled) onSave({ rowVersion: ticket.rowVersion, status: next }); };
  return <article aria-label={`Phiếu ${ticket.code}`} className="min-w-0 overflow-hidden rounded-xl border bg-card shadow-sm">
    <header className="space-y-3 border-b p-5">
      <div className="flex items-center gap-2">
        <span className="font-mono text-xs text-muted-foreground">{ticket.code}</span>
        <StatusChip status={ticket.status} />
        <button type="button" onClick={onClose} aria-label="Đóng chi tiết phiếu"
          className="ml-auto inline-flex size-8 items-center justify-center rounded-lg text-muted-foreground hover:bg-muted hover:text-foreground">
          <X className="size-4" aria-hidden="true" /></button>
      </div>
      <h3 className="break-words text-lg font-semibold leading-snug">{ticket.customerName}</h3>
      <dl className="grid grid-cols-[7.5rem_minmax(0,1fr)] gap-x-3 gap-y-2 text-sm">
        <dt className="text-muted-foreground">Nhóm Zalo</dt><dd className="break-words">{ticket.sourceGroupName}</dd>
        <dt className="text-muted-foreground">Người báo</dt><dd className="break-words">{detail.reportedBy}</dd>
        <dt className="text-muted-foreground">Phụ trách</dt><dd className="break-words">{name(ticket.assigneeUserId)}</dd>
        <dt className="text-muted-foreground">Ngày tạo</dt><dd>{requestTime(ticket.createdAt)}</dd>
        <dt className="text-muted-foreground">Cập nhật</dt><dd>{requestTime(ticket.updatedAt)}</dd>
      </dl>
      {ticket.status === "done" || ticket.status === "rejected" ? null : <div className="flex flex-wrap gap-2 pt-1">
        {ticket.status === "in_progress" ? null : <button type="button" className={`${requestPrimaryButton} flex-1`} disabled={disabled}
          onClick={() => move("in_progress")}>Bắt đầu xử lý</button>}
        <button type="button" className={`${ticket.status === "in_progress" ? requestPrimaryButton : requestButton} flex-1`} disabled={disabled}
          onClick={() => move("done")}>Đánh dấu xong</button>
      </div>}
    </header>
    <section aria-label="Tóm tắt yêu cầu" className="space-y-2 border-b p-5">
      <h4 className={`${heading} flex items-center gap-2`}><Sparkles className="size-4 text-indigo-500" aria-hidden="true" />Tóm tắt</h4>
      <p className="whitespace-pre-wrap break-words text-sm leading-6">{ticket.summary}</p>
      <p className="text-xs text-muted-foreground">Khoảng tin nguồn: {requestTime(detail.sourceWindowFrom)} – {requestTime(detail.sourceWindowTo)}</p></section>
    <section aria-label="Nội dung yêu cầu" className="space-y-2 border-b p-5"><h4 className={heading}>Nội dung yêu cầu</h4>
      <ul className="space-y-2">{detail.items.map((item, index) => <li key={index} className="rounded-lg bg-muted p-3 text-sm">
        <p className="whitespace-pre-wrap break-words">{item.content}</p><p className="mt-1.5 text-xs text-muted-foreground">Ưu tiên: {priority[item.priority]} · Loại: {kind[item.kind]}</p></li>)}</ul></section>
    <section className="space-y-2 border-b p-5"><h4 className={heading}>Còn chưa rõ / cần hỏi lại</h4>
      {detail.openQuestions.length ? <ul className="list-disc space-y-1 pl-5 text-sm">{detail.openQuestions.map((question, index) => <li className="whitespace-pre-wrap break-words" key={index}>{question}</li>)}</ul>
        : <p className="text-sm text-muted-foreground">Không có câu hỏi cần làm rõ được ghi nhận.</p>}</section>
    <section className="space-y-3 border-b p-5"><h4 className={heading}>Bằng chứng trích từ Zalo</h4>
      {detail.evidence.length ? detail.evidence.map((item, index) => <figure key={index} className="m-0 flex gap-2.5">
        <span aria-hidden="true" className="flex size-7 shrink-0 items-center justify-center rounded-full bg-amber-100 text-[11px] font-semibold text-amber-800 dark:bg-amber-400/15 dark:text-amber-200">{initials(item.senderName)}</span>
        <div className="min-w-0 space-y-1">
          <figcaption className="text-xs font-medium text-muted-foreground">{item.senderName} · <time dateTime={item.messageTimestamp}>{requestTime(item.messageTimestamp)}</time></figcaption>
          <blockquote className="m-0 whitespace-pre-wrap break-words rounded-lg rounded-tl-sm bg-muted px-3 py-2 text-sm">{item.quote}</blockquote>
        </div></figure>)
        : <p className="text-sm text-muted-foreground">Chưa có bằng chứng được ghi nhận.</p>}</section>
    <section className="space-y-2 border-b p-5"><h4 className={heading}>Thời hạn khách mong muốn</h4>
      <p className="whitespace-pre-wrap break-words text-sm">{detail.customerDesiredDueText ?? "Chưa ghi nhận."}</p>
      <p className="text-xs text-muted-foreground">Đây là mong muốn của khách hàng, chưa phải cam kết xử lý của nhân viên.</p></section>
    <form onSubmit={save} aria-label="Cập nhật phiếu" className="space-y-3 border-b p-5">
      <h4 className={heading}>Cập nhật xử lý</h4>
      <div className="grid gap-3 sm:grid-cols-2"><label className="grid gap-1 text-xs font-medium text-muted-foreground">Trạng thái
        <select className={requestField} value={status} disabled={disabled} onChange={event => setStatus(event.target.value as RequestStatus)}>
          {Object.entries(requestStatuses).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
        <label className="grid min-w-0 gap-1 text-xs font-medium text-muted-foreground">Người phụ trách
          <select className={`${requestField} max-w-full`} value={assignee} disabled={disabled || members === null || !!memberError}
            onChange={event => setAssignee(event.target.value)}>
            <option value="">Chưa phân công</option>
            {ticket.assigneeUserId && !active.some(member => member.userId === ticket.assigneeUserId)
              ? <option value={ticket.assigneeUserId}>{name(ticket.assigneeUserId)} (hiện tại)</option> : null}
            {active.map(member => <option key={member.userId} value={member.userId}>{member.displayName}</option>)}</select></label></div>
      {memberError ? <p role="note" className="text-sm text-amber-700 dark:text-amber-200">{memberError}</p>
        : members === null ? <p role="status" className="text-sm text-muted-foreground">Đang tải thành viên…</p> : null}
      <button type="submit" className={requestButton} disabled={disabled || !changed}>Lưu thay đổi</button>
    </form>
    <section className="space-y-2 p-5"><h4 className={heading}>Lịch sử sự kiện</h4>
      {detail.events.length ? <ol className="space-y-3 border-l pl-4">{detail.events.map((event, index) => <li key={index} className="text-sm">
        <p>{event.eventType === "created" ? `Tạo phiếu · ${statusLabel(event.newValue)}` : event.eventType === "status_changed"
          ? `Đổi trạng thái: ${statusLabel(event.oldValue)} → ${statusLabel(event.newValue)}`
          : `Đổi người phụ trách: ${name(event.oldValue)} → ${name(event.newValue)}`}</p>
        <p className="mt-0.5 break-words text-xs text-muted-foreground">{event.actorType === "service" ? "Dịch vụ tiếp nhận tự động" : event.actorType === "zalo_member"
          ? `Nhóm Kỹ thuật Zalo: ${event.actorName}` : `Nhân viên: ${name(event.actorId)}`} · {requestTime(event.timestamp)}</p></li>)}</ol>
        : <p className="text-sm text-muted-foreground">Chưa có sự kiện được ghi nhận.</p>}</section>
  </article>;
}
