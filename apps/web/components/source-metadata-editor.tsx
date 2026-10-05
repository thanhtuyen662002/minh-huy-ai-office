"use client";

import { FormEvent, useState } from "react";
import { LocalDataSource } from "../lib/local-ai-workspace";
import { SourceMetadataDraft } from "../lib/source-metadata-editor";

type Props = {
  source: LocalDataSource;
  saving: boolean;
  error: string;
  onSave: (draft: SourceMetadataDraft) => void;
  onCancel: () => void;
};

export function SourceMetadataEditor({ source, saving, error, onSave, onCancel }: Props) {
  const [draft, setDraft] = useState<SourceMetadataDraft>({
    logicalName: source.logicalName, purpose: source.purpose,
    maxConcurrency: String(source.maxConcurrency), isEnabled: source.isEnabled,
  });
  const field = "mt-2 w-full rounded-xl border border-slate-200 bg-transparent px-3 py-2 text-sm disabled:opacity-50 dark:border-white/15";
  function submit(event: FormEvent) {
    event.preventDefault();
    if (!saving) onSave(draft);
  }
  return (
    <form aria-label="Chỉnh sửa nguồn dữ liệu" onSubmit={submit} className="mt-5 space-y-4 border-t border-slate-200 pt-5 dark:border-white/10">
      <fieldset disabled={saving} className="space-y-4">
        <label className="block text-sm font-medium">Tên nguồn
          <input name="logicalName" required maxLength={200} value={draft.logicalName} onChange={event => setDraft({ ...draft, logicalName: event.target.value })} className={field} />
        </label>
        <label className="block text-sm font-medium">Mục đích
          <input name="purpose" required maxLength={200} value={draft.purpose} onChange={event => setDraft({ ...draft, purpose: event.target.value })} className={field} />
        </label>
        <label className="block text-sm font-medium">Số tác vụ đồng thời
          <input name="maxConcurrency" type="number" required min={1} max={1024} step={1} value={draft.maxConcurrency} onChange={event => setDraft({ ...draft, maxConcurrency: event.target.value })} className={field} />
        </label>
        <label className="flex items-center gap-3 text-sm font-medium">
          <input name="isEnabled" type="checkbox" checked={draft.isEnabled} onChange={event => setDraft({ ...draft, isEnabled: event.target.checked })} />Bật nguồn dữ liệu
        </label>
      </fieldset>
      <p className="text-xs leading-5 text-slate-500 dark:text-slate-400">Nguồn đã tắt sẽ không xuất hiện khi chọn nguồn cho tác vụ mới. Quyền truy cập và cấu hình kết nối được giữ nguyên.</p>
      {error ? <p role="alert" className="text-sm text-rose-600 dark:text-rose-300">{error}</p> : null}
      <div className="flex gap-3">
        <button type="submit" disabled={saving} className="rounded-xl bg-indigo-600 px-4 py-2 text-sm font-semibold text-white disabled:opacity-50">{saving ? "Đang lưu…" : "Lưu thay đổi"}</button>
        <button type="button" disabled={saving} onClick={onCancel} className="rounded-xl border border-slate-200 px-4 py-2 text-sm disabled:opacity-50 dark:border-white/15">Hủy</button>
      </div>
    </form>
  );
}
