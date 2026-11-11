"use client";

import { FormEvent, useState } from "react";
import { Loader2 } from "lucide-react";
import { CustomerErpSource, ErpFinding, ErpServer, ErpServerFields, erpFindingLabels, erpMessages, parseErpFindings } from "../lib/customer-erp";

/** Sends one request through the panel's session checks; null when the session changed or was refused (already handled). */
export type ErpSend = (path: string, method: "POST" | "PUT", body: unknown) => Promise<{ status: number; payload: Record<string, unknown> } | null>;

const inputClass = "mt-1 w-full rounded-lg border border-slate-200 p-2 dark:border-white/15 dark:bg-slate-900";
const outline = "rounded-lg border px-4 py-2 text-sm hover:bg-muted disabled:opacity-50";
const primary = "inline-flex items-center gap-2 rounded-lg bg-primary px-4 py-2 text-sm text-primary-foreground hover:bg-primary/90 disabled:opacity-50";

function Outcome({ code, details, findings, source }: { code: string; details?: string; findings: ErpFinding[]; source?: string }) {
  if (!code) return null;
  return <>
    <p role={code === "ok" ? "status" : "alert"} className="mt-4 text-sm">
      {source ? <>Database của <strong>{source}</strong>: </> : null}{erpMessages[code] ?? erpMessages.unavailable} {details}</p>
    {findings.length > 0 && code !== "ok" ? <div className="mt-3 rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm dark:border-amber-500/40 dark:bg-amber-500/10">
      <p className="font-medium">Điều kiện không đạt:</p>
      <ul aria-label="Điều kiện không đạt" className="mt-1 list-disc space-y-1 pl-5">{findings.map(item => <li key={item.code}>
        {erpFindingLabels[item.code] ?? item.code}{item.writeRisk ? " (quyền ghi/quản trị)" : ""}{item.detail ? <>: <code className="text-xs">{item.detail}</code></> : null}</li>)}</ul>
    </div> : null}
  </>;
}

function resultCode(status: number, payload: Record<string, unknown>) {
  return status === 429 ? "rate_limited" : typeof payload.code === "string" && Object.hasOwn(erpMessages, payload.code) ? payload.code : "unavailable";
}

