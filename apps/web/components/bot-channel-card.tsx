"use client";
import { useState, type FormEvent, type ReactNode } from "react";
import { Play, QrCode, RotateCw } from "lucide-react";
import { botSchedule, type BotChannelView, type BotQr } from "../lib/bot-channels";

export const botButton = "inline-flex h-9 items-center justify-center gap-2 rounded-lg border bg-card px-3 text-sm font-medium hover:bg-muted disabled:pointer-events-none disabled:opacity-50";
export const botPrimaryButton = "inline-flex h-9 items-center justify-center gap-2 rounded-lg bg-primary px-3 text-sm font-medium text-primary-foreground hover:opacity-90 disabled:pointer-events-none disabled:opacity-50";
export const botInput = "h-9 w-full rounded-lg border bg-card px-2.5 text-sm disabled:opacity-50";
const loginNames = { logged_out: "Chưa đăng nhập", awaiting_qr: "Chờ quét mã QR", logged_in: "Đã đăng nhập", error: "Có lỗi" };
const time = (value: string | null | undefined) => value ? new Date(value).toLocaleString("vi-VN") : "chưa có";
const tiles = { zalo: "bg-blue-50 text-blue-700 dark:bg-blue-400/15 dark:text-blue-200", whatsapp: "bg-emerald-50 text-emerald-700 dark:bg-emerald-400/15 dark:text-emerald-200" };

// A checkbox drawn as a switch; `role` stays checkbox unless asked so lists of choices read as choices.
export function Toggle({ checked, onChange, label, role, disabled, children }: { checked: boolean; onChange: (checked: boolean) => void;
  label?: string; role?: "switch"; disabled?: boolean; children?: ReactNode }) {
  return <label className={`inline-flex min-w-0 items-center gap-3 ${disabled ? "opacity-60" : "cursor-pointer"}`}>
    <input type="checkbox" role={role} aria-label={label} checked={checked} disabled={disabled} onChange={event => onChange(event.target.checked)} className="peer sr-only" />
    <span aria-hidden="true" className="relative h-5 w-9 shrink-0 rounded-full bg-slate-300 transition-colors after:absolute after:left-0.5 after:top-0.5 after:size-4 after:rounded-full after:bg-white after:shadow-sm after:transition-transform peer-checked:bg-primary peer-checked:after:translate-x-4 peer-focus-visible:ring-2 peer-focus-visible:ring-ring/50 dark:bg-white/20 dark:after:bg-background" />
    {children}
  </label>;
}

type Props = { channel: BotChannelView; busy: boolean; qr: BotQr | null;
  save: (channel: BotChannelView, schedule: string[]) => void; command: (kind: "summarize-now" | "relogin") => void; refreshQr: () => void };

