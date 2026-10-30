"use client";

import { useEffect, useRef, useState, type ReactNode } from "react";
import { BookOpen, Bot, UserRound, KanbanSquare, LayoutDashboard, LogOut, MessagesSquare, Moon, PanelLeftClose, PanelLeftOpen, Plug, Search, Sun } from "lucide-react";
import { workspaceSections, type WorkspaceSection } from "../lib/workspace-sections";
import type { CompanyChoice } from "../lib/company-choices";

const icons: Record<WorkspaceSection, typeof Bot> = {
  "tong-quan": LayoutDashboard, "tin-nhan": MessagesSquare, "cong-viec": KanbanSquare,
  "tro-ly": Bot, "kien-thuc": BookOpen, "ket-noi": Plug,
};

function readPreference(key: string) {
  try { return window.localStorage.getItem(key); } catch { return null; }
}
function savePreference(key: string, value: string) {
  try { window.localStorage.setItem(key, value); } catch { /* Preferences are optional. */ }
}

export function ThemeToggle() {
  const [dark, setDark] = useState(false);
  useEffect(() => { setDark(document.documentElement.classList.contains("dark")); }, []);
  function toggle() {
    const next = !dark;
    document.documentElement.classList.toggle("dark", next);
    savePreference("aioffice-theme", next ? "dark" : "light");
    setDark(next);
  }
  return <button type="button" onClick={toggle} aria-label={dark ? "Chuyển sang giao diện sáng" : "Chuyển sang giao diện tối"}
    className="inline-flex size-9 items-center justify-center rounded-lg border bg-card text-muted-foreground hover:text-foreground">
    {dark ? <Sun className="size-4" /> : <Moon className="size-4" />}
  </button>;
}

type Props = {
  section: WorkspaceSection;
  onSection: (section: WorkspaceSection) => void;
  companyId: string;
  companyName: string;
  companies: readonly CompanyChoice[];
  companyDisabled: boolean;
  onSelectCompany: (companyId: string) => void;
  roleLabel: string;
  accountLabel: string;
  userName?: string | null;
  counts?: Partial<Record<WorkspaceSection, string>>;
  bot?: { label: string; running: boolean } | null;
  onSearch?: (query: string) => void;
  onSignOut: () => void;
  children: ReactNode;
};

const avatar = (name: string) => {
  const words = name.trim().split(/\s+/);
  return (words.length > 1 ? words[0][0] + words[words.length - 1][0] : name.slice(0, 2)).toUpperCase();
};