/** Adds or changes a shared server. Saving a change re-checks every database on the server and updates them together. */
export function ErpServerForm({ server, send, onSaved, onCancel }: {
  server: ErpServer | null; send: ErpSend; onSaved: () => void; onCancel: () => void;
}) {
  const [fields, setFields] = useState<ErpServerFields>(server
    ? { name: server.name, host: server.host, port: server.port, userName: server.userName, password: "", encrypt: server.encrypt, trustServerCertificate: server.trustServerCertificate }
    : { name: "", host: "", port: 1433, userName: "", password: "", encrypt: true, trustServerCertificate: false });
  const [testDatabase, setTestDatabase] = useState("");
  const [action, setAction] = useState<"test" | "save" | null>(null);
  const [code, setCode] = useState(""), [details, setDetails] = useState(""), [source, setSource] = useState("");
  const [findings, setFindings] = useState<ErpFinding[]>([]);
  const [operation, setOperation] = useState<string | null>(null);
  const change = <K extends keyof ErpServerFields>(key: K, value: ErpServerFields[K]) => {
    setFields(previous => ({ ...previous, [key]: value })); setCode(""); setDetails(""); setSource(""); setFindings([]); setOperation(null);
  };
  const complete = fields.name.trim().length > 0 && fields.host.trim().length > 0 && Number.isInteger(fields.port) && fields.port >= 1 && fields.port <= 65535
    && (server !== null || (fields.userName.trim().length > 0 && fields.password.length > 0));
  // Testing needs the full login; when editing with the password left blank, the save itself checks every database.
  const testable = complete && fields.userName.trim().length > 0 && fields.password.length > 0 && testDatabase.trim().length > 0;

  async function test() {
    if (action || !testable) return;
    setAction("test"); setCode(""); setDetails(""); setSource(""); setFindings([]);
    try {
      const result = await send("connection-test", "POST", { host: fields.host.trim(), port: fields.port, database: testDatabase.trim(),
        userName: fields.userName.trim(), password: fields.password, encrypt: fields.encrypt, trustServerCertificate: fields.trustServerCertificate });
      if (!result) return;
      const next = resultCode(result.status, result.payload);
      setCode(next); setFindings(parseErpFindings(result.payload.findings));
      if (next === "ok" && typeof result.payload.serverVersion === "string" && typeof result.payload.tableCount === "number")
        setDetails(`SQL Server ${result.payload.serverVersion} · ${result.payload.tableCount} bảng`);
    } finally { setAction(null); }
  }

  async function save(event: FormEvent) {
    event.preventDefault();
    if (action || !complete) return;
    setAction("save"); setCode(""); setDetails(""); setSource(""); setFindings([]);
    const operationId = operation ?? crypto.randomUUID();
    setOperation(operationId);
    try {
      const body = { operationId, ...fields, name: fields.name.trim(), host: fields.host.trim(), userName: fields.userName.trim(), ...(server ? { version: server.version } : {}) };
      const result = await send(server ? `erp-servers/${server.id}` : "erp-servers", server ? "PUT" : "POST", body);
      if (!result) return;
      if (result.status >= 200 && result.status < 300) { onSaved(); return; }
      setCode(resultCode(result.status, result.payload)); setFindings(parseErpFindings(result.payload.findings));
      if (typeof result.payload.source === "string") setSource(result.payload.source.slice(0, 200));
      // A definite refusal needs a new operation; an uncertain failure retries the same one.
      if ([400, 409, 422, 429].includes(result.status)) setOperation(null);
    } finally { setAction(null); }
  }

  return <form aria-label={server ? "Sửa máy chủ dùng chung" : "Thêm máy chủ dùng chung"} className="mt-4 rounded-lg border p-4" onSubmit={event => void save(event)}>
    <p className="text-sm text-muted-foreground">{server
      ? `Đang dùng cho ${server.sourceCount} database. Khi lưu, AI Office kiểm tra lại tất cả rồi mới đổi cùng lúc; nếu một database không đạt thì không đổi gì.`
      : "Nhập một lần máy chủ và tài khoản chỉ đọc dùng chung cho nhiều database của khách."}</p>
    <fieldset disabled={action !== null} className="mt-4 grid gap-4 sm:grid-cols-2">
      <label className="text-sm">Tên máy chủ<input value={fields.name} maxLength={200} onChange={event => change("name", event.target.value)} placeholder="Máy chủ ERP chính" className={inputClass} required /></label>
      <label className="text-sm">Máy chủ<input value={fields.host} maxLength={253} onChange={event => change("host", event.target.value)}
        onBlur={event => {
          const match = /^\s*([^\s,:;\\/=]+)\s*[,:]\s*(\d{1,5})\s*$/.exec(event.target.value);
          if (match && Number(match[2]) >= 1 && Number(match[2]) <= 65535) setFields(previous => ({ ...previous, host: match[1], port: Number(match[2]) }));
        }} placeholder="sql.khachhang.vn" className={inputClass} required autoComplete="off" /></label>
      <label className="text-sm">Cổng<input type="number" min={1} max={65535} value={fields.port} onChange={event => change("port", Number(event.target.value))} className={inputClass} required /></label>
      <label className="text-sm">Tài khoản<input value={fields.userName} maxLength={128} onChange={event => change("userName", event.target.value)} className={inputClass} required={server === null} autoComplete="off" /></label>
      <label className="text-sm">Mật khẩu<input type="password" value={fields.password} maxLength={1024} onChange={event => change("password", event.target.value)}
        placeholder={server ? "Để trống để giữ mật khẩu cũ" : undefined} className={inputClass} required={server === null} autoComplete="new-password" /></label>
      <label className="text-sm">Database để kiểm tra (không bắt buộc)<input value={testDatabase} maxLength={128} onChange={event => { setTestDatabase(event.target.value); setCode(""); setFindings([]); }} className={inputClass} autoComplete="off" /></label>
      <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={fields.encrypt} onChange={event => change("encrypt", event.target.checked)} />Mã hoá đường truyền</label>
      <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={fields.trustServerCertificate} onChange={event => change("trustServerCertificate", event.target.checked)} />Tin cậy chứng chỉ máy chủ (chỉ mạng được quản lý)</label>
    </fieldset>
    {!fields.encrypt ? <p className="mt-3 text-sm text-amber-700">Đường truyền có thể không được mã hoá. Nên bật mã hoá và dùng chứng chỉ TLS hợp lệ.</p> : null}
    <Outcome code={code} details={details} findings={findings} source={source} />
    <div className="mt-4 flex flex-wrap gap-3">
      <button type="button" disabled={action !== null || !testable} onClick={() => void test()} className={`${outline} inline-flex items-center gap-2`}>
        {action === "test" ? <Loader2 className="size-4 animate-spin" aria-hidden="true" /> : null}Kiểm tra kết nối</button>
      <button type="submit" disabled={action !== null || !complete} className={primary}>
        {action === "save" ? <Loader2 className="size-4 animate-spin" aria-hidden="true" /> : null}{action === "save" ? (server?.sourceCount ? "Đang kiểm tra các database…" : "Đang lưu…") : operation ? "Thử lại lưu" : "Lưu máy chủ"}</button>
      <button type="button" disabled={action !== null} onClick={onCancel} className={outline}>Huỷ</button>
    </div>
  </form>;
}

