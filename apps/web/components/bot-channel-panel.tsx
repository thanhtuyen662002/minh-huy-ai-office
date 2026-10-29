"use client";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { botObject, parseBotAdmin, parseBotAudit, parseBotQr, type BotAdminView, type BotAudit, type BotChannel, type BotChannelView, type BotQr } from "../lib/bot-channels";
import { RefreshCw } from "lucide-react";
import { BotChannelCard, botButton, botInput, botPrimaryButton } from "./bot-channel-card";

export type BotSessionProps = { companyId: string; userId: string; generation: number; isCurrent: (generation: number) => boolean;
  validate: (generation: number) => Promise<boolean>; request: (generation: number, url: string, init?: RequestInit) => Promise<Response>; onUnauthorized: () => void };
class BotFailure extends Error { constructor(readonly status: number) { super("Bot settings unavailable"); } }
export function BotChannelPanel(props: BotSessionProps) {
  const { companyId, userId, generation } = props, scope = `${companyId}:${userId}:${generation}`;
  const [state, setState] = useState<{ scope: string; view: BotAdminView | null }>({ scope, view: null });
  const [error, setError] = useState(""), [notice, setNotice] = useState(""), [busy, setBusy] = useState(false);
  const [baseUrl, setBaseUrl] = useState(""), [model, setModel] = useState(""), [key, setKey] = useState(""), [clearKey, setClearKey] = useState(false);
  const [qr, setQr] = useState<Partial<Record<BotChannel, BotQr>>>({}), [audit, setAudit] = useState<BotAudit[]>([]);
  const live = useRef(scope); live.current = scope;
  const mounted = useRef(false), serial = useRef(0), saving = useRef(false), abort = useRef<AbortController | null>(null);
  const view = state.scope === scope && props.isCurrent(generation) ? state.view : null;
  const current = (value: number) => mounted.current && live.current === scope && serial.current === value && props.isCurrent(generation);
  function discard() { setState({ scope, view: null }); setKey(""); setQr({}); setAudit([]); setBaseUrl(""); setModel(""); }
  async function call(path: string, value: number, init?: RequestInit) {
    if (!await props.validate(generation) || !current(value)) { if (current(value)) discard(); return null; }
    const response = await props.request(generation, `/api/local/bot-channels${path}?companyId=${encodeURIComponent(companyId)}`, { ...init, cache: "no-store", signal: abort.current?.signal });
    if (!current(value)) return null;
    if (response.status === 401) { discard(); props.onUnauthorized(); return null; }
    if (!response.ok) throw new BotFailure(response.status);
    const body: unknown = await response.json();
    if (!await props.validate(generation) || !current(value)) { if (current(value)) discard(); return null; }
    return body;
  }
  async function load(value: number) {
    const raw = await call("", value); if (raw === null || !current(value)) return;
    const next = parseBotAdmin(raw); if (!next) throw new Error();
    setState({ scope, view: next }); setBaseUrl(next.ai.baseUrl); setModel(next.ai.model); setKey(""); setClearKey(false);
  }
  function failure(e: unknown) {
    if (e instanceof BotFailure && [403, 404].includes(e.status)) discard();
    setError(e instanceof BotFailure && e.status === 409 ? "Cấu hình vừa thay đổi. Tải lại trước khi lưu tiếp."
      : e instanceof BotFailure && e.status === 400 ? "Cấu hình chưa hợp lệ. Kiểm tra giờ, nhóm và thông tin AI."
        : e instanceof BotFailure && e.status === 403 ? "Bạn không còn quyền quản trị công ty này."
          : e instanceof BotFailure && e.status === 404 ? "Tính năng Kênh & Bot chưa được bật hoặc mã QR chưa có."
            : "Chưa xác nhận được kết quả. Tải lại cấu hình trước khi thao tác tiếp.");
  }
  async function run(action: (value: number) => Promise<void>) {
    if (saving.current || !props.isCurrent(generation)) return;
    saving.current = true; abort.current?.abort(); abort.current = new AbortController(); const value = ++serial.current;
    setBusy(true); setError(""); setNotice("");
    try { await action(value); } catch (e) { if (current(value)) failure(e); }
    finally { if (current(value)) { saving.current = false; setBusy(false); setKey(""); } }
  }
  const latest = useRef({ load, run }); latest.current = { load, run };
  useEffect(() => {
    mounted.current = true; saving.current = false; setKey(""); setQr({}); setAudit([]); setError(""); setNotice("");
    void latest.current.run(latest.current.load);
    return () => { mounted.current = false; serial.current++; abort.current?.abort(); saving.current = false; };
  }, [scope]);
  useEffect(() => {
    const expiry = Object.values(qr).filter((v): v is BotQr => !!v).map(v => Date.parse(v.expiresAt));
    if (!expiry.length) return;
    const timer = setTimeout(() => setQr(old => Object.fromEntries(Object.entries(old).filter(([, v]) => v && Date.parse(v.expiresAt) > Date.now()))), Math.max(1, Math.min(...expiry) - Date.now()));
    return () => clearTimeout(timer);
  }, [qr]);
  function saveChannel(channel: BotChannelView, summarySchedule: string[]) {
    void run(async value => {
      const raw = await call(`/${channel.channel}`, value, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({
        expectedVersion: channel.configVersion, enabled: channel.enabled, dryRun: channel.dryRun, summarySchedule,
        techGroupExternalId: channel.techGroupExternalId, customerGroups: channel.customerGroups }) });
      if (raw === null) return; await load(value); if (current(value)) setNotice("Đã lưu cấu hình kênh.");
    });
  }
  async function fetchQr(channel: BotChannel, value: number) {
    try {
      const raw = await call(`/${channel}/qr`, value); if (raw === null || !current(value)) return;
      const next = parseBotQr(raw); if (!next) throw new Error(); setQr(old => ({ ...old, [channel]: next }));
    } catch (e) { if (e instanceof BotFailure && e.status === 404) { if (current(value)) { setQr(old => ({ ...old, [channel]: undefined })); setNotice("Chờ bot gửi mã QR mới, sau đó chọn Xem mã QR."); } } else throw e; }
  }
  function saveAi(event: FormEvent) {
    event.preventDefault(); if (!view) return;
    const input = { expectedVersion: view.ai.version, baseUrl, model, ...(key ? { apiKey: key } : {}), clearApiKey: clearKey };
    setKey(""); void run(async value => { const raw = await call("/ai", value, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(input) });
      if (raw === null) return; await load(value); if (current(value)) setNotice("Đã lưu thiết lập AI."); });
  }
  const field = "grid gap-1.5 text-xs font-medium text-muted-foreground";
  return <section aria-label="Kênh & Bot" className="space-y-5">
    <div className="flex flex-wrap items-center justify-between gap-3">
      <p className="text-sm text-muted-foreground">Bật/tắt bot, giờ tóm tắt và nhóm khách đều chỉnh ở đây. Bot nhận cấu hình mới trong lần đồng bộ tiếp theo.</p>
      <button className={botButton} disabled={busy} onClick={() => void run(load)}><RefreshCw className={`size-4 ${busy ? "animate-spin" : ""}`} aria-hidden="true" />Tải lại cấu hình</button>
    </div>
    <div aria-live="polite" className="space-y-1 text-sm empty:hidden">
      {busy ? <p role="status" className="text-muted-foreground">Đang xử lý…</p> : null}
      {notice ? <p role="status" className="text-emerald-700 dark:text-emerald-300">{notice}</p> : null}
    </div>
    {error ? <p role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:border-rose-400/30 dark:bg-rose-400/10 dark:text-rose-200">{error}</p> : null}
    {view ? <><div className="grid items-start gap-4 xl:grid-cols-2">{view.channels.map(channel => <BotChannelCard key={`${scope}:${channel.channel}:${channel.configVersion}:${channel.status?.receivedAt ?? ""}`} channel={channel} busy={busy}
      qr={qr[channel.channel] && Date.parse(qr[channel.channel]!.expiresAt) > Date.now() ? qr[channel.channel]! : null} save={saveChannel}
      refreshQr={() => void run(value => fetchQr(channel.channel, value))} command={kind => void run(async value => {
        setQr(old => ({ ...old, [channel.channel]: undefined }));
        const raw = await call(`/${channel.channel}/commands`, value, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ kind }) });
        if (raw === null || !current(value)) return; setNotice(kind === "relogin" ? "Đã yêu cầu đăng nhập lại. Chờ bot gửi mã QR mới." : "Đã xếp lệnh tóm tắt; chờ bot nhận lệnh.");
        if (kind === "relogin") await fetchQr(channel.channel, value);
      })} />)}</div>
      <div className="grid items-start gap-4 xl:grid-cols-2">
      <form onSubmit={saveAi} aria-label="Thiết lập AI" className="overflow-hidden rounded-xl border bg-card shadow-sm">
        <div className="border-b px-5 py-4"><h3 className="text-base font-semibold">Thiết lập AI dùng chung</h3>
          <p className="text-xs text-muted-foreground">Mô hình bot dùng để tóm tắt tin nhắn và đọc ảnh.</p></div>
        <fieldset disabled={busy} className="m-0 grid gap-4 border-0 p-5 disabled:opacity-70"><label className={field}>Địa chỉ API AI<input type="url" required maxLength={1000} className={botInput} value={baseUrl} onChange={e => setBaseUrl(e.target.value)} /></label>
          <label className={field}>Mô hình AI<input required maxLength={200} className={botInput} value={model} onChange={e => setModel(e.target.value)} /></label>
          <p className="text-sm">{view.ai.apiKeyConfigured ? "Đã cấu hình khóa API." : "Chưa cấu hình khóa API."} Để trống để giữ khóa hiện tại.</p>
          <label className={field}>Khóa API mới<input type="password" autoComplete="new-password" maxLength={4096} disabled={clearKey} className={botInput} value={key} onChange={e => setKey(e.target.value)} /></label>
          <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={clearKey} onChange={e => { setClearKey(e.target.checked); setKey(""); }} />Xóa khóa API đã lưu</label>
          <div className="flex flex-wrap items-center gap-2"><button className={botPrimaryButton} type="submit">Lưu thiết lập AI</button><button className={botButton} type="button" onClick={() => void run(async value => {
            const result = await call("/ai/test", value, { method: "POST" }); if (!botObject(result) || !current(value)) return;
            setNotice(result.success === true ? "Kết nối AI thành công." : result.code === "host_not_allowed" ? "Địa chỉ AI chưa được hệ thống cho phép kiểm tra. Liên hệ người vận hành."
              : "Không kết nối được AI. Kiểm tra địa chỉ và khóa đã lưu.");
          })}>Kiểm tra kết nối</button><span className="text-xs text-muted-foreground">Kiểm tra sử dụng thiết lập đã lưu.</span></div>
        </fieldset></form>
      <section aria-label="Lịch sử thay đổi" className="overflow-hidden rounded-xl border bg-card shadow-sm">
        <div className="flex items-center justify-between gap-3 border-b px-5 py-4"><h3 className="text-base font-semibold">Lịch sử thay đổi</h3>
          <button className={botButton} disabled={busy} onClick={() => void run(async value => {
            const raw = await call("/audit", value); if (raw === null || !current(value)) return; const rows = parseBotAudit(raw); if (!rows) throw new Error(); setAudit(rows);
          })}>Xem lịch sử thay đổi</button></div>
        {audit.length ? <ul className="m-0 list-none divide-y p-0 text-sm">{audit.map(row => <li key={row.id} className="grid gap-0.5 px-5 py-2.5">
          <span>{row.channel ?? "AI dùng chung"} · {row.field}</span>
          <span className="break-all text-xs text-muted-foreground">{new Date(row.timestamp).toLocaleString("vi-VN")} · {row.actorUserId}</span></li>)}</ul>
          : <p className="px-5 py-4 text-sm text-muted-foreground">Ai đã đổi gì, lúc nào. Bấm nút để xem.</p>}
      </section>
      </div>
    </> : null}</section>;
}

