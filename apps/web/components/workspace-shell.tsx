"use client";

import { useEffect, useRef, useState, type ReactNode } from "react";
import { BookOpen, Bot, KanbanSquare, LayoutDashboard, LogOut, MessagesSquare, Moon, PanelLeftClose, PanelLeftOpen, Plug, Sun } from "lucide-react";
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
  onSignOut: () => void;
  children: ReactNode;
};

export function WorkspaceShell({ section, onSection, companyId, companyName, companies, companyDisabled, onSelectCompany,
  roleLabel, accountLabel, onSignOut, children }: Props) {
  const [collapsed, setCollapsed] = useState(false);
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
          <div className={`flex h-14 items-center gap-2.5 px-4 ${collapsed ? "lg:justify-center lg:px-0" : ""}`}>
            <div className="flex size-8 shrink-0 items-center justify-center rounded-lg bg-primary text-sm font-bold text-primary-foreground">AI</div>
            <div className={collapsed ? "lg:hidden" : ""}>
              <p className="text-sm font-semibold leading-tight">AI Office</p>
              <p className="text-xs text-muted-foreground">Minh Huy</p>
            </div>
          </div>
          <nav aria-label="Điều hướng chính" className="flex gap-1 overflow-x-auto px-3 pb-2 lg:mt-2 lg:flex-1 lg:flex-col lg:overflow-visible lg:pb-0">
            {sections.map(([key, label]) => {
              const Icon = icons[key], active = key === section;
              return <button key={key} ref={active ? activeItem : undefined} type="button" onClick={() => onSection(key)} aria-current={active ? "page" : undefined} title={collapsed ? label : undefined}
                className={`flex shrink-0 items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors ${collapsed ? "lg:justify-center lg:px-0" : ""} ${active
                  ? "bg-accent text-accent-foreground" : "text-muted-foreground hover:bg-muted hover:text-foreground"}`}>
                <Icon className="size-4 shrink-0" aria-hidden="true" />
                <span className={collapsed ? "lg:sr-only" : ""}>{label}</span>
              </button>;
            })}
          </nav>
          <div className="hidden border-t border-sidebar-border p-3 lg:block">
            <button type="button" onClick={toggleSidebar} aria-label={collapsed ? "Mở rộng thanh bên" : "Thu gọn thanh bên"}
              className={`flex w-full items-center gap-3 rounded-lg px-3 py-2 text-sm text-muted-foreground hover:bg-muted hover:text-foreground ${collapsed ? "justify-center px-0" : ""}`}>
              {collapsed ? <PanelLeftOpen className="size-4" /> : <><PanelLeftClose className="size-4" /> Thu gọn</>}
            </button>
          </div>
        </aside>

        <div className="flex min-w-0 flex-1 flex-col">
          <header className="sticky top-0 z-10 flex min-h-14 flex-wrap items-center justify-between gap-3 border-b bg-background/85 px-4 py-2 backdrop-blur sm:px-6">
            <div className="min-w-0">
              {companies.length > 1 ? <>
                <label htmlFor="workspace-company" className="sr-only">Chuyển công ty</label>
                <select id="workspace-company" value={companies.find(item => item.companyId.toLowerCase() === companyId.toLowerCase())?.companyId ?? companyId}
                  disabled={companyDisabled} onChange={event => onSelectCompany(event.target.value)}
                  className="max-w-full rounded-lg border bg-card px-2.5 py-1.5 text-sm font-medium">
                  {companies.map(item => <option key={item.companyId} value={item.companyId}>{item.companyName}</option>)}
                </select>
              </> : <h2 className="break-words text-sm font-semibold">{companyName}</h2>}
            </div>
            <div className="flex items-center gap-2">
              <span className="hidden text-xs text-muted-foreground sm:inline">{accountLabel} · {roleLabel}</span>
              <button type="button" onClick={onSignOut} className="inline-flex h-9 items-center gap-2 rounded-lg border bg-card px-3 text-sm font-medium hover:bg-muted">
                <LogOut className="size-4" aria-hidden="true" />Đăng xuất
              </button>
              <ThemeToggle />
            </div>
          </header>
          <div className="min-w-0 flex-1 px-4 py-5 sm:px-6 sm:py-6">{children}</div>
        </div>
      </div>
    </main>
  );
}

export function SectionHeader({ title, description, actions }: { title: string; description?: string; actions?: ReactNode }) {
  return <div className="mb-5 flex flex-wrap items-end justify-between gap-3">
    <div>
      <h1 className="text-xl font-semibold tracking-tight">{title}</h1>
      {description ? <p className="mt-1 text-sm text-muted-foreground">{description}</p> : null}
    </div>
    {actions}
  </div>;
}

export function SectionTabs<T extends string>({ label, tabs, value, onChange }: {
  label: string; tabs: readonly (readonly [T, string])[]; value: T; onChange: (value: T) => void }) {
  return <div role="group" aria-label={label} className="mb-5 inline-flex max-w-full gap-1 overflow-x-auto rounded-lg bg-muted p-1">
    {tabs.map(([key, text]) => <button key={key} type="button" aria-pressed={key === value} onClick={() => onChange(key)}
      className={`shrink-0 rounded-md px-3 py-1.5 text-sm font-medium ${key === value ? "bg-card text-foreground shadow-sm" : "text-muted-foreground hover:text-foreground"}`}>
      {text}
    </button>)}
  </div>;
}