export function BotChannelCard({ channel, busy, qr, save, command, refreshQr }: Props) {
  const [draft, setDraft] = useState(channel), [schedule, setSchedule] = useState(channel.summarySchedule.join(", ")), [error, setError] = useState("");
  const name = channel.channel === "zalo" ? "Zalo" : "WhatsApp", groups = channel.status?.groups ?? [];
  const running = channel.enabled && channel.status?.loginState === "logged_in";
  function submit(event: FormEvent) {
    event.preventDefault(); const values = schedule.trim() ? schedule.split(",").map(v => v.trim()) : [];
    if (!botSchedule(values)) { setError("Giờ tóm tắt phải có dạng HH:mm, cách nhau bằng dấu phẩy."); return; }
    setError(""); save(draft, [...new Set(values)].sort());
  }
  const customers = groups.filter(g => g.externalId !== draft.techGroupExternalId);
  const missing = draft.customerGroups.filter(g => !groups.some(reported => reported.externalId === g.externalId));
  return <article aria-label={`Kênh ${name}`} className="flex flex-col overflow-hidden rounded-xl border bg-card shadow-sm">
    <form onSubmit={submit} className="flex flex-1 flex-col"><fieldset disabled={busy} className="m-0 flex min-w-0 flex-1 flex-col border-0 p-0 disabled:opacity-70">
      <header className="flex flex-wrap items-center gap-3 border-b p-5">
        <span className={`flex size-10 shrink-0 items-center justify-center rounded-lg text-xs font-bold ${tiles[channel.channel]}`} aria-hidden="true">{channel.channel === "zalo" ? "Zalo" : "WA"}</span>
        <div className="min-w-0 flex-1">
          <h3 className="text-base font-semibold">Bot {name}</h3>
          <p className="text-xs text-muted-foreground"><span>{channel.status ? loginNames[channel.status.loginState] : "Chưa nhận trạng thái"}</span> · tóm tắt lần cuối {time(channel.status?.lastSummaryAt)}</p>
        </div>
        <span className={`inline-flex h-6 items-center gap-1.5 rounded-full px-2.5 text-xs font-medium ${running
          ? "bg-emerald-50 text-emerald-800 dark:bg-emerald-400/15 dark:text-emerald-200" : "bg-muted text-muted-foreground"}`}>
          <span className={`size-1.5 rounded-full ${running ? "bg-emerald-500" : "bg-slate-400"}`} aria-hidden="true" />{running ? "Đang chạy" : channel.enabled ? "Đã bật" : "Đang tắt"}</span>
        <Toggle role="switch" label={`Bật kênh ${name}`} checked={draft.enabled} onChange={enabled => setDraft({ ...draft, enabled })} />
      </header>
      <div className="grid flex-1 content-start gap-4 p-5">
        {channel.status?.lastError ? <p role="status" className="rounded-lg bg-amber-50 px-3 py-2 text-sm text-amber-800 dark:bg-amber-400/10 dark:text-amber-200">{channel.status.lastError}</p> : null}
        <div className="grid gap-4 sm:grid-cols-2">
          <div className="grid gap-1.5 text-xs text-muted-foreground"><label className="grid gap-1.5 font-medium">Lịch tóm tắt {name} (HH:mm)
            <input className={botInput} value={schedule} maxLength={12000} placeholder="08:00, 17:30" onChange={e => setSchedule(e.target.value)} /></label>
            <span>Giờ Việt Nam, cách nhau bằng dấu phẩy</span></div>
          <div className="grid gap-1.5 text-xs text-muted-foreground"><label className="grid gap-1.5 font-medium">Nhóm kỹ thuật {name}
            <select className={botInput} value={draft.techGroupExternalId ?? ""} onChange={e => setDraft({ ...draft, techGroupExternalId: e.target.value || null,
              customerGroups: draft.customerGroups.filter(g => g.externalId !== e.target.value) })}><option value="">Chọn nhóm kỹ thuật</option>
              {draft.techGroupExternalId && !groups.some(g => g.externalId === draft.techGroupExternalId) ? <option value={draft.techGroupExternalId}>Nhóm đã chọn không còn trong danh sách</option> : null}
              {groups.map(g => <option key={g.externalId} value={g.externalId}>{g.displayName}</option>)}</select></label>
            <span>Bot gửi bảng tóm tắt vào nhóm này</span></div>
        </div>
        <div className="flex items-center justify-between gap-4 rounded-lg bg-muted px-4 py-3">
          <div><p className="text-sm font-medium">Chạy thử</p><p className="text-xs text-muted-foreground">Bot chỉ đọc và tạo phiếu, không gửi gì vào {name}</p></div>
          <Toggle role="switch" label={`Chạy thử ${name} (dry-run)`} checked={draft.dryRun} onChange={dryRun => setDraft({ ...draft, dryRun })} />
        </div>
        <fieldset className="m-0 min-w-0 rounded-lg border p-0">
          <legend className="sr-only">Nhóm khách hàng {name}</legend>
          <div className="flex items-baseline justify-between gap-3 border-b px-4 py-2.5">
            <span className="text-sm font-medium">Nhóm khách hàng</span>
            <span className="text-xs text-muted-foreground">Bật để bot tạo phiếu từ nhóm</span>
          </div>
          {customers.length === 0 ? <p className="px-4 py-3 text-sm text-muted-foreground">Chờ bot báo danh sách nhóm sau khi đăng nhập.</p>
            : <ul className="m-0 list-none divide-y p-0">{customers.map(g => {
              const selected = draft.customerGroups.find(c => c.externalId === g.externalId);
              return <li key={g.externalId} className="grid gap-2 px-4 py-2.5">
                <Toggle checked={!!selected} onChange={checked => setDraft({ ...draft, customerGroups: checked
                  ? [...draft.customerGroups, { ...g, customerName: "" }] : draft.customerGroups.filter(c => c.externalId !== g.externalId) })}>
                  <span className="truncate text-sm">{g.displayName}</span></Toggle>
                {selected ? <label className="grid gap-1 pl-12 text-xs text-muted-foreground">Tên khách hàng cho {g.displayName}
                  <input required maxLength={200} className={botInput} value={selected.customerName} placeholder="Ví dụ: Cty Hoà Phát Đạt"
                    onChange={e => setDraft({ ...draft, customerGroups: draft.customerGroups.map(c => c.externalId === g.externalId ? { ...c, customerName: e.target.value } : c) })} /></label> : null}
              </li>;
            })}</ul>}
          {missing.map(g => <label key={g.externalId} className="flex gap-2 border-t px-4 py-2.5 text-sm text-amber-700 dark:text-amber-200"><input type="checkbox" checked
            onChange={() => setDraft({ ...draft, customerGroups: draft.customerGroups.filter(c => c.externalId !== g.externalId) })} />{g.displayName} · không còn trong danh sách, bỏ chọn trước khi lưu</label>)}
        </fieldset>
        {error ? <p role="alert" className="text-sm text-rose-600 dark:text-rose-300">{error}</p> : null}
      </div>
      <footer className="flex flex-wrap items-center gap-2 border-t bg-muted/40 px-5 py-3">
        <button className={botPrimaryButton} type="submit">Lưu<span className="sr-only"> kênh {name}</span></button>
        <button type="button" className={botButton} onClick={() => command("summarize-now")}><Play className="size-4" aria-hidden="true" />Tóm tắt ngay<span className="sr-only"> {name}</span></button>
        <button type="button" className={botButton} onClick={() => command("relogin")}><RotateCw className="size-4" aria-hidden="true" />Đăng nhập lại<span className="sr-only"> {name}</span></button>
        <button type="button" className={botButton} onClick={refreshQr}><QrCode className="size-4" aria-hidden="true" />Xem mã QR<span className="sr-only"> {name}</span></button>
      </footer>
    </fieldset></form>
    {qr ? <div className="flex flex-wrap items-center gap-5 border-t p-5">
      <img alt={`Mã QR đăng nhập ${name}`} width={160} height={160} className="rounded-lg border bg-white p-2" src={`data:image/png;base64,${qr.base64}`} />
      <div className="space-y-2 text-sm"><p className="font-medium">Quét mã để đăng nhập {name}</p>
        <p className="text-muted-foreground">Mở {name} trên điện thoại, chọn liên kết thiết bị rồi quét mã. Mã có hiệu lực tối đa 2 phút.</p></div>
    </div> : null}
  </article>;
}
