"use client";

import { FormEvent, useEffect, useRef, useState } from "react";
import { Loader2 } from "lucide-react";
import { ErpChatClients } from "./erp-chat-clients";
import { AiAccount, AiUsageReport, AiUsageSettings, AiUsageTotals, currentMonth, formatMoney, formatTokens,
  isAiAccountSave, isAiPriceSave, parseAiUsageReport, parseAiUsageSettings } from "../lib/ai-usage";

type Props = {
  companyId: string; generation: number; isCurrent: (generation: number) => boolean;
  validate: (generation: number) => Promise<boolean>;
  /** Session check for reads; may reuse a recent confirmation. Saves always use validate. */
  validateRead?: (generation: number) => Promise<boolean>;
  request: (generation: number, url: string, init?: RequestInit) => Promise<Response>;
  onUnauthorized: () => void;
};
type AccountForm = { id: string | null; version: string | null; name: string; budget: string; perMinute: string; sourceIds: string[] };
type PriceForm = { model: string; input: string; cached: string; output: string };

const box = "rounded-lg border bg-card p-4";
const outline = "rounded-lg border bg-card px-3 py-2 text-sm hover:bg-muted disabled:opacity-50";
const primary = "inline-flex items-center gap-2 rounded-lg bg-primary px-4 py-2 text-sm font-medium text-primary-foreground hover:opacity-90 disabled:opacity-50";
const input = "w-full rounded-lg border bg-background px-3 py-2 text-sm";
const messages: Record<string, string> = {
  invalid_fields: "Thông tin chưa hợp lệ. Kiểm tra lại các ô.",
  conflict: "Dữ liệu vừa được người khác thay đổi. Tải lại rồi sửa lại.",
  not_found: "Khách hàng này không còn. Tải lại danh sách.",
  unavailable: "Chưa nhận được kết quả từ máy chủ. Thử lại sau ít phút.",
  load_failed: "Không tải được số liệu chi phí AI. Bấm “Tải lại”.",
};
const number = (value: string) => value.trim() === "" ? null : Number(value.replace(/[\s.,](?=\d{3}(\D|$))/g, "").replace(",", "."));

