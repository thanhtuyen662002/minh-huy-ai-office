"use client";
import { useState, type FormEvent } from "react";
import { botSchedule, type BotChannelView, type BotQr } from "../lib/bot-channels";

export const botButton = "rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm font-medium disabled:opacity-50 dark:border-white/10 dark:bg-white/5";
const inputClass = "rounded-lg border border-slate-300 bg-transparent p-2 dark:border-white/20";
const loginNames = { logged_out: "Chưa đăng nhập", awaiting_qr: "Chờ quét mã QR", logged_in: "Đã đăng nhập", error: "Có lỗi" };
const time = (value: string | null | undefined) => value ? new Date(value).toLocaleString("vi-VN") : "Chưa có";
type Props = { channel: BotChannelView; busy: boolean; qr: BotQr | null;
  save: (channel: BotChannelView, schedule: string[]) => void; command: (kind: "summarize-now" | "relogin") => void; refreshQr: () => void };

export function BotChannelCard({ channel, busy, qr, save, command, refreshQr }: Props) {
  const [draft, setDraft] = useState(channel), [schedule, setSchedule] = useState(channel.summarySchedule.join(", ")), [error, setError] = useState("");
  const name = channel.channel === "zalo" ? "Zalo" : "WhatsApp", groups = channel.status?.groups ?? [];
  function submit(event: FormEvent) {
    event.preventDefault(); const values = schedule.trim() ? schedule.split(",").map(v => v.trim()) : [];
    if (!botSchedule(values)) { setError("Giờ tóm tắt phải có dạng HH:mm, cách nhau bằng dấu phẩy."); return; }
    setError(""); save(draft, [...new Set(values)].sort());
  }
  return <article aria-label={`Kênh ${name}`} className="space-y-4 rounded-2xl border border-slate-200 bg-white p-5 dark:border-white/10 dark:bg-[#11182a]">
    <div className="flex items-center justify-between"><h3 className="text-lg font-semibold">{name}</h3><span>{channel.status ? loginNames[channel.status.loginState] : "Chưa nhận trạng thái"}</span></div>
    <dl className="grid gap-1 text-sm text-slate-500"><div><dt className="inline">Nhận trạng thái lần cuối: </dt><dd className="inline">{time(channel.status?.receivedAt)}</dd></div>
      <div><dt className="inline">Tóm tắt lần cuối: </dt><dd className="inline">{time(channel.status?.lastSummaryAt)}</dd></div></dl>
    {channel.status?.lastError ? <p role="status" className="text-sm text-amber-700">{channel.status.lastError}</p> : null}
    <form onSubmit={submit} className="grid gap-4"><fieldset disabled={busy} className="grid gap-4 disabled:opacity-60">
      <label className="flex gap-2"><input type="checkbox" checked={draft.enabled} onChange={e => setDraft({ ...draft, enabled: e.target.checked })} />Bật kênh {name}</label>
      <label className="flex gap-2"><input type="checkbox" checked={draft.dryRun} onChange={e => setDraft({ ...draft, dryRun: e.target.checked })} />Chạy thử {name} (dry-run)</label>
      <label className="grid gap-1">Lịch tóm tắt {name} (HH:mm)<input className={inputClass} value={schedule} maxLength={12000} placeholder="08:00, 17:30" onChange={e => setSchedule(e.target.value)} /></label>
      <p className="text-xs text-slate-500">Múi giờ Việt Nam · Asia/Ho_Chi_Minh</p>
      <label className="grid gap-1">Nhóm kỹ thuật {name}<select className={inputClass} value={draft.techGroupExternalId ?? ""} onChange={e => setDraft({ ...draft, techGroupExternalId: e.target.value || null,
        customerGroups: draft.customerGroups.filter(g => g.externalId !== e.target.value) })}><option value="">Chọn nhóm kỹ thuật</option>
        {draft.techGroupExternalId && !groups.some(g => g.externalId === draft.techGroupExternalId) ? <option value={draft.techGroupExternalId}>Nhóm đã chọn không còn trong danh sách</option> : null}
        {groups.map(g => <option key={g.externalId} value={g.externalId}>{g.displayName}</option>)}</select></label>
      <fieldset className="grid gap-2"><legend className="mb-2 font-medium">Nhóm khách hàng {name}</legend>
        {groups.length === 0 ? <p className="text-sm text-slate-500">Chờ bot báo danh sách nhóm sau khi đăng nhập.</p> : groups.filter(g => g.externalId !== draft.techGroupExternalId).map(g => {
          const selected = draft.customerGroups.find(c => c.externalId === g.externalId);
          return <div key={g.externalId} className="grid gap-2 rounded-xl bg-slate-50 p-3 dark:bg-white/5"><label className="flex gap-2"><input type="checkbox" checked={!!selected}
            onChange={e => setDraft({ ...draft, customerGroups: e.target.checked ? [...draft.customerGroups, { ...g, customerName: "" }] : draft.customerGroups.filter(c => c.externalId !== g.externalId) })} />{g.displayName}</label>
            {selected ? <label className="grid gap-1 text-sm">Tên khách hàng cho {g.displayName}<input required maxLength={200} className={inputClass} value={selected.customerName}
              onChange={e => setDraft({ ...draft, customerGroups: draft.customerGroups.map(c => c.externalId === g.externalId ? { ...c, customerName: e.target.value } : c) })} /></label> : null}</div>;
        })}
        {draft.customerGroups.filter(g => !groups.some(reported => reported.externalId === g.externalId)).map(g => <label key={g.externalId} className="flex gap-2 text-amber-700"><input type="checkbox" checked
          onChange={() => setDraft({ ...draft, customerGroups: draft.customerGroups.filter(c => c.externalId !== g.externalId) })} />{g.displayName} · không còn trong danh sách, bỏ chọn trước khi lưu</label>)}
      </fieldset>
      {error ? <p role="alert">{error}</p> : null}<button className={botButton} type="submit">Lưu kênh {name}</button>
    </fieldset></form>
    <div className="flex flex-wrap gap-2"><button className={botButton} disabled={busy} onClick={() => command("summarize-now")}>Tóm tắt ngay {name}</button>
      <button className={botButton} disabled={busy} onClick={() => command("relogin")}>Đăng nhập lại {name}</button>
      <button className={botButton} disabled={busy} onClick={refreshQr}>Xem mã QR {name}</button></div>
    {qr ? <div className="space-y-2"><p>Quét mã để đăng nhập {name}. Mã có hiệu lực tối đa 2 phút.</p><img alt={`Mã QR đăng nhập ${name}`} width={240} height={240} src={`data:image/png;base64,${qr.base64}`} /></div> : null}
  </article>;
}