/** Adds a database on a shared server (source null) or moves an existing source onto a server / to another database. */
export function ErpServerSourceForm({ servers, source, send, onSaved, onCancel }: {
  servers: ErpServer[]; source: CustomerErpSource | null; send: ErpSend; onSaved: () => void; onCancel: () => void;
}) {
  const [serverId, setServerId] = useState(source?.serverId ?? servers[0]?.id ?? "");
  const [name, setName] = useState("");
  const [database, setDatabase] = useState(source?.database ?? "");
  const [saving, setSaving] = useState(false);
  const [code, setCode] = useState(""), [findings, setFindings] = useState<ErpFinding[]>([]);
  const [operation, setOperation] = useState<string | null>(null);
  const reset = () => { setCode(""); setFindings([]); setOperation(null); };
  // Moving a source with a typed connection may leave the database blank to keep the one it uses now.
  const complete = serverId !== "" && (source ? true : name.trim().length > 0 && database.trim().length > 0);

  async function save(event: FormEvent) {
    event.preventDefault();
    if (saving || !complete) return;
    setSaving(true); setCode(""); setFindings([]);
    const operationId = operation ?? crypto.randomUUID();
    setOperation(operationId);
    try {
      const body = source
        ? { operationId, serverId, database: database.trim(), bindingVersion: source.bindingVersion }
        : { operationId, serverId, database: database.trim(), logicalName: name.trim() };
      const result = await send(source ? `${source.id}/server` : "customer-erp/from-server", source ? "PUT" : "POST", body);
      if (!result) return;
      if (result.status >= 200 && result.status < 300) { onSaved(); return; }
      setCode(resultCode(result.status, result.payload)); setFindings(parseErpFindings(result.payload.findings));
      if ([400, 409, 422, 429].includes(result.status)) setOperation(null);
    } finally { setSaving(false); }
  }

  const label = source ? source.serverId ? `Đổi database cho ${source.logicalName}` : `Chuyển ${source.logicalName} sang máy chủ dùng chung` : "Thêm database trên máy chủ dùng chung";
  return <form aria-label={label} className="mt-4 rounded-lg border p-4" onSubmit={event => void save(event)}>
    <p className="text-sm font-medium">{label}</p>
    <fieldset disabled={saving} className="mt-4 grid gap-4 sm:grid-cols-2">
      {source ? null : <label className="text-sm">Tên khách hàng<input value={name} maxLength={200} onChange={event => { setName(event.target.value); reset(); }} className={inputClass} required /></label>}
      <label className="text-sm">Máy chủ dùng chung<select value={serverId} onChange={event => { setServerId(event.target.value); reset(); }} className={inputClass} required>
        {servers.map(server => <option key={server.id} value={server.id}>{server.name} · {server.host}:{server.port}</option>)}</select></label>
      <label className="text-sm">Tên database<input value={database} maxLength={128} onChange={event => { setDatabase(event.target.value); reset(); }}
        placeholder={source && !source.serverId ? "Để trống để giữ database hiện tại" : undefined} className={inputClass} required={!source} autoComplete="off" /></label>
    </fieldset>
    <Outcome code={code} findings={findings} />
    <div className="mt-4 flex flex-wrap gap-3">
      <button type="submit" disabled={saving || !complete} className={primary}>
        {saving ? <Loader2 className="size-4 animate-spin" aria-hidden="true" /> : null}{saving ? "Đang kiểm tra và lưu…" : operation ? "Thử lại lưu" : "Kiểm tra và lưu"}</button>
      <button type="button" disabled={saving} onClick={onCancel} className={outline}>Huỷ</button>
    </div>
  </form>;
}
