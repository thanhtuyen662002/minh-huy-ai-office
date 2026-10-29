"use client";
import { FormEvent, useEffect, useMemo, useRef, useState } from "react";
import { Building2, FileText, Lightbulb, Plus, Search } from "lucide-react";
import { parseMemberPage } from "../lib/company-members";
import { KnowledgePage, KnowledgeSummary, knowledgeBlocks, knowledgeLimits, knowledgeTicketCodes, parseKnowledgeList, parseKnowledgePage,
  toggleKnowledgeTodo } from "../lib/knowledge-pages";

type Props = { companyId: string; userId: string; generation: number;
  isCurrent: (generation: number) => boolean; validate: (generation: number) => Promise<boolean>;
  request: (generation: number, url: string, init?: RequestInit) => Promise<Response>; onUnauthorized: () => void;
  onOpenTickets?: (customer: string) => void };
type Draft = { title: string; customerName: string; body: string };
const button = "inline-flex h-8 items-center justify-center gap-1.5 rounded-lg border bg-card px-3 text-sm font-medium hover:bg-muted disabled:pointer-events-none disabled:opacity-50";
const primary = "inline-flex h-8 items-center justify-center gap-1.5 rounded-lg bg-primary px-3 text-sm font-medium text-primary-foreground hover:opacity-90 disabled:pointer-events-none disabled:opacity-50";
const field = "w-full rounded-lg border bg-background px-3 py-2 text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring";
const time = (value: string) => new Date(value).toLocaleString("vi-VN", { dateStyle: "short", timeStyle: "short" });
const messages: Record<number, string> = {
  403: "Bạn không có quyền xem hoặc sửa trang kiến thức của công ty này.",
  404: "Trang này không còn. Danh sách đã được tải lại.",
  409: "Trang vừa được người khác sửa. Hãy tải lại trang rồi sửa tiếp.",
  400: "Nội dung chưa hợp lệ. Kiểm tra tiêu đề và độ dài nội dung.",
};

