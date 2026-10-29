"use client";
import { useState, type FormEvent } from "react";
import type { CompanyMember } from "../lib/company-members";
import { type CustomerRequestDetail as Detail, type CustomerRequestPatch, type RequestStatus, requestStatuses } from "../lib/customer-requests";

export const requestButton = "rounded-lg border border-slate-200 px-3 py-2 text-sm disabled:opacity-50 dark:border-white/15";
export const requestTime = (value: string) => new Date(value).toLocaleString("vi-VN");
const priority = { high: "Cao", medium: "Trung bình", low: "Thấp" };
const kind = { bug: "Lỗi", feature: "Tính năng", question: "Câu hỏi", data: "Dữ liệu" };
type Props = { detail: Detail; members: readonly CompanyMember[] | null; memberError: string; disabled: boolean;
  onSave: (patch: CustomerRequestPatch) => void };

export function CustomerRequestDetail({ detail, members, memberError, disabled, onSave }: Props) {
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
  return <article aria-label={`Phiếu ${ticket.code}`} className="min-w-0 space-y-6">
    <header className="space-y-2"><h3 className="text-xl font-semibold">{ticket.code} · {ticket.customerName}</h3>
      <p className="text-sm">Nhóm Zalo: {ticket.sourceGroupName} · Người báo: {detail.reportedBy}</p>
      <p className="text-sm">Ngày tạo: {requestTime(ticket.createdAt)} · Cập nhật: {requestTime(ticket.updatedAt)}</p>
      <p className="text-sm">Trạng thái: {requestStatuses[ticket.status]} · Người phụ trách: {name(ticket.assigneeUserId)}</p></header>
    <section aria-label="Tóm tắt yêu cầu" className="space-y-2"><h4 className="font-semibold">Tóm tắt</h4>
      <p className="whitespace-pre-wrap break-words">{ticket.summary}</p>
      <p className="text-sm">Khoảng tin nguồn: {requestTime(detail.sourceWindowFrom)} – {requestTime(detail.sourceWindowTo)}</p></section>
    <section aria-label="Nội dung yêu cầu" className="space-y-2"><h4 className="font-semibold">Nội dung yêu cầu</h4>
      <ul className="space-y-3">{detail.items.map((item, index) => <li key={index} className="rounded-xl bg-slate-50 p-3 dark:bg-white/5">
        <p className="whitespace-pre-wrap break-words">{item.content}</p><p className="mt-2 text-sm">Ưu tiên: {priority[item.priority]} · Loại: {kind[item.kind]}</p></li>)}</ul></section>
    <section className="space-y-2"><h4 className="font-semibold">Còn chưa rõ / cần hỏi lại</h4>
      {detail.openQuestions.length ? <ul className="list-disc space-y-1 pl-5">{detail.openQuestions.map((question, index) => <li className="whitespace-pre-wrap break-words" key={index}>{question}</li>)}</ul>
        : <p>Không có câu hỏi cần làm rõ được ghi nhận.</p>}</section>
    <section className="space-y-2"><h4 className="font-semibold">Bằng chứng trích từ Zalo</h4>
      {detail.evidence.length ? detail.evidence.map((item, index) => <figure key={index} className="border-l-4 border-indigo-200 pl-4 dark:border-indigo-400/40">
        <blockquote className="whitespace-pre-wrap break-words">{item.quote}</blockquote>
        <figcaption className="mt-1 text-sm text-slate-500">{item.senderName} · <time dateTime={item.messageTimestamp}>{requestTime(item.messageTimestamp)}</time></figcaption></figure>)
        : <p>Chưa có bằng chứng được ghi nhận.</p>}</section>
    <section className="space-y-2"><h4 className="font-semibold">Thời hạn khách mong muốn</h4>
      <p className="whitespace-pre-wrap break-words">{detail.customerDesiredDueText ?? "Chưa ghi nhận."}</p>
      <p className="text-sm text-slate-500">Đây là mong muốn của khách hàng, chưa phải cam kết xử lý của nhân viên.</p></section>
    <form onSubmit={save} aria-label="Cập nhật phiếu" className="space-y-3 rounded-xl border border-slate-200 p-4 dark:border-white/15">
      <h4 className="font-semibold">Cập nhật xử lý</h4>
      <div className="flex flex-wrap gap-4"><label className="grid gap-1 text-sm">Trạng thái
        <select className={requestButton} value={status} disabled={disabled} onChange={event => setStatus(event.target.value as RequestStatus)}>
          {Object.entries(requestStatuses).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
        <label className="grid gap-1 text-sm">Người phụ trách
          <select className={`${requestButton} max-w-full`} value={assignee} disabled={disabled || members === null || !!memberError}
            onChange={event => setAssignee(event.target.value)}>
            <option value="">Chưa phân công</option>
            {ticket.assigneeUserId && !active.some(member => member.userId === ticket.assigneeUserId)
              ? <option value={ticket.assigneeUserId}>{name(ticket.assigneeUserId)} (hiện tại)</option> : null}
            {active.map(member => <option key={member.userId} value={member.userId}>{member.displayName}</option>)}</select></label></div>
      {memberError ? <p role="note" className="text-sm text-amber-700 dark:text-amber-200">{memberError}</p>
        : members === null ? <p role="status">Đang tải thành viên…</p> : null}
      <button type="submit" className={requestButton} disabled={disabled || !changed}>Lưu thay đổi</button>
    </form>
    <section className="space-y-2"><h4 className="font-semibold">Lịch sử sự kiện</h4>
      {detail.events.length ? <ol className="space-y-2">{detail.events.map((event, index) => <li key={index} className="rounded-lg bg-slate-50 p-3 text-sm dark:bg-white/5">
        <p>{event.eventType === "created" ? `Tạo phiếu · ${statusLabel(event.newValue)}` : event.eventType === "status_changed"
          ? `Đổi trạng thái: ${statusLabel(event.oldValue)} → ${statusLabel(event.newValue)}`
          : `Đổi người phụ trách: ${name(event.oldValue)} → ${name(event.newValue)}`}</p>
        <p className="mt-1 break-words">{event.actorType === "service" ? "Dịch vụ tiếp nhận tự động" : event.actorType === "zalo_member"
          ? `Nhóm Kỹ thuật Zalo: ${event.actorName}` : `Nhân viên: ${name(event.actorId)}`} · {requestTime(event.timestamp)}</p></li>)}</ol>
        : <p>Chưa có sự kiện được ghi nhận.</p>}</section>
  </article>;
}