export function WorkspaceShell({ section, onSection, companyId, companyName, companies, companyDisabled, onSelectCompany,
  roleLabel, accountLabel, userName, counts, bot, onSearch, onSignOut, children }: Props) {
  const [collapsed, setCollapsed] = useState(false);
  const [query, setQuery] = useState("");
  const search = useRef<HTMLInputElement | null>(null);
  useEffect(() => {
    if (!onSearch) return;
    const focus = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") { event.preventDefault(); search.current?.focus(); }
    };
    window.addEventListener("keydown", focus);
    return () => window.removeEventListener("keydown", focus);
  }, [onSearch]);
  useEffect(() => { setCollapsed(readPreference("aioffice-sidebar") === "collapsed"); }, []);
  // On phones the menu is a horizontal strip; keep the current section visible in it.
  const activeItem = useRef<HTMLButtonElement | null>(null);
  useEffect(() => { activeItem.current?.scrollIntoView?.({ block: "nearest", inline: "nearest" }); }, [section]);
  function toggleSidebar() {
    savePreference("aioffice-sidebar", collapsed ? "open" : "collapsed");
    setCollapsed(!collapsed);
  }
  const sections = Object.entries(workspaceSections) as [WorkspaceSection, string][];
  return (
    <main className="min-h-screen bg-background text-foreground">
      <div className="flex min-h-screen flex-col lg:flex-row">
        <aside className={`shrink-0 border-b border-sidebar-border bg-sidebar lg:sticky lg:top-0 lg:flex lg:h-screen lg:flex-col lg:border-b-0 lg:border-r ${collapsed ? "lg:w-16" : "lg:w-60"}`}>
          <div className={`flex h-16 items-center gap-2.5 px-4 ${collapsed ? "lg:justify-center lg:px-0" : ""}`}>
            <img src="/brand/logo-icon.svg" alt="" className="size-8 shrink-0 rounded-lg" />
            <div className={collapsed ? "lg:hidden" : ""}>
              <p className="text-sm font-semibold leading-tight">AI Office</p>
              <p className="text-xs text-muted-foreground">Minh Huy</p>
            </div>
          </div>
          <nav aria-label="Điều hướng chính" className="flex gap-1 overflow-x-auto px-3 pb-2 lg:mt-1 lg:flex-1 lg:flex-col lg:gap-1.5 lg:overflow-visible lg:pb-0">
            {sections.map(([key, label]) => {
              const Icon = icons[key], active = key === section, count = counts?.[key];
              return <button key={key} ref={active ? activeItem : undefined} type="button" onClick={() => onSection(key)} aria-current={active ? "page" : undefined} title={collapsed ? label : undefined}
                className={`flex shrink-0 items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors lg:h-11 ${collapsed ? "lg:justify-center lg:px-0" : ""} ${active
                  ? "bg-accent text-accent-foreground" : "text-muted-foreground hover:bg-muted hover:text-foreground"}`}>
                <Icon className="size-4 shrink-0" aria-hidden="true" />
                <span className={collapsed ? "lg:sr-only" : ""}>{label}</span>
                {count && !collapsed ? <span aria-hidden="true" className={`ml-auto hidden text-xs font-semibold tabular-nums lg:inline ${active ? "" : "text-muted-foreground"}`}>{count}</span> : null}
              </button>;
            })}
          </nav>
          <div className={`hidden items-center gap-1 border-t border-sidebar-border p-3 lg:flex ${collapsed ? "flex-col" : ""}`}>
            <span className={`min-w-0 flex-1 items-center gap-2.5 ${collapsed ? "hidden" : "flex"}`}>
              <span aria-hidden="true" className="flex size-8 shrink-0 items-center justify-center rounded-full bg-accent text-xs font-semibold text-accent-foreground">{userName ? avatar(userName) : <UserRound className="size-4" />}</span>
              <span className="min-w-0"><span className="block truncate text-sm font-medium leading-tight">{userName || accountLabel}</span>
                <span className="block truncate text-xs text-muted-foreground">{roleLabel}</span></span>
            </span>
            <button type="button" onClick={toggleSidebar} aria-label={collapsed ? "Mở rộng thanh bên" : "Thu gọn thanh bên"} title={collapsed ? "Mở rộng thanh bên" : "Thu gọn thanh bên"}
              className="inline-flex size-8 shrink-0 items-center justify-center rounded-lg text-muted-foreground hover:bg-muted hover:text-foreground">
              {collapsed ? <PanelLeftOpen className="size-4" aria-hidden="true" /> : <PanelLeftClose className="size-4" aria-hidden="true" />}
            </button>
          </div>
        </aside>

        <div className="flex min-w-0 flex-1 flex-col">
          <header className="sticky top-0 z-10 flex min-h-16 flex-wrap items-center gap-3 border-b bg-sidebar/90 px-4 py-2.5 backdrop-blur sm:px-6">
            {onSearch ? <form role="search" className="relative w-full max-w-md flex-1" onSubmit={event => { event.preventDefault(); if (query.trim()) onSearch(query.trim()); }}>
              <label htmlFor="workspace-search" className="sr-only">Tìm phiếu theo khách hàng</label>
              <Search className="pointer-events-none absolute left-3 top-2.5 size-4 text-muted-foreground" aria-hidden="true" />
              <input ref={search} id="workspace-search" type="search" maxLength={200} value={query} onChange={event => setQuery(event.target.value)}
                placeholder="Tìm phiếu theo khách hàng…" className="h-9 w-full rounded-lg border bg-background pl-9 pr-16 text-sm" />
              <kbd className="pointer-events-none absolute right-2 top-2 hidden rounded border bg-card px-1.5 py-0.5 font-sans text-[11px] text-muted-foreground sm:block">Ctrl K</kbd>
            </form> : null}
            {companies.length > 1 ? <div className="min-w-0">
              <label htmlFor="workspace-company" className="sr-only">Chuyển công ty</label>
              <select id="workspace-company" value={companies.find(item => item.companyId.toLowerCase() === companyId.toLowerCase())?.companyId ?? companyId}
                disabled={companyDisabled} onChange={event => onSelectCompany(event.target.value)}
                className="h-9 max-w-full rounded-lg border bg-card px-2.5 text-sm font-medium">
                {companies.map(item => <option key={item.companyId} value={item.companyId}>{item.companyName}</option>)}
              </select>
            </div> : onSearch ? null : <h2 className="break-words text-sm font-semibold">{companyName}</h2>}
            <div className="ml-auto flex items-center gap-2">
              {/* First in the header for keyboard users, drawn last. */}
              <button type="button" onClick={onSignOut} aria-label="Đăng xuất" title="Đăng xuất"
                className="order-last inline-flex size-9 items-center justify-center rounded-lg border bg-card text-muted-foreground hover:text-foreground">
                <LogOut className="size-4" aria-hidden="true" /></button>
              {bot ? <span className={`inline-flex h-7 items-center gap-1.5 rounded-full px-2.5 text-xs font-medium ${bot.running
                ? "bg-emerald-50 text-emerald-800 dark:bg-emerald-400/15 dark:text-emerald-200" : "bg-muted text-muted-foreground"}`}>
                <span aria-hidden="true" className={`size-1.5 rounded-full ${bot.running ? "bg-emerald-500" : "bg-slate-400"}`} />{bot.label}</span> : null}
              <ThemeToggle />
            </div>
          </header>
          <div className="min-w-0 flex-1 px-4 py-5 sm:px-8 sm:py-7">{children}</div>
        </div>
      </div>
    </main>
  );
}

export function SectionHeader({ title, description, eyebrow, actions }: { title: string; description?: string; eyebrow?: string; actions?: ReactNode }) {
  return <div className="mb-5 flex flex-wrap items-end justify-between gap-3">
    <div>
      {eyebrow ? <p className="mb-1 text-sm text-muted-foreground">{eyebrow}</p> : null}
      <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
      {description ? <p className="mt-1 text-sm text-muted-foreground">{description}</p> : null}
    </div>
    {actions}
  </div>;
}

export function SectionTabs<T extends string>({ label, tabs, value, onChange }: {
  label: string; tabs: readonly (readonly [T, string])[]; value: T; onChange: (value: T) => void }) {
  return <div role="group" aria-label={label} className="no-scrollbar mb-5 flex max-w-full gap-5 overflow-x-auto overflow-y-hidden shadow-[inset_0_-1px_0_var(--border)]">
    {tabs.map(([key, text]) => <button key={key} type="button" aria-pressed={key === value} onClick={() => onChange(key)}
      className={`shrink-0 border-b-2 pb-2.5 pt-1 text-sm font-medium ${key === value ? "border-foreground text-foreground" : "border-transparent text-muted-foreground hover:text-foreground"}`}>
      {text}
    </button>)}
  </div>;
}