export function useBotChannels(props: BotSessionProps, enabled: boolean): BotAdminView | null {
  const [loaded, setLoaded] = useState<{ scope: string; view: BotAdminView } | null>(null), scope = `${props.companyId}:${props.userId}:${props.generation}`;
  const latest = useRef(props); latest.current = props;
  useEffect(() => {
    let active = true; const controller = new AbortController(); setLoaded(null);
    if (enabled) void (async () => {
      const p = latest.current;
      try {
        if (!await p.validate(p.generation) || !active || !p.isCurrent(p.generation)) return;
        const response = await p.request(p.generation, `/api/local/bot-channels?companyId=${encodeURIComponent(p.companyId)}`, { cache: "no-store", signal: controller.signal });
        if (!active || !p.isCurrent(p.generation)) return;
        if (!response.ok) return;
        const view = parseBotAdmin(await response.json());
        if (view && await p.validate(p.generation) && active && p.isCurrent(p.generation)) setLoaded({ scope, view });
      } catch { /* A missing or disabled feature has no navigation entry. */ }
    })();
    return () => { active = false; controller.abort(); };
  }, [scope, enabled]);
  return enabled && loaded?.scope === scope && props.isCurrent(props.generation) ? loaded.view : null;
}

export function useBotChannelsAvailable(props: BotSessionProps, enabled: boolean) {
  return useBotChannels(props, enabled) !== null;
}
