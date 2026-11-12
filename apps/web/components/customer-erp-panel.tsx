"use client";

import { FormEvent, useEffect, useRef, useState } from "react";
import { Check, Loader2 } from "lucide-react";
import { CustomerErpSource, ErpConnection, ErpFinding, ErpServer, erpFindingLabels, erpMessages, parseErpFindings, isErpConnection, parseCustomerErpSources, parseErpServers, readOnlyLoginSql } from "../lib/customer-erp";
import { ErpSend, ErpServerForm, ErpServerSourceForm } from "./erp-server-forms";

type Props = {
  companyId: string; generation: number; isCurrent: (generation: number) => boolean;
  validate: (generation: number) => Promise<boolean>;
  request: (generation: number, url: string, init?: RequestInit) => Promise<Response>;
  onUnauthorized: () => void; onSaved: () => void;
};
const empty: ErpConnection = { host: "", port: 1433, database: "", userName: "", password: "", encrypt: true, trustServerCertificate: false };

export function CustomerErpPanel({ companyId, generation, isCurrent, validate, request, onUnauthorized, onSaved }: Props) {
  const [sources, setSources] = useState<CustomerErpSource[]>([]);
  const [fields, setFields] = useState<ErpConnection>(empty);
  const [name, setName] = useState("");
  const [editing, setEditing] = useState<CustomerErpSource | null>(null);
  const [code, setCode] = useState("");
  const [details, setDetails] = useState("");
  const [findings, setFindings] = useState<ErpFinding[]>([]);
  const [tested, setTested] = useState(false);
  const [busy, setBusy] = useState(false);
  const [action, setAction] = useState<"test" | "save" | "reader" | null>(null);
  const [createdReader, setCreatedReader] = useState("");
  const [formOpen, setFormOpen] = useState(false);
  const [copied, setCopied] = useState(false);
  const [pending, setPending] = useState<{ operationId: string } | null>(null);
  // Shared servers: one address and login for several databases. "new" opens an empty server form.
  const [servers, setServers] = useState<ErpServer[]>([]);
  const [serversFailed, setServersFailed] = useState(false);
  const [serverForm, setServerForm] = useState<ErpServer | "new" | null>(null);
  const [serverSource, setServerSource] = useState<{ source: CustomerErpSource | null } | null>(null);
  const mounted = useRef(false), lock = useRef(false);
  const current = () => mounted.current && isCurrent(generation);
  const selector = `companyId=${encodeURIComponent(companyId)}`;

  async function authorized(response: Response) {
    if (!current()) return false;
    if (response.status === 401) { onUnauthorized(); return false; }
    if (response.status === 403) {
      setFields(empty); setTested(false); setPending(null); setSources([]); setEditing(null); setName("");
      setServers([]); setServerForm(null); setServerSource(null);
      await validate(generation);
      if (current()) setCode("unavailable");
      return false;
    }
    return await validate(generation) && current();
  }

  async function load() {
    try {
      if (!await validate(generation) || !current()) return;
      const response = await request(generation, `/api/local/data-sources/customer-erp?${selector}`, { cache: "no-store" });
      if (!await authorized(response)) return;
      const rows = response.ok ? parseCustomerErpSources(await response.json()) : null;
      if (!current()) return;
      if (!rows) { setCode("load_failed"); return; }
      setSources(rows);
    } catch { if (current()) setCode("load_failed"); return; }
    // Shared servers are optional: if they cannot be loaded the sources above still work, so this never raises the panel alert.
    try {
      const listed = await request(generation, `/api/local/data-sources/erp-servers?${selector}`, { cache: "no-store" });
      if (!current()) return;
      if (listed.status === 401) { onUnauthorized(); return; }
      const shared = listed.ok ? parseErpServers(await listed.json()) : null;
      if (!current()) return;
      setServers(shared ?? []); setServersFailed(!shared);
    } catch { if (current()) { setServers([]); setServersFailed(true); } }
  }

  const send: ErpSend = async (path, method, body) => {
    if (lock.current || !current()) return null;
    lock.current = true; setBusy(true);
    try {
      if (!await validate(generation) || !current()) return null;
      const response = await request(generation, `/api/local/data-sources/${path}?${selector}`, {
        method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body),
      });
      if (!await authorized(response)) return null;
      let payload: Record<string, unknown> = {};
      try { payload = await response.json(); } catch { /* Reported as unavailable by the form. */ }
      return current() ? { status: response.status, payload } : null;
    } catch { return current() ? { status: 0, payload: {} } : null; }
    finally { lock.current = false; if (current()) setBusy(false); }
  };
  async function sharedSaved(message: string) {
    setServerForm(null); setServerSource(null); setCode(message);
    await load(); if (current()) onSaved();
  }

  useEffect(() => {
    mounted.current = true;
    void load();
    return () => { mounted.current = false; };
    // Parent remounts on every company/session authority generation.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function change<K extends keyof ErpConnection>(key: K, value: ErpConnection[K]) {
    setFields(previous => ({ ...previous, [key]: value })); setTested(false); setCode(""); setDetails(""); setFindings([]); setCreatedReader("");
  }
  // SQL Server tools write the address as "host,port" (or "host:port"); split it into the two fields once the
  // address is complete (pasted or the field is left), never while it is still being typed.
  function settleHost(value: string) {
    const match = /^\s*([^\s,:;\\/=]+)\s*[,:]\s*(\d{1,5})\s*$/.exec(value);
    if (match && Number(match[2]) >= 1 && Number(match[2]) <= 65535) {
      setFields(previous => ({ ...previous, host: match[1], port: Number(match[2]) })); setTested(false); setCode(""); setDetails(""); setFindings([]); setCreatedReader("");
    } else change("host", value.trim());
  }

  async function execute(save: boolean, connection = fields) {
    if (lock.current || !current() || !isErpConnection(connection) || (save && (!tested || !name.trim()))) return;
    lock.current = true; setBusy(true); setAction(save ? "save" : "test"); setCode(""); setDetails(""); setFindings([]);
    try {
      if (!await validate(generation) || !current()) return;
      const operation = pending ?? { operationId: crypto.randomUUID() };
      const body = save ? { ...connection, logicalName: name.trim(), ...operation, ...(editing ? { bindingVersion: editing.bindingVersion } : {}) } : connection;
      if (save) setPending(operation);
      const path = save ? (editing ? `${editing.id}/credentials` : "customer-erp") : "connection-test";
      const response = await request(generation, `/api/local/data-sources/${path}?${selector}`, {
        method: save && editing ? "PUT" : "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body),
      });
      if (!await authorized(response)) return;
      let payload: Record<string, unknown> = {};
      try { payload = await response.json(); } catch { /* An uncertain save keeps its operation ID. */ }
      if (!current()) return;
      if (save && response.ok) {
        if (!parseCustomerErpSources([payload])) { setCode("unavailable"); return; }
        setFields(empty); setName(""); setEditing(null); setPending(null); setTested(false); setCode("saved"); setCreatedReader(""); setFormOpen(false);
        await load(); if (current()) onSaved();
        return;
      }
      const resultCode = response.status === 429 ? "rate_limited" : typeof payload.code === "string" && Object.hasOwn(erpMessages, payload.code) ? payload.code : "unavailable";
      setCode(resultCode);
      setFindings(parseErpFindings(payload.findings));
      if (!save) {
        setTested(response.ok && resultCode === "ok");
        if (response.ok && resultCode === "ok" && typeof payload.serverVersion === "string" && typeof payload.tableCount === "number")
          setDetails(`SQL Server ${payload.serverVersion} · ${payload.tableCount} bảng`);
      } else if ([400, 409, 422, 429].includes(response.status)) { setPending(null); setTested(false); }
    } catch { if (current()) setCode("unavailable"); }
    finally { lock.current = false; if (current()) { setBusy(false); setAction(null); } }
  }

  // The administrator account in the form is used once by the server to create a new read-only login; it is never saved.
  async function createReader() {
    if (lock.current || !current() || !isErpConnection(fields)) return;
    lock.current = true; setBusy(true); setAction("reader"); setCode(""); setDetails(""); setFindings([]); setCreatedReader("");
    let reader: { userName: string; password: string } | null = null;
    try {
      if (!await validate(generation) || !current()) return;
      const response = await request(generation, `/api/local/data-sources/reader-login?${selector}`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(fields),
      });
      if (!await authorized(response)) return;
      let payload: Record<string, unknown> = {};
      try { payload = await response.json(); } catch { /* Reported as unavailable below. */ }
      if (!current()) return;
      if (response.ok && payload.code === "created" && typeof payload.userName === "string" && /^aioffice_reader_[0-9a-f]{8}$/.test(payload.userName)
        && typeof payload.password === "string" && payload.password.length >= 16 && payload.password.length <= 128)
        reader = { userName: payload.userName, password: payload.password };
      else setCode(response.status === 429 ? "rate_limited" : typeof payload.code === "string" && payload.code !== "created" && Object.hasOwn(erpMessages, payload.code) ? payload.code : "unavailable");
    } catch { if (current()) setCode("unavailable"); }
    finally { lock.current = false; if (current()) { setBusy(false); setAction(null); } }
    if (!reader || !current()) return;
    const connection = { ...fields, ...reader };
    setFields(connection); setCreatedReader(reader.userName);
    await execute(false, connection);
  }

  function resetForm(source: CustomerErpSource | null, open = source !== null) {
    setFormOpen(open); setFields(empty); setCreatedReader(""); setName(source?.logicalName ?? ""); setEditing(source); setTested(false); setCode(""); setDetails(""); setFindings([]); setPending(null);
  }
  const frozen = busy || pending !== null;
  const sharedOpen = serverForm !== null || serverSource !== null;
  const outline = "rounded-lg border px-4 py-2 text-sm hover:bg-muted disabled:opacity-50";
  const inputClass = "mt-1 w-full rounded-lg border border-slate-200 p-2 dark:border-white/15 dark:bg-slate-900";
  return <section aria-label="Kết nối ERP khách hàng" className="rounded-xl border p-5">
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div>
        <h3 className="font-semibold">Kết nối ERP khách hàng</h3>
        <p className="mt-1 text-sm text-muted-foreground">Database SQL Server của khách mà Trợ lý được đọc. Chỉ đọc, mật khẩu được mã hoá và không hiển thị lại.</p>
      </div>
      <div className="flex flex-wrap gap-2">
        <button type="button" disabled={busy} onClick={() => void load()} className={outline}>Tải lại</button>
        {!formOpen && !sharedOpen && servers.length ? <button type="button" onClick={() => { setCode(""); setServerSource({ source: null }); }} className={outline}>Thêm database trên máy chủ chung</button> : null}
        {!formOpen && !sharedOpen ? <button type="button" onClick={() => resetForm(null, true)} className="rounded-lg bg-primary px-4 py-2 text-sm text-primary-foreground hover:bg-primary/90">Thêm kết nối ERP</button> : null}
      </div>
    </div>
    <div className="mt-4 rounded-lg border p-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h4 className="text-sm font-semibold">Máy chủ dùng chung</h4>
          <p className="mt-1 text-sm text-muted-foreground">Nhiều database cùng một máy chủ và tài khoản: đổi máy chủ hoặc mật khẩu ở đây một lần, mọi database dùng nó được cập nhật cùng lúc.</p>
        </div>
        {!formOpen && !sharedOpen ? <button type="button" onClick={() => { setCode(""); setServerForm("new"); }} className={outline}>Thêm máy chủ</button> : null}
      </div>
      {servers.length ? <ul aria-label="Máy chủ dùng chung" className="mt-3 space-y-2">{servers.map(server => <li key={server.id} className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-muted p-3 text-sm">
        <span><span className="font-medium">{server.name}</span> · {server.host}:{server.port} · tài khoản {server.userName} · {server.sourceCount} database</span>
        <button type="button" disabled={frozen || formOpen || sharedOpen} onClick={() => { setCode(""); setServerForm(server); }} className="rounded-lg border bg-card px-3 py-2 hover:bg-muted disabled:opacity-50">Sửa máy chủ<span className="sr-only"> {server.name}</span></button>
      </li>)}</ul> : serversFailed ? <p className="mt-3 text-sm text-muted-foreground">Chưa tải được danh sách máy chủ dùng chung. Bấm “Tải lại” để thử lại.</p> : null}
      {serverForm ? <ErpServerForm key={serverForm === "new" ? "new" : serverForm.id} server={serverForm === "new" ? null : serverForm} send={send}
        onSaved={() => void sharedSaved("server_saved")} onCancel={() => setServerForm(null)} /> : null}
    </div>
    {sources.length ? <ul className="mt-4 space-y-2">{sources.map(source => {
      const server = source.serverId ? servers.find(item => item.id === source.serverId) : undefined;
      return <li key={source.id} className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-muted p-3 text-sm">
        <span><span className="font-medium">{source.logicalName}</span> · {source.isEnabled ? "Đang bật · chỉ đọc" : "Đã tắt"}
          {source.serverId ? <> · máy chủ {server?.name ?? "dùng chung"} / {source.database}</> : null}</span>
        <span className="flex flex-wrap gap-2">
          {source.serverId ? <button type="button" disabled={frozen || formOpen || sharedOpen} onClick={() => { setCode(""); setServerSource({ source }); }} className="rounded-lg border bg-card px-3 py-2 hover:bg-muted disabled:opacity-50">Đổi database<span className="sr-only"> {source.logicalName}</span></button>
            : <>
              {servers.length ? <button type="button" disabled={frozen || formOpen || sharedOpen} onClick={() => { setCode(""); setServerSource({ source }); }} className="rounded-lg border bg-card px-3 py-2 hover:bg-muted disabled:opacity-50">Dùng máy chủ chung<span className="sr-only"> {source.logicalName}</span></button> : null}
              <button type="button" disabled={frozen || sharedOpen} onClick={() => resetForm(source)} className="rounded-lg border bg-card px-3 py-2 hover:bg-muted disabled:opacity-50">Đổi mật khẩu<span className="sr-only"> {source.logicalName}</span></button>
            </>}
        </span>
      </li>;
    })}</ul> : !formOpen && !sharedOpen && code !== "load_failed" ? <p className="mt-4 rounded-lg bg-muted p-4 text-sm text-muted-foreground">Chưa có kết nối ERP nào. Bấm “Thêm kết nối ERP” để bắt đầu.</p> : null}
    {serverSource ? <ErpServerSourceForm key={serverSource.source?.id ?? "new"} servers={servers} source={serverSource.source} send={send}
      onSaved={() => void sharedSaved("saved")} onCancel={() => setServerSource(null)} /> : null}
    {!formOpen && code ? <p role={code === "saved" || code === "server_saved" ? "status" : "alert"} className="mt-4 text-sm">{code === "saved" ? "Đã lưu kết nối ERP khách hàng."
      : code === "server_saved" ? "Đã lưu máy chủ dùng chung; các database dùng máy chủ này đã được kiểm tra và cập nhật." : erpMessages[code] ?? erpMessages.unavailable}</p> : null}
    {formOpen ? <form aria-label={editing ? "Đổi kết nối ERP" : "Thêm kết nối ERP"} className="mt-4 rounded-lg border p-4" onSubmit={(event: FormEvent) => { event.preventDefault(); void execute(true); }}>
      {editing ? <p className="mt-4 text-sm">Nhập lại máy chủ, database và tài khoản cùng mật khẩu mới cho {editing.logicalName}.</p> : null}
      <fieldset disabled={frozen} className="mt-4 grid gap-4 sm:grid-cols-2">
        <label className="text-sm">Tên khách hàng<input value={name} maxLength={200} readOnly={editing !== null} onChange={event => { setName(event.target.value); }} className={inputClass} required /></label>
        <label className="text-sm">Máy chủ<input value={fields.host} maxLength={253} onChange={event => change("host", event.target.value)}
          onBlur={event => settleHost(event.target.value)} onPaste={event => {
            const input = event.currentTarget, pasted = event.clipboardData.getData("text");
            const next = input.value.slice(0, input.selectionStart ?? input.value.length) + pasted + input.value.slice(input.selectionEnd ?? input.value.length);
            event.preventDefault(); settleHost(next);
          }} placeholder="sql.khachhang.vn" className={inputClass} required autoComplete="off" /></label>
        <label className="text-sm">Cổng<input type="number" min={1} max={65535} value={fields.port} onChange={event => change("port", Number(event.target.value))} className={inputClass} required /></label>
        <label className="text-sm">Tên database<input value={fields.database} maxLength={128} onChange={event => change("database", event.target.value)} className={inputClass} required autoComplete="off" /></label>
        <label className="text-sm">Tài khoản<input value={fields.userName} maxLength={128} onChange={event => change("userName", event.target.value)} className={inputClass} required autoComplete="off" /></label>
        <label className="text-sm">Mật khẩu<input type="password" value={fields.password} maxLength={1024} onChange={event => change("password", event.target.value)} className={inputClass} required autoComplete="new-password" /></label>
        <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={fields.encrypt} onChange={event => change("encrypt", event.target.checked)} />Mã hoá đường truyền</label>
        <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={fields.trustServerCertificate} onChange={event => change("trustServerCertificate", event.target.checked)} />Tin cậy chứng chỉ máy chủ (chỉ mạng được quản lý)</label>
      </fieldset>
      {!fields.encrypt ? <p className="mt-3 text-sm text-amber-700">Đường truyền có thể không được mã hoá. Nên bật mã hoá và dùng chứng chỉ TLS hợp lệ.</p> : null}
      {code ? <p role={code === "ok" || code === "saved" ? "status" : "alert"} className="mt-4 text-sm">{code === "saved" ? "Đã lưu kết nối ERP khách hàng." : erpMessages[code] ?? erpMessages.unavailable} {details}</p> : null}
      {findings.length > 0 && code && code !== "ok" ? <div className="mt-3 rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm dark:border-amber-500/40 dark:bg-amber-500/10">
        <p className="font-medium">Điều kiện không đạt:</p>
        <ul aria-label="Điều kiện không đạt" className="mt-1 list-disc space-y-1 pl-5">{findings.map(item => <li key={item.code}>
          {erpFindingLabels[item.code] ?? item.code}{item.writeRisk ? " (quyền ghi/quản trị)" : ""}{item.detail ? <>: <code className="text-xs">{item.detail}</code></> : null}</li>)}</ul>
      </div> : null}
      {createdReader ? <p role="status" className="mt-4 rounded-lg bg-emerald-50 p-3 text-sm text-emerald-800 dark:bg-emerald-500/10 dark:text-emerald-300">
        Đã tạo tài khoản chỉ đọc <strong>{createdReader}</strong> và điền sẵn mật khẩu. Mật khẩu quản trị không được lưu lại.{tested ? " Bấm Lưu để lưu kết nối." : ""}</p> : null}
      {code === "not_read_only" || code === "missing_view_definition" || code === "not_permitted" || action === "reader" ? <div className="mt-4 rounded-lg border p-4">
        <p className="text-sm">Nếu tài khoản vừa nhập là quản trị SQL (ví dụ sa), AI Office có thể dùng nó một lần để tạo tài khoản chỉ đọc mới cho database này và điền sẵn vào form. Mật khẩu quản trị không được lưu.</p>
        <button type="button" disabled={frozen || !isErpConnection(fields)} onClick={() => void createReader()} className="mt-3 inline-flex items-center gap-2 rounded-lg bg-primary px-4 py-2 text-sm text-primary-foreground hover:bg-primary/90 disabled:opacity-50">
          {action === "reader" ? <Loader2 className="size-4 animate-spin" aria-hidden="true" /> : null}{action === "reader" ? "Đang tạo tài khoản…" : "Tạo tài khoản chỉ đọc giúp tôi"}</button>
        <details className="mt-3">
        <summary className="cursor-pointer text-sm text-muted-foreground">Hoặc gửi script SQL cho quản trị SQL tự chạy</summary>
        <pre aria-label="SQL tạo tài khoản chỉ đọc" className="mt-2 max-h-72 overflow-auto rounded-lg bg-slate-100 p-3 text-xs dark:bg-slate-900">{readOnlyLoginSql(fields.database)}</pre>
        <button type="button" onClick={() => {
          void navigator.clipboard?.writeText(readOnlyLoginSql(fields.database)).then(() => {
            if (!current()) return; setCopied(true); setTimeout(() => { if (mounted.current) setCopied(false); }, 2000);
          }).catch(() => {});
        }} className="mt-2 inline-flex items-center gap-2 rounded-lg border px-3 py-2 text-sm hover:bg-muted">
          {copied ? <><Check className="size-4" aria-hidden="true" />Đã sao chép</> : "Sao chép SQL"}</button>
        </details>
      </div> : null}
      {!frozen && !tested && !code ? <p className="mt-4 text-sm text-muted-foreground">{isErpConnection(fields)
        ? "Bấm “Kiểm tra kết nối” trước; nút Lưu bật khi kết nối thành công."
        : "Điền đủ máy chủ (chỉ địa chỉ, cổng nhập ở ô Cổng), database, tài khoản và mật khẩu để kiểm tra kết nối."}</p> : null}
      <div className="mt-4 flex flex-wrap gap-3">
        <button type="button" disabled={frozen || !isErpConnection(fields)} onClick={() => void execute(false)} className={`${outline} inline-flex items-center gap-2`}>
          {action === "test" ? <Loader2 className="size-4 animate-spin" aria-hidden="true" /> : null}Kiểm tra kết nối</button>
        <button type="submit" disabled={busy || !tested || !name.trim()} className="inline-flex items-center gap-2 rounded-lg bg-primary px-4 py-2 text-sm text-primary-foreground hover:bg-primary/90 disabled:opacity-50">
          {action === "save" ? <Loader2 className="size-4 animate-spin" aria-hidden="true" /> : null}{action === "save" ? "Đang lưu…" : pending ? "Thử lại lưu" : "Lưu"}</button>
        <button type="button" disabled={busy} onClick={() => resetForm(null)} className={outline}>Huỷ</button>
      </div>
    </form> : null}
  </section>;
}
