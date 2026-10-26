"use client";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { botObject, parseBotAdmin, parseBotAudit, parseBotQr, type BotAdminView, type BotAudit, type BotChannel, type BotChannelView, type BotQr } from "../lib/bot-channels";
import { BotChannelCard, botButton } from "./bot-channel-card";

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
  return <section aria-label="Kênh & Bot" className="space-y-5"><header className="flex items-center justify-between"><h2 className="text-2xl font-semibold">Kênh & Bot</h2>
    <button className={botButton} disabled={busy} onClick={() => void run(load)}>Tải lại cấu hình</button></header>
    <p className="text-sm text-slate-500">Quản lý bot tiếp nhận Zalo và WhatsApp. Bot nhận cấu hình trong lần đồng bộ tiếp theo.</p>
    {error ? <p role="alert">{error}</p> : null}{notice ? <p role="status">{notice}</p> : null}{busy ? <p role="status">Đang xử lý…</p> : null}
    {view ? <><div className="grid gap-5 xl:grid-cols-2">{view.channels.map(channel => <BotChannelCard key={`${scope}:${channel.channel}:${channel.configVersion}:${channel.status?.receivedAt ?? ""}`} channel={channel} busy={busy}
      qr={qr[channel.channel] && Date.parse(qr[channel.channel]!.expiresAt) > Date.now() ? qr[channel.channel]! : null} save={saveChannel}
      refreshQr={() => void run(value => fetchQr(channel.channel, value))} command={kind => void run(async value => {
        setQr(old => ({ ...old, [channel.channel]: undefined }));
        const raw = await call(`/${channel.channel}/commands`, value, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ kind }) });
        if (raw === null || !current(value)) return; setNotice(kind === "relogin" ? "Đã yêu cầu đăng nhập lại. Chờ bot gửi mã QR mới." : "Đã xếp lệnh tóm tắt; chờ bot nhận lệnh.");
        if (kind === "relogin") await fetchQr(channel.channel, value);
      })} />)}</div>
      <form onSubmit={saveAi} aria-label="Thiết lập AI" className="grid gap-4 rounded-2xl border border-slate-200 p-5 dark:border-white/10"><h3 className="text-lg font-semibold">Thiết lập AI dùng chung</h3>
        <fieldset disabled={busy} className="grid gap-4"><label className="grid gap-1">Địa chỉ API AI<input type="url" required maxLength={1000} className="rounded-lg border bg-transparent p-2" value={baseUrl} onChange={e => setBaseUrl(e.target.value)} /></label>
          <label className="grid gap-1">Mô hình AI<input required maxLength={200} className="rounded-lg border bg-transparent p-2" value={model} onChange={e => setModel(e.target.value)} /></label>
          <p>{view.ai.apiKeyConfigured ? "Đã cấu hình khóa API." : "Chưa cấu hình khóa API."} Để trống để giữ khóa hiện tại.</p>
          <label className="grid gap-1">Khóa API mới<input type="password" autoComplete="new-password" maxLength={4096} disabled={clearKey} className="rounded-lg border bg-transparent p-2" value={key} onChange={e => setKey(e.target.value)} /></label>
          <label className="flex gap-2"><input type="checkbox" checked={clearKey} onChange={e => { setClearKey(e.target.checked); setKey(""); }} />Xóa khóa API đã lưu</label>
          <div className="flex gap-2"><button className={botButton} type="submit">Lưu thiết lập AI</button><button className={botButton} type="button" onClick={() => void run(async value => {
            const result = await call("/ai/test", value, { method: "POST" }); if (!botObject(result) || !current(value)) return;
            setNotice(result.success === true ? "Kết nối AI thành công." : result.code === "host_not_allowed" ? "Địa chỉ AI chưa được hệ thống cho phép kiểm tra. Liên hệ người vận hành."
              : "Không kết nối được AI. Kiểm tra địa chỉ và khóa đã lưu.");
          })}>Kiểm tra kết nối</button></div><p className="text-xs text-slate-500">Kiểm tra sử dụng thiết lập đã lưu.</p>
        </fieldset></form>
      <section className="space-y-3"><h3 className="font-semibold">Lịch sử thay đổi</h3><button className={botButton} disabled={busy} onClick={() => void run(async value => {
        const raw = await call("/audit", value); if (raw === null || !current(value)) return; const rows = parseBotAudit(raw); if (!rows) throw new Error(); setAudit(rows);
      })}>Xem lịch sử thay đổi</button><ul className="space-y-2 text-sm">{audit.map(row => <li key={row.id}>{new Date(row.timestamp).toLocaleString("vi-VN")} · {row.channel ?? "AI dùng chung"} · {row.field} · {row.actorUserId}</li>)}</ul></section>
    </> : null}</section>;
}

export function useBotChannelsAvailable(props: BotSessionProps, enabled: boolean) {
  const [available, setAvailable] = useState<string | null>(null), scope = `${props.companyId}:${props.userId}:${props.generation}`;
  const latest = useRef(props); latest.current = props;
  useEffect(() => {
    let active = true; const controller = new AbortController(); setAvailable(null);
    if (enabled) void (async () => {
      const p = latest.current;
      try {
        if (!await p.validate(p.generation) || !active || !p.isCurrent(p.generation)) return;
        const response = await p.request(p.generation, `/api/local/bot-channels?companyId=${encodeURIComponent(p.companyId)}`, { cache: "no-store", signal: controller.signal });
        if (!active || !p.isCurrent(p.generation)) return;
        if (!response.ok) return;
        const view = parseBotAdmin(await response.json());
        if (view && await p.validate(p.generation) && active && p.isCurrent(p.generation)) setAvailable(scope);
      } catch { /* A missing or disabled feature has no navigation entry. */ }
    })();
    return () => { active = false; controller.abort(); };
  }, [scope, enabled]);
  return enabled && available === scope && props.isCurrent(props.generation);
}