// Kiến thức: a page tree on the left (general pages, then pages per customer) and the open page on the right.
export function KnowledgePanel({ companyId, userId, generation, isCurrent, validate, request, onUnauthorized, onOpenTickets }: Props) {
  const [pages, setPages] = useState<KnowledgeSummary[] | null>(null);
  const [page, setPage] = useState<KnowledgePage | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [names, setNames] = useState<Record<string, string>>({});
  const [draft, setDraft] = useState<Draft | null>(null);
  const [editingExisting, setEditingExisting] = useState(false);
  const [filter, setFilter] = useState("");
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const mounted = useRef(false), serial = useRef(0), lock = useRef(false);
  const selector = `companyId=${encodeURIComponent(companyId)}`;
  const live = () => mounted.current && isCurrent(generation);

  // Every call is checked against the session before and after it, like the other company panels.
  async function call(path: string, init: RequestInit = {}): Promise<Response | null> {
    if (!live() || !await validate(generation) || !live()) return null;
    const response = await request(generation, path, { cache: "no-store", ...init });
    if (!live()) return null;
    if (response.status === 401) { onUnauthorized(); return null; }
    // A refused company keeps nothing on screen from before.
    if (response.status === 403) { setPages([]); setPage(null); setSelected(null); setDraft(null); }
    return await validate(generation) && live() ? response : null;
  }

  async function loadList(open?: string | null) {
    const value = ++serial.current;
    try {
      const response = await call(`/api/local/knowledge-pages?${selector}`);
      if (!response || serial.current !== value) return;
      const list = response.ok ? parseKnowledgeList(await response.json()) : null;
      if (!live() || serial.current !== value) return;
      if (!list) { setPages([]); setError(messages[response.status] ?? "Không tải được trang kiến thức. Hãy thử lại sau."); return; }
      setPages(list.items);
      const next = open !== undefined ? open : selected;
      const target = next && list.items.some(item => item.id === next) ? next : list.items[0]?.id ?? null;
      if (target) await openPage(target); else { setSelected(null); setPage(null); }
    } catch { if (live()) { setPages(old => old ?? []); setError("Không tải được trang kiến thức. Hãy thử lại sau."); } }
  }

  async function openPage(id: string) {
    const value = ++serial.current;
    setSelected(id); setDraft(null); setConfirmDelete(false);
    try {
      const response = await call(`/api/local/knowledge-pages/${id}?${selector}`);
      if (!response || serial.current !== value) return;
      const view = response.ok ? parseKnowledgePage(await response.json(), id) : null;
      if (!live() || serial.current !== value) return;
      if (!view) { setPage(null); setError(messages[response.status] ?? "Không mở được trang này."); return; }
      setPage(view); setError("");
    } catch { if (live()) setError("Không mở được trang này."); }
  }

  useEffect(() => {
    mounted.current = true;
    void loadList(null);
    // Member names only label "Sửa bởi"; the page works without them.
    void (async () => {
      try {
        const response = await request(generation, `/api/local/company/members?${new URLSearchParams({ companyId, offset: "0", limit: "100" })}`, { cache: "no-store" });
        const members = response.ok ? parseMemberPage(await response.json()) : null;
        if (members && live()) setNames(Object.fromEntries(members.items.map(member => [member.userId.toLowerCase(), member.displayName])));
      } catch { /* Names stay unknown. */ }
    })();
    return () => { mounted.current = false; serial.current++; };
    // Parent remounts on every company/session authority generation.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function save(body: Draft, version?: string, id?: string) {
    if (lock.current || !live()) return;
    lock.current = true; setBusy(true); setError("");
    try {
      const payload = { title: body.title.trim(), body: body.body, customerName: body.customerName.trim() || null, ...(version ? { rowVersion: version } : {}) };
      const response = await call(`/api/local/knowledge-pages${id ? `/${id}` : ""}?${selector}`, {
        method: id ? "PUT" : "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
      if (!response) return;
      const view = response.ok ? parseKnowledgePage(await response.json(), id) : null;
      if (!live()) return;
      if (!view) { setError(messages[response.status] ?? "Chưa lưu được trang. Hãy thử lại."); if (response.status === 404) void loadList(null); return; }
      setDraft(null); setPage(view); setSelected(view.id);
      setPages(old => [...(old ?? []).filter(item => item.id !== view.id),
        { id: view.id, customerName: view.customerName, title: view.title, updatedBy: view.updatedBy, updatedAt: view.updatedAt }]);
    } catch { if (live()) setError("Chưa lưu được trang. Hãy thử lại."); }
    finally { lock.current = false; if (mounted.current) setBusy(false); }
  }

  async function remove() {
    if (!page || lock.current || !live()) return;
    lock.current = true; setBusy(true); setError("");
    try {
      const response = await call(`/api/local/knowledge-pages/${page.id}?${selector}&rowVersion=${encodeURIComponent(page.rowVersion)}`, { method: "DELETE" });
      if (!response) return;
      if (response.status !== 204) { setError(messages[response.status] ?? "Chưa xoá được trang. Hãy thử lại."); return; }
      setPage(null); setConfirmDelete(false);
      await loadList(null);
    } catch { if (live()) setError("Chưa xoá được trang. Hãy thử lại."); }
    finally { lock.current = false; if (mounted.current) setBusy(false); }
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    if (draft) void save(draft, editingExisting ? page?.rowVersion : undefined, editingExisting ? page?.id : undefined);
  }

  const shown = useMemo(() => {
    const query = filter.trim().toLowerCase();
    return (pages ?? []).filter(item => !query || item.title.toLowerCase().includes(query) || item.customerName?.toLowerCase().includes(query));
  }, [pages, filter]);
  const general = shown.filter(item => item.customerName === null).sort((a, b) => a.title.localeCompare(b.title, "vi"));
  const customers = [...new Set(shown.flatMap(item => item.customerName === null ? [] : [item.customerName]))].sort((a, b) => a.localeCompare(b, "vi"));
  const knownCustomers = [...new Set((pages ?? []).flatMap(item => item.customerName === null ? [] : [item.customerName]))];
  const pageButton = (item: KnowledgeSummary) => <li key={item.id}>
    <button type="button" onClick={() => void openPage(item.id)} aria-current={selected === item.id ? "page" : undefined}
      className={`flex w-full items-center gap-2 rounded-lg px-2.5 py-1.5 text-left text-sm ${selected === item.id ? "bg-muted font-medium text-foreground" : "text-muted-foreground hover:bg-muted/60 hover:text-foreground"}`}>
      <FileText aria-hidden className="size-4 shrink-0" /><span className="truncate">{item.title}</span>
    </button>
  </li>;
  const editor = (id?: string) => (base: Draft) => { setEditingExisting(!!id); setDraft(base); setConfirmDelete(false); setError(""); };

  return <div className="grid overflow-hidden rounded-xl border bg-card shadow-sm lg:min-h-[560px] lg:grid-cols-[264px_minmax(0,1fr)]">
    <aside aria-label="Danh sách trang" className="border-b bg-muted/30 p-3 lg:border-b-0 lg:border-r">
      <div className="relative">
        <Search aria-hidden className="pointer-events-none absolute left-2.5 top-2.5 size-4 text-muted-foreground" />
        <input type="search" value={filter} onChange={event => setFilter(event.target.value)} aria-label="Tìm trang" placeholder="Tìm trang…"
          className="h-9 w-full rounded-lg border bg-background pl-8 pr-2 text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring" />
      </div>
      {pages === null ? <p className="px-2 py-4 text-sm text-muted-foreground">Đang tải…</p> : <>
        <p className="mt-4 px-2.5 text-xs font-semibold tracking-wide text-muted-foreground">CHUNG</p>
        <ul className="mt-1 space-y-0.5">{general.map(pageButton)}</ul>
        {general.length === 0 ? <p className="px-2.5 py-1 text-xs text-muted-foreground">Chưa có trang chung.</p> : null}
        <p className="mt-4 px-2.5 text-xs font-semibold tracking-wide text-muted-foreground">KHÁCH HÀNG</p>
        {customers.length === 0 ? <p className="px-2.5 py-1 text-xs text-muted-foreground">Chưa có trang theo khách hàng.</p> : null}
        {customers.map(name => <div key={name} className="mt-1">
          <p className="flex items-center gap-2 px-2.5 py-1 text-sm font-medium"><Building2 aria-hidden className="size-4 text-muted-foreground" />{name}</p>
          <ul className="ml-3 space-y-0.5 border-l pl-1">{shown.filter(item => item.customerName === name).sort((a, b) => a.title.localeCompare(b.title, "vi")).map(pageButton)}</ul>
        </div>)}
      </>}
      <button type="button" disabled={busy || pages === null || pages.length >= knowledgeLimits.pages} onClick={() => editor()({ title: "", customerName: "", body: "" })}
        className="mt-4 flex w-full items-center gap-2 rounded-lg px-2.5 py-1.5 text-sm text-muted-foreground hover:bg-muted hover:text-foreground disabled:opacity-50">
        <Plus aria-hidden className="size-4" />Trang mới
      </button>
    </aside>

    <article aria-label="Trang kiến thức" className="min-w-0 p-5 sm:p-8">
      {error ? <p role="alert" className="mb-4 flex flex-wrap items-center gap-3 rounded-lg border border-destructive/30 bg-destructive/5 px-3 py-2 text-sm text-destructive">
        {error}
        {page ? <button type="button" className={button} onClick={() => void openPage(page.id)}>Tải lại trang</button> : null}
      </p> : null}
      {draft ? <form onSubmit={submit} className="space-y-4">
        <h2 className="text-lg font-semibold">{editingExisting ? "Sửa trang" : "Trang mới"}</h2>
        <label className="block text-sm font-medium">Tiêu đề
          <input required value={draft.title} maxLength={knowledgeLimits.name} onChange={event => setDraft({ ...draft, title: event.target.value })} className={`mt-1.5 ${field}`} />
        </label>
        <label className="block text-sm font-medium">Khách hàng <span className="font-normal text-muted-foreground">(để trống nếu là trang chung)</span>
          <input value={draft.customerName} maxLength={knowledgeLimits.name} list="knowledge-customers" onChange={event => setDraft({ ...draft, customerName: event.target.value })} className={`mt-1.5 ${field}`} />
          <datalist id="knowledge-customers">{knownCustomers.map(name => <option key={name} value={name} />)}</datalist>
        </label>
        <label className="block text-sm font-medium">Nội dung
          <textarea value={draft.body} maxLength={knowledgeLimits.body} rows={14} onChange={event => setDraft({ ...draft, body: event.target.value })} className={`mt-1.5 font-mono ${field}`} />
        </label>
        <p className="text-xs text-muted-foreground">Gõ <code># </code> cho tiêu đề, <code>- </code> cho gạch đầu dòng, <code>- [ ] </code> cho việc cần làm, <code>&gt; </code> cho ghi chú nổi bật. Mã phiếu như YC-0012 sẽ hiện ở mục Phiếu liên quan.</p>
        <div className="flex gap-2">
          <button type="submit" disabled={busy || !draft.title.trim()} className={primary}>{busy ? "Đang lưu…" : "Lưu trang"}</button>
          <button type="button" disabled={busy} onClick={() => { setDraft(null); setError(""); }} className={button}>Huỷ</button>
        </div>
      </form> : page ? <PageView page={page} names={names} userId={userId} busy={busy} confirmDelete={confirmDelete}
        onEdit={() => editor(page.id)({ title: page.title, customerName: page.customerName ?? "", body: page.body })}
        onToggle={line => { void save({ title: page.title, customerName: page.customerName ?? "", body: toggleKnowledgeTodo(page.body, line) }, page.rowVersion, page.id); }}
        onDelete={() => confirmDelete ? void remove() : setConfirmDelete(true)} onCancelDelete={() => setConfirmDelete(false)} onOpenTickets={onOpenTickets} />
      : pages !== null ? <div className="grid h-full place-items-center py-16 text-center">
        <div>
          <FileText aria-hidden className="mx-auto size-8 text-muted-foreground" />
          <p className="mt-3 font-medium">Chưa có trang nào được mở</p>
          <p className="mt-1 text-sm text-muted-foreground">Tạo trang đầu tiên cho quy trình chung hoặc cho từng khách hàng.</p>
          <button type="button" onClick={() => editor()({ title: "", customerName: "", body: "" })} className={`mt-4 ${primary}`}><Plus aria-hidden className="size-4" />Trang mới</button>
        </div>
      </div> : null}
    </article>
  </div>;
}

function PageView({ page, names, userId, busy, confirmDelete, onEdit, onToggle, onDelete, onCancelDelete, onOpenTickets }: {
  page: KnowledgePage; names: Record<string, string>; userId: string; busy: boolean; confirmDelete: boolean;
  onEdit: () => void; onToggle: (line: number) => void; onDelete: () => void; onCancelDelete: () => void; onOpenTickets?: (customer: string) => void }) {
  const editor = page.updatedBy.toLowerCase() === userId.toLowerCase() ? "bạn" : names[page.updatedBy.toLowerCase()] ?? "thành viên";
  const codes = knowledgeTicketCodes(page.body);
  const blocks = knowledgeBlocks(page.body);
  return <div className="max-w-3xl">
    <nav aria-label="Vị trí trang" className="text-sm text-muted-foreground">Kiến thức / {page.customerName ?? "Chung"}</nav>
    <div className="mt-2 flex flex-wrap items-start justify-between gap-3">
      <h2 className="text-2xl font-semibold tracking-tight">{page.title}</h2>
      <div className="flex gap-2">
        <button type="button" disabled={busy} onClick={onEdit} className={button}>Sửa</button>
        <button type="button" disabled={busy} onClick={onDelete} className={confirmDelete ? `${button} border-destructive text-destructive` : button}>
          {confirmDelete ? "Xác nhận xoá" : "Xoá"}
        </button>
        {confirmDelete ? <button type="button" disabled={busy} onClick={onCancelDelete} className={button}>Giữ lại</button> : null}
      </div>
    </div>
    <p className="mt-1 text-sm text-muted-foreground">Sửa bởi {editor} · {time(page.updatedAt)}</p>
    <div className="mt-6 space-y-2.5 text-[15px] leading-relaxed">
      {blocks.length === 0 ? <p className="text-muted-foreground">Trang này chưa có nội dung. Bấm Sửa để viết.</p> : null}
      {blocks.map((block, index) => block.kind === "heading" ? <h3 key={index} className="pt-3 text-lg font-semibold">{block.text}</h3>
        : block.kind === "todo" ? <label key={index} className="flex cursor-pointer items-start gap-2.5">
          <input type="checkbox" checked={block.done} disabled={busy} onChange={() => onToggle(block.line)} className="mt-1 size-4 accent-primary" />
          <span className={block.done ? "text-muted-foreground line-through" : ""}>{block.text}</span>
        </label>
        : block.kind === "bullet" ? <p key={index} className="flex gap-2.5"><span aria-hidden className="mt-2.5 size-1.5 shrink-0 rounded-full bg-muted-foreground" />{block.text}</p>
        : block.kind === "callout" ? <p key={index} className="flex gap-2.5 rounded-lg border bg-muted/50 px-3.5 py-3"><Lightbulb aria-hidden className="mt-0.5 size-4 shrink-0 text-amber-500" />{block.text}</p>
        : <p key={index} className="whitespace-pre-wrap">{block.text}</p>)}
    </div>
    {codes.length || (page.customerName && onOpenTickets) ? <section aria-label="Phiếu liên quan" className="mt-8 border-t pt-5">
      <h3 className="text-sm font-semibold">Phiếu liên quan</h3>
      <div className="mt-2 flex flex-wrap items-center gap-2">
        {codes.map(code => <span key={code} className="rounded-md border bg-muted/40 px-2 py-0.5 font-mono text-xs">{code}</span>)}
        {page.customerName && onOpenTickets ? <button type="button" onClick={() => onOpenTickets(page.customerName!)} className={button}>
          Xem phiếu của {page.customerName}
        </button> : null}
      </div>
    </section> : null}
  </div>;
}
