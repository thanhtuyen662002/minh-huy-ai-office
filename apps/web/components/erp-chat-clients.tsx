"use client";

import { FormEvent, useEffect, useRef, useState } from "react";
import { Loader2 } from "lucide-react";
import { AiAccount } from "../lib/ai-usage";
import { ErpChatClient, ErpChatClientSecret, isErpChatClientCreate, parseErpChatClients, parseErpChatSecret } from "../lib/erp-chat";

type Props = {
  companyId: string; generation: number; isCurrent: (generation: number) => boolean;
  validate: (generation: number) => Promise<boolean>;
  request: (generation: number, url: string, init?: RequestInit) => Promise<Response>;
  onUnauthorized: () => void; accounts: AiAccount[];
};

const outline = "rounded-lg border bg-card px-3 py-2 text-sm hover:bg-muted disabled:opacity-50";
const input = "w-full rounded-lg border bg-background px-3 py-2 text-sm";

/** Web ERPs allowed to call the chat API: create one (its key is shown once), replace its key, or turn it off. */
export function ErpChatClients({ companyId, generation, isCurrent, validate, request, onUnauthorized, accounts }: Props) {
  const [clients, setClients] = useState<ErpChatClient[] | null>(null);
  const [form, setForm] = useState<{ name: string; accountId: string; requireScopes: boolean } | null>(null);
  const [secret, setSecret] = useState<ErpChatClientSecret | null>(null);
  const [error, setError] = useState(""), [busy, setBusy] = useState(false), [copied, setCopied] = useState(false);
  const mounted = useRef(false), lock = useRef(false);
  const current = () => mounted.current && isCurrent(generation);
  const selector = `companyId=${encodeURIComponent(companyId)}`;

  async function load() {
    try {
      if (!await validate(generation) || !current()) return;
      const response = await request(generation, `/api/local/erp-chat/clients?${selector}`, { cache: "no-store" });
      if (!current()) return;
      if (response.status === 401) { onUnauthorized(); return; }
      const parsed = response.ok ? parseErpChatClients(await response.json()) : null;
      if (!current()) return;
      setClients(parsed ?? []); if (!parsed) setError("Không tải được danh sách web ERP.");
    } catch { if (current()) setError("Không tải được danh sách web ERP."); }
  }

  useEffect(() => {
    mounted.current = true;
    void load();
    return () => { mounted.current = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function send(path: string, method: "POST" | "PUT", body: unknown): Promise<unknown | null> {
    if (lock.current) return null;
    lock.current = true; setBusy(true); setError("");
    try {
      if (!await validate(generation) || !current()) return null;
      const response = await request(generation, `/api/local/erp-chat/clients${path}?${selector}`,
        { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
      if (!current()) return null;
      if (response.status === 401) { onUnauthorized(); return null; }
      if (!response.ok) {
        setError(response.status === 409 ? "Tên đã có hoặc dữ liệu vừa thay đổi. Tải lại rồi thử lại." : "Chưa lưu được. Kiểm tra lại rồi thử lần nữa.");
        return null;
      }
      const value = await response.json().catch(() => ({}));
      await load();
      return value;
    } catch { if (current()) setError("Chưa lưu được. Thử lại sau ít phút."); return null; }
    finally { lock.current = false; if (mounted.current) setBusy(false); }
  }

  async function create(event: FormEvent) {
    event.preventDefault();
    if (!form) return;
    const body = { name: form.name.trim(), billingAccountId: form.accountId, requireScopes: form.requireScopes };
    if (!isErpChatClientCreate(body)) { setError("Nhập tên và chọn khách hàng tính tiền."); return; }
    const created = parseErpChatSecret(await send("", "POST", body));
    if (created) { setSecret(created); setForm(null); setCopied(false); }
  }

  async function update(client: ErpChatClient, isEnabled: boolean, rotateKey: boolean, requireScopes = client.requireScopes) {
    if (rotateKey && !window.confirm(`Tạo khoá mới cho ${client.name}? Khoá cũ ngừng hoạt động ngay, web ERP phải cập nhật khoá mới.`)) return;
    const result = await send(`/${client.id}`, "PUT", { isEnabled, rotateKey, version: client.version, requireScopes });
    const rotated = parseErpChatSecret(result);
    if (rotated) { setSecret(rotated); setCopied(false); }
  }

  const accountName = (id: string) => accounts.find(account => account.id === id)?.name ?? "Khách hàng đã xoá";

  return <section className="rounded-lg border bg-card p-4">
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div>
        <h3 className="font-semibold">Web ERP kết nối</h3>
        <p className="mt-1 text-sm text-muted-foreground">Web ERP gọi API chat của AI Office từ máy chủ, ký mỗi yêu cầu bằng khoá riêng.
          Mỗi web ERP chỉ hỏi được các nguồn ERP của khách hàng được gắn, và chi phí tính cho khách hàng đó.</p>
      </div>
      {!form ? <button type="button" disabled={!accounts.length || busy} onClick={() => setForm({ name: "", accountId: accounts[0]?.id ?? "", requireScopes: false })}
        className={outline}>Thêm web ERP</button> : null}
    </div>
    {!accounts.length ? <p className="mt-3 text-sm text-muted-foreground">Tạo khách hàng tính tiền trước, rồi thêm web ERP cho khách hàng đó.</p> : null}
    {error ? <p role="alert" className="mt-3 rounded-lg bg-destructive/10 p-3 text-sm text-destructive">{error}</p> : null}
    {secret ? <div role="status" className="mt-4 space-y-2 rounded-lg border border-amber-300 bg-amber-50 p-4 text-sm text-amber-950 dark:bg-amber-950 dark:text-amber-50">
      <p className="font-semibold">Khoá của {secret.client.name}: chỉ hiện một lần</p>
      <p>Gửi hai giá trị này cho đội web ERP qua kênh an toàn. Đóng thông báo này là không xem lại được khoá.</p>
      <p>Client ID: <code className="break-all">{secret.client.id}</code></p>
      <p>Khoá ký: <code className="break-all">{secret.signingKey}</code></p>
      <div className="flex gap-2">
        <button type="button" className={outline} onClick={() => { void navigator.clipboard?.writeText(`Client ID: ${secret.client.id}\nSigning key: ${secret.signingKey}`).then(() => setCopied(true)); }}>
          {copied ? "Đã chép" : "Chép cả hai"}</button>
        <button type="button" className={outline} onClick={() => setSecret(null)}>Đã lưu, đóng</button>
      </div>
    </div> : null}
    {form ? <form onSubmit={event => void create(event)} className="mt-4 space-y-3 rounded-lg bg-muted p-4">
      <div className="grid gap-3 sm:grid-cols-2">
        <label className="text-sm">Tên web ERP<input required maxLength={200} value={form.name} placeholder="Web ERP Mak"
          onChange={event => setForm({ ...form, name: event.target.value })} className={`${input} mt-1`} /></label>
        <label className="text-sm">Khách hàng tính tiền<select value={form.accountId} onChange={event => setForm({ ...form, accountId: event.target.value })}
          className={`${input} mt-1`}>{accounts.map(account => <option key={account.id} value={account.id}>{account.name}</option>)}</select></label>
      </div>
      <label className="flex items-start gap-2 text-sm"><input type="checkbox" checked={form.requireScopes} className="mt-1"
        onChange={event => setForm({ ...form, requireScopes: event.target.checked })} />
        <span>Bắt buộc gửi phạm vi dữ liệu<span className="block text-xs text-muted-foreground">Câu hỏi không kèm phạm vi của người dùng ERP
          (ví dụ chi nhánh) sẽ bị từ chối. Database ERP cần bật Row-Level Security.</span></span></label>
      <div className="flex gap-2">
        <button type="submit" disabled={busy} className="inline-flex items-center gap-2 rounded-lg bg-primary px-4 py-2 text-sm font-medium text-primary-foreground disabled:opacity-50">
          {busy ? <Loader2 className="size-4 animate-spin" aria-hidden="true" /> : null}Tạo và lấy khoá</button>
        <button type="button" disabled={busy} onClick={() => setForm(null)} className={outline}>Huỷ</button>
      </div>
    </form> : null}
    {clients?.length ? <ul className="mt-4 space-y-2">{clients.map(client => <li key={client.id}
      className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-muted p-3 text-sm">
      <span><strong>{client.name}</strong> · {accountName(client.billingAccountId)} · {client.isEnabled ? "đang bật" : "đã tắt"}
        {client.requireScopes ? " · bắt buộc phạm vi" : ""}
        <span className="block break-all text-xs text-muted-foreground">Client ID: {client.id}</span></span>
      <span className="flex flex-wrap gap-2">
        <button type="button" disabled={busy} className={outline} aria-pressed={client.requireScopes}
          onClick={() => void update(client, client.isEnabled, false, !client.requireScopes)}>
          {client.requireScopes ? "Bỏ bắt buộc phạm vi" : "Bắt buộc phạm vi"}<span className="sr-only"> {client.name}</span></button>
        <button type="button" disabled={busy} className={outline} onClick={() => void update(client, client.isEnabled, true)}>
          Tạo khoá mới<span className="sr-only"> {client.name}</span></button>
        <button type="button" disabled={busy} className={outline} onClick={() => void update(client, !client.isEnabled, false)}>
          {client.isEnabled ? "Tắt" : "Bật"}<span className="sr-only"> {client.name}</span></button>
      </span>
    </li>)}</ul> : clients && accounts.length ? <p className="mt-4 text-sm text-muted-foreground">Chưa có web ERP nào.</p> : null}
  </section>;
}
