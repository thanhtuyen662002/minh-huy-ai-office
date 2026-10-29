"use client";

import { ArrowRight, Bot, KanbanSquare, MessagesSquare, Plug } from "lucide-react";
import type { WorkspaceSection } from "../lib/workspace-sections";
import { SectionHeader } from "./workspace-shell";

const shortcuts: { section: WorkspaceSection; title: string; text: string; icon: typeof Bot }[] = [
  { section: "cong-viec", title: "Công việc", text: "Yêu cầu khách đã được AI tổng hợp: việc mới, đang xử lý, cần hỏi lại, đã xong.", icon: KanbanSquare },
  { section: "tin-nhan", title: "Tin nhắn", text: "Tin nhắn từ các nhóm Zalo khách hàng mà bot đang theo dõi.", icon: MessagesSquare },
  { section: "tro-ly", title: "Trợ lý", text: "Hỏi AI về số liệu trong ERP của khách hàng.", icon: Bot },
  { section: "ket-noi", title: "Kết nối", text: "Bật/tắt bot, chọn nhóm khách, nguồn dữ liệu ERP và thành viên.", icon: Plug },
];

export function WorkspaceOverview({ companyName, onOpen }: { companyName: string; onOpen: (section: WorkspaceSection) => void }) {
  return <>
    <SectionHeader title="Tổng quan" description={`Không gian làm việc của ${companyName}.`} />
    <div className="grid gap-4 sm:grid-cols-2">
      {shortcuts.map(({ section, title, text, icon: Icon }) => <button key={section} type="button" onClick={() => onOpen(section)} aria-label={`Mở ${title}`}
        className="group flex items-start gap-4 rounded-xl border bg-card p-5 text-left shadow-sm transition-colors hover:border-ring/50">
        <span className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-accent text-accent-foreground"><Icon className="size-5" aria-hidden="true" /></span>
        <span className="min-w-0 flex-1">
          <span className="flex items-center gap-1.5 font-semibold">{title}<ArrowRight className="size-4 opacity-0 transition-opacity group-hover:opacity-100" aria-hidden="true" /></span>
          <span className="mt-1 block text-sm leading-6 text-muted-foreground">{text}</span>
        </span>
      </button>)}
    </div>
  </>;
}