/** Kết nối → Chi phí AI: what each customer and person used this month, the selling prices and each customer's limits. */
export function AiUsagePanel({ companyId, generation, isCurrent, validate, validateRead = validate, request, onUnauthorized }: Props) {
  const [month, setMonth] = useState(currentMonth());
  const [report, setReport] = useState<AiUsageReport | null>(null);
  const [settings, setSettings] = useState<AiUsageSettings | null>(null);
  const [code, setCode] = useState(""), [notice, setNotice] = useState("");
  const [loading, setLoading] = useState(false), [busy, setBusy] = useState(false);
  const [accountForm, setAccountForm] = useState<AccountForm | null>(null);
  const [priceForm, setPriceForm] = useState<PriceForm | null>(null);
  const mounted = useRef(false), lock = useRef(false);
  const current = () => mounted.current && isCurrent(generation);
  const selector = `companyId=${encodeURIComponent(companyId)}`;

  async function read(url: string): Promise<unknown | null | "unauthorized"> {
    const response = await request(generation, url, { cache: "no-store" });
    if (response.status === 401) return "unauthorized";
    if (!response.ok) return null;
    try { return await response.json(); } catch { return null; }
  }

  async function load(selected = month) {
    setLoading(true);
    try {
      if (!await validateRead(generation) || !current()) return;
      const [usage, config] = await Promise.all([read(`/api/local/ai-usage?${selector}&month=${selected}`), read(`/api/local/ai-usage/settings?${selector}`)]);
      if (!current()) return;
      if (usage === "unauthorized" || config === "unauthorized") { onUnauthorized(); return; }
      const parsedReport = parseAiUsageReport(usage), parsedSettings = parseAiUsageSettings(config);
      if (!parsedReport || !parsedSettings) { setCode("load_failed"); return; }
      setReport(parsedReport); setSettings(parsedSettings); setCode("");
    } catch { if (current()) setCode("load_failed"); }
    finally { if (mounted.current) setLoading(false); }
  }

  useEffect(() => {
    mounted.current = true;
    void load();
    return () => { mounted.current = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function send(path: string, method: "POST" | "PUT", body: unknown, done: string) {
    if (lock.current) return false;
    lock.current = true; setBusy(true); setCode(""); setNotice("");
    try {
      if (!await validate(generation) || !current()) return false;
      const response = await request(generation, `/api/local/ai-usage/${path}?${selector}`,
        { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
      if (!current()) return false;
      if (response.status === 401) { onUnauthorized(); return false; }
      if (!response.ok) {
        const payload = await response.json().catch(() => null) as { code?: unknown } | null;
        setCode(typeof payload?.code === "string" && messages[payload.code] ? payload.code
          : response.status === 409 ? "conflict" : response.status === 400 ? "invalid_fields" : "unavailable");
        return false;
      }
      setNotice(done);
      await load();
      return true;
    } catch { if (current()) setCode("unavailable"); return false; }
    finally { lock.current = false; if (mounted.current) setBusy(false); }
  }

  async function saveAccount(event: FormEvent) {
    event.preventDefault();
    if (!accountForm) return;
    const body = { name: accountForm.name.trim(), monthlyBudget: number(accountForm.budget), questionsPerMinute: number(accountForm.perMinute),
      sourceIds: accountForm.sourceIds, version: accountForm.version };
    if (!isAiAccountSave(body)) { setCode("invalid_fields"); return; }
    if (await send(accountForm.id ? `accounts/${accountForm.id}` : "accounts", accountForm.id ? "PUT" : "POST", body, "Đã lưu khách hàng."))
      setAccountForm(null);
  }

  async function savePrice(event: FormEvent) {
    event.preventDefault();
    if (!priceForm) return;
    const body = { model: priceForm.model.trim(), inputPerMillion: number(priceForm.input) ?? NaN,
      cachedInputPerMillion: number(priceForm.cached || priceForm.input) ?? NaN, outputPerMillion: number(priceForm.output) ?? NaN };
    if (!isAiPriceSave(body)) { setCode("invalid_fields"); return; }
    if (await send("prices", "POST", body, "Đã lưu giá. Giá mới áp dụng cho các lượt gọi AI từ bây giờ.")) setPriceForm(null);
  }

  const sourceName = (id: string) => settings?.sources.find(source => source.id === id)?.logicalName ?? "Nguồn đã xoá";
  const editAccount = (account: AiAccount | null) => setAccountForm(account
    ? { id: account.id, version: account.version, name: account.name, budget: account.monthlyBudget?.toString() ?? "",
      perMinute: account.questionsPerMinute?.toString() ?? "", sourceIds: account.sourceIds }
    : { id: null, version: null, name: "", budget: "", perMinute: "", sourceIds: [] });
  const owner = (sourceId: string) => settings?.accounts.find(account => account.sourceIds.includes(sourceId) && account.id !== accountForm?.id);

  return <div className="space-y-6">
    <div className="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h2 className="text-lg font-semibold">Chi phí AI</h2>
        <p className="mt-1 text-sm text-muted-foreground">Số câu hỏi, token và thành tiền theo khách hàng, người hỏi và model. Tháng tính theo giờ Việt Nam.</p>
      </div>
      <div className="flex items-end gap-2">
        <label className="text-sm">Tháng
          <input type="month" value={month} max={currentMonth()} onChange={event => { if (event.target.value) { setMonth(event.target.value); void load(event.target.value); } }}
            className={`${input} mt-1`} />
        </label>
        <button type="button" onClick={() => void load()} disabled={loading} className={outline}>
          {loading ? <Loader2 className="inline size-4 animate-spin" aria-hidden="true" /> : null} Tải lại
        </button>
      </div>
    </div>
    {code ? <p role="alert" className="rounded-lg bg-destructive/10 p-3 text-sm text-destructive">{messages[code] ?? messages.unavailable}</p> : null}
    {notice ? <p role="status" className="rounded-lg bg-muted p-3 text-sm">{notice}</p> : null}

    {report ? <>
      <div className="grid gap-3 sm:grid-cols-4">
        <Tile label="Câu hỏi" value={formatTokens(report.totals.questions)} />
        <Tile label="Token vào" value={formatTokens(report.totals.inputTokens)}
          hint={report.totals.cachedInputTokens ? `${formatTokens(report.totals.cachedInputTokens)} từ bộ nhớ đệm` : undefined} />
        <Tile label="Token ra" value={formatTokens(report.totals.outputTokens)} />
        <Tile label="Thành tiền" value={formatMoney(report.totals.cost)} />
      </div>
      {report.totals.unpricedCalls ? <p className="rounded-lg bg-amber-50 p-3 text-sm text-amber-900 dark:bg-amber-950 dark:text-amber-100">
        {formatTokens(report.totals.unpricedCalls)} lượt gọi AI chưa có giá nên chưa tính tiền. Thêm giá cho model ở “Bảng giá” bên dưới; giá chỉ áp dụng từ lúc lưu.
      </p> : null}
      <UsageTable title="Theo khách hàng" first="Khách hàng" rows={report.accounts.map(row => ({ key: row.accountId ?? "none", name: row.name,
        totals: row.totals, budget: row.monthlyBudget }))} />
      <UsageTable title="Theo người hỏi" first="Người hỏi" rows={report.users.map(row => ({ key: row.userId ?? "system", name: row.name, totals: row.totals }))} />
      <UsageTable title="Theo model" first="Model" calls rows={report.models.map(row => ({ key: `${row.providerId}:${row.model}`, name: row.model,
        detail: row.providerId, totals: row.totals }))} />
      <UsageTable title="Theo ngày" first="Ngày" rows={[...report.days].reverse().map(row => ({ key: row.date, name: row.date.split("-").reverse().join("/"), totals: row.totals }))} />
    </> : loading ? <p className="text-sm text-muted-foreground">Đang tải…</p> : null}

    {settings ? <section className={box}>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h3 className="font-semibold">Khách hàng tính tiền</h3>
          <p className="mt-1 text-sm text-muted-foreground">Gắn nguồn ERP với khách hàng để tính chi phí, đặt hạn mức tháng và số câu hỏi mỗi phút.
            Mỗi người hỏi tối đa {settings.userQuestionsPerMinute} câu mỗi phút.</p>
        </div>
        {!accountForm ? <button type="button" onClick={() => editAccount(null)} className={outline}>Thêm khách hàng</button> : null}
      </div>
      {accountForm ? <form onSubmit={event => void saveAccount(event)} className="mt-4 space-y-3 rounded-lg bg-muted p-4">
        <div className="grid gap-3 sm:grid-cols-3">
          <label className="text-sm">Tên khách hàng<input required maxLength={200} value={accountForm.name}
            onChange={event => setAccountForm({ ...accountForm, name: event.target.value })} className={`${input} mt-1`} /></label>
          <label className="text-sm">Hạn mức tháng (đ)<input inputMode="numeric" placeholder="Không giới hạn" value={accountForm.budget}
            onChange={event => setAccountForm({ ...accountForm, budget: event.target.value })} className={`${input} mt-1`} /></label>
          <label className="text-sm">Câu hỏi mỗi phút<input inputMode="numeric" placeholder="Không giới hạn" value={accountForm.perMinute}
            onChange={event => setAccountForm({ ...accountForm, perMinute: event.target.value })} className={`${input} mt-1`} /></label>
        </div>
        <fieldset className="text-sm">
          <legend className="mb-1">Nguồn ERP tính cho khách hàng này</legend>
          <div className="grid gap-1 sm:grid-cols-2">
            {settings.sources.map(source => <label key={source.id} className="flex items-center gap-2">
              <input type="checkbox" checked={accountForm.sourceIds.includes(source.id)} onChange={event => setAccountForm({ ...accountForm,
                sourceIds: event.target.checked ? [...accountForm.sourceIds, source.id] : accountForm.sourceIds.filter(id => id !== source.id) })} />
              {source.logicalName}{owner(source.id) ? <span className="text-muted-foreground"> (đang thuộc {owner(source.id)!.name})</span> : null}
            </label>)}
          </div>
        </fieldset>
        <div className="flex gap-2">
          <button type="submit" disabled={busy} className={primary}>{busy ? <Loader2 className="size-4 animate-spin" aria-hidden="true" /> : null}Lưu khách hàng</button>
          <button type="button" onClick={() => setAccountForm(null)} disabled={busy} className={outline}>Huỷ</button>
        </div>
      </form> : null}
      {settings.accounts.length ? <ul className="mt-4 space-y-2">{settings.accounts.map(account => <li key={account.id}
        className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-muted p-3 text-sm">
        <span><strong>{account.name}</strong>
          {" · "}{account.monthlyBudget === null ? "không giới hạn tiền" : `hạn mức ${formatMoney(account.monthlyBudget)}/tháng`}
          {" · "}{account.questionsPerMinute === null ? "không giới hạn câu hỏi" : `${account.questionsPerMinute} câu/phút`}
          <span className="block text-muted-foreground">{account.sourceIds.length ? account.sourceIds.map(sourceName).join(", ") : "Chưa gắn nguồn nào"}</span>
        </span>
        <button type="button" disabled={!!accountForm || busy} onClick={() => editAccount(account)} className={outline}>Sửa<span className="sr-only"> {account.name}</span></button>
      </li>)}</ul> : !accountForm ? <p className="mt-4 text-sm text-muted-foreground">Chưa có khách hàng nào. Câu hỏi vẫn được ghi, nằm ở mục “Chưa gán khách hàng”.</p> : null}
    </section> : null}

    {settings ? <section className={box}>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h3 className="font-semibold">Bảng giá</h3>
          <p className="mt-1 text-sm text-muted-foreground">Giá bán cho 1 triệu token (đ). Đổi giá chỉ áp dụng cho lượt gọi từ lúc lưu; số đã ghi giữ giá cũ.
            Tên model khớp theo phần đầu, ví dụ “claude-sonnet-5-5” áp cho cả “claude-sonnet-5-5-20261001”.</p>
        </div>
        {!priceForm ? <button type="button" onClick={() => setPriceForm({ model: report?.models.find(row => row.totals.unpricedCalls)?.model ?? "", input: "", cached: "", output: "" })}
          className={outline}>Thêm giá</button> : null}
      </div>
      {priceForm ? <form onSubmit={event => void savePrice(event)} className="mt-4 space-y-3 rounded-lg bg-muted p-4">
        <div className="grid gap-3 sm:grid-cols-4">
          <label className="text-sm">Model<input required maxLength={200} value={priceForm.model}
            onChange={event => setPriceForm({ ...priceForm, model: event.target.value })} className={`${input} mt-1`} /></label>
          <label className="text-sm">Token vào<input required inputMode="decimal" value={priceForm.input}
            onChange={event => setPriceForm({ ...priceForm, input: event.target.value })} className={`${input} mt-1`} /></label>
          <label className="text-sm">Token vào từ bộ nhớ đệm<input inputMode="decimal" placeholder="Như token vào" value={priceForm.cached}
            onChange={event => setPriceForm({ ...priceForm, cached: event.target.value })} className={`${input} mt-1`} /></label>
          <label className="text-sm">Token ra<input required inputMode="decimal" value={priceForm.output}
            onChange={event => setPriceForm({ ...priceForm, output: event.target.value })} className={`${input} mt-1`} /></label>
        </div>
        <div className="flex gap-2">
          <button type="submit" disabled={busy} className={primary}>{busy ? <Loader2 className="size-4 animate-spin" aria-hidden="true" /> : null}Lưu giá</button>
          <button type="button" onClick={() => setPriceForm(null)} disabled={busy} className={outline}>Huỷ</button>
        </div>
      </form> : null}
      {settings.prices.length ? <table className="mt-4 w-full text-sm">
        <thead className="text-left text-muted-foreground"><tr><th className="py-1">Model</th><th>Token vào</th><th>Bộ nhớ đệm</th><th>Token ra</th><th /></tr></thead>
        <tbody>{settings.prices.map(price => <tr key={price.model} className="border-t">
          <td className="py-2 font-medium">{price.model}</td><td>{formatMoney(price.inputPerMillion)}</td>
          <td>{formatMoney(price.cachedInputPerMillion)}</td><td>{formatMoney(price.outputPerMillion)}</td>
          <td className="text-right"><button type="button" disabled={!!priceForm || busy} className={outline} onClick={() => setPriceForm({ model: price.model,
            input: String(price.inputPerMillion), cached: String(price.cachedInputPerMillion), output: String(price.outputPerMillion) })}>
            Đổi giá<span className="sr-only"> {price.model}</span></button></td>
        </tr>)}</tbody>
      </table> : !priceForm ? <p className="mt-4 text-sm text-muted-foreground">Chưa có giá nào: token vẫn được ghi nhưng chưa quy ra tiền.</p> : null}
    </section> : null}
    {settings ? <ErpChatClients companyId={companyId} generation={generation} isCurrent={isCurrent} validate={validate} validateRead={validateRead} request={request}
      onUnauthorized={onUnauthorized} accounts={settings.accounts} /> : null}
  </div>;
}

function Tile({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return <div className={box}>
    <p className="text-sm text-muted-foreground">{label}</p>
    <p className="mt-1 text-xl font-semibold tabular-nums">{value}</p>
    {hint ? <p className="mt-1 text-xs text-muted-foreground">{hint}</p> : null}
  </div>;
}

type Row = { key: string; name: string; detail?: string; totals: AiUsageTotals; budget?: number | null };
function UsageTable({ title, first, rows, calls }: { title: string; first: string; rows: Row[]; calls?: boolean }) {
  if (!rows.length) return null;
  return <section className={box}>
    <h3 className="font-semibold">{title}</h3>
    <div className="mt-2 overflow-x-auto">
      <table className="w-full min-w-[560px] text-sm tabular-nums">
        <thead className="text-left text-muted-foreground"><tr>
          <th className="py-1">{first}</th><th>{calls ? "Lượt gọi" : "Câu hỏi"}</th><th>Token vào</th><th>Token ra</th><th className="text-right">Thành tiền</th>
        </tr></thead>
        <tbody>{rows.map(row => <tr key={row.key} className="border-t">
          <td className="py-2">{row.name}{row.detail ? <span className="block text-xs text-muted-foreground">{row.detail}</span> : null}</td>
          <td>{formatTokens(calls ? row.totals.calls : row.totals.questions)}</td>
          <td>{formatTokens(row.totals.inputTokens)}</td>
          <td>{formatTokens(row.totals.outputTokens)}</td>
          <td className="text-right">{formatMoney(row.totals.cost)}
            {row.budget ? <span className={`block text-xs ${row.totals.cost >= row.budget ? "text-destructive" : "text-muted-foreground"}`}>
              {Math.min(999, Math.round(row.totals.cost / row.budget * 100))}% hạn mức {formatMoney(row.budget)}</span> : null}
            {row.totals.unpricedCalls ? <span className="block text-xs text-amber-700 dark:text-amber-300">{formatTokens(row.totals.unpricedCalls)} lượt chưa có giá</span> : null}
          </td>
        </tr>)}</tbody>
      </table>
    </div>
  </section>;
}
