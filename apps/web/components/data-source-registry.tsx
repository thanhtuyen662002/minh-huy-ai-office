"use client";

import { useState } from "react";
import type { FormEvent } from "react";
import type { DataSourceMetadataEdit, DataSourceView } from "../lib/data-source-registry";
import { applyMetadataEdit, createSecretRotationIntent, dataSourcesForCompany } from "../lib/data-source-registry";

type Props = { companyId: string; companyName: string; sources: DataSourceView[] };
type Action = { kind: "create" } | { kind: "edit" | "rotate"; sourceId: string } | null;

const fieldClass = "mt-1 w-full rounded-lg border border-black/15 bg-transparent px-3 py-2 dark:border-white/20";
const buttonClass = "rounded-lg border border-black/15 px-3 py-2 text-sm dark:border-white/20";

export function DataSourceRegistry({ companyId, companyName, sources }: Props) {
  const [items, setItems] = useState(() => dataSourcesForCompany(companyId, sources));
  const [action, setAction] = useState<Action>(null);
  const [notice, setNotice] = useState("");
  if (!companyId.trim()) return <section role="alert" className="rounded-2xl border border-black/10 p-6 dark:border-white/15"><h2 className="text-xl font-semibold">Không thể mở nguồn dữ liệu</h2><p className="mt-2 opacity-70">Cần xác định công ty hợp lệ trước khi hiển thị cấu hình nguồn dữ liệu.</p></section>;

  const selected = action && "sourceId" in action ? items.find((item) => item.id === action.sourceId) : undefined;
  function close() { setAction(null); setNotice(""); }
  function saveMetadata(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    const edit: DataSourceMetadataEdit = {
      logicalName: String(data.get("logicalName") ?? "").trim(), kind: data.get("kind") as DataSourceView["kind"], environment: data.get("environment") as DataSourceView["environment"], purpose: String(data.get("purpose") ?? "").trim(), access: data.get("access") as DataSourceView["access"], maxConcurrency: Number(data.get("maxConcurrency")), enabled: data.get("enabled") === "on",
    };
    if (!edit.logicalName || !edit.purpose || !Number.isInteger(edit.maxConcurrency) || edit.maxConcurrency < 1) { setNotice("Hãy nhập đủ tên, mục đích và mức đồng thời hợp lệ."); return; }
    if (action?.kind === "edit" && selected) setItems((current) => current.map((item) => item.id === selected.id ? applyMetadataEdit(item, edit) : item));
    if (action?.kind === "create") setItems((current) => [...current, { ...edit, id: `local-${current.length + 1}`, companyId, connectionStatus: "unknown", connectionMessage: "Chưa kiểm tra kết nối.", secretConfigured: false }]);
    setNotice(action?.kind === "create" ? "Đã thêm cấu hình. Hãy dùng thao tác đổi thông tin bí mật để cấu hình quyền truy cập." : "Đã cập nhật metadata. Thông tin bí mật đã lưu không thay đổi.");
    setAction(null);
  }
  function rotateSecret(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!selected) return;
    const form = event.currentTarget;
    const replacementSecret = String(new FormData(form).get("replacementSecret") ?? "");
    if (!createSecretRotationIntent(selected.id, replacementSecret)) { setNotice("Nhập thông tin bí mật mới để xác nhận thao tác đổi."); return; }
    setItems((current) => current.map((item) => item.id === selected.id ? { ...item, secretConfigured: true } : item));
    form.reset();
    setAction(null);
    setNotice("Đã ghi nhận yêu cầu đổi thông tin bí mật. Giá trị không được hiển thị lại.");
  }

  return <section aria-labelledby="datasource-heading" className="space-y-5">
    <div><p className="text-sm font-medium opacity-60">Cấu hình theo công ty</p><h2 id="datasource-heading" className="mt-1 text-2xl font-semibold">Nguồn dữ liệu · {companyName}</h2><p className="mt-2 max-w-2xl text-sm leading-6 opacity-70">Quản lý metadata và quyền truy cập. Thông tin bí mật đã lưu không bao giờ được hiển thị lại; thay đổi bí mật là một thao tác riêng.</p></div>
    {notice && <p role="status" className="rounded-xl bg-black/[0.035] p-3 text-sm dark:bg-white/[0.06]">{notice}</p>}
    <div className="grid gap-4 xl:grid-cols-2">{items.map((source) => <article key={source.id} className="rounded-2xl border border-black/10 p-5 dark:border-white/15"><div className="flex items-start justify-between gap-4"><div><h3 className="font-semibold">{source.logicalName}</h3><p className="mt-1 text-sm opacity-60">{source.purpose}</p></div><span className="rounded-full bg-black/5 px-2.5 py-1 text-xs dark:bg-white/10">{source.enabled ? "Đang bật" : "Đã tắt"}</span></div><dl className="mt-5 grid grid-cols-2 gap-3 text-sm"><div><dt className="opacity-50">Loại</dt><dd className="font-medium">{source.kind}</dd></div><div><dt className="opacity-50">Môi trường</dt><dd className="font-medium">{source.environment}</dd></div><div><dt className="opacity-50">Quyền</dt><dd className="font-medium">{source.access === "read" ? "Chỉ đọc" : "Đọc / ghi"}</dd></div><div><dt className="opacity-50">Đồng thời tối đa</dt><dd className="font-medium">{source.maxConcurrency}</dd></div></dl><div className="mt-5 rounded-xl bg-black/[0.035] p-4 text-sm dark:bg-white/[0.06]"><p className="font-medium">Trạng thái kết nối</p><p className="mt-1 opacity-70">{source.connectionMessage}</p></div><div className="mt-4 flex flex-wrap gap-2"><button type="button" className={buttonClass} onClick={() => { setNotice(""); setAction({ kind: "edit", sourceId: source.id }); }}>Sửa metadata <span className="sr-only">{source.logicalName}</span></button><button type="button" className={buttonClass} onClick={() => { setNotice(""); setAction({ kind: "rotate", sourceId: source.id }); }}>Đổi thông tin bí mật <span className="sr-only">{source.logicalName}</span></button></div><p className="mt-3 text-xs opacity-55">{source.secretConfigured ? "Thông tin bí mật: đã cấu hình · giá trị được ẩn" : "Thông tin bí mật: chưa cấu hình"}</p></article>)}</div>
    <button type="button" className="rounded-lg bg-black px-4 py-2.5 text-sm font-medium text-white dark:bg-white dark:text-black" onClick={() => { setNotice(""); setAction({ kind: "create" }); }}>Thêm nguồn dữ liệu</button>
    {(action?.kind === "create" || action?.kind === "edit") && <MetadataForm source={selected} onSubmit={saveMetadata} onCancel={close} />}
    {action?.kind === "rotate" && selected && <form aria-label={`Đổi thông tin bí mật · ${selected.logicalName}`} onSubmit={rotateSecret} className="space-y-4 rounded-2xl border border-black/10 p-5 dark:border-white/15"><div><h3 className="font-semibold">Đổi thông tin bí mật</h3><p className="mt-1 text-sm opacity-65">Nhập giá trị thay thế. Giá trị hiện tại không được tải hoặc hiển thị lại.</p></div><label className="block text-sm">Thông tin bí mật mới<input name="replacementSecret" type="password" autoComplete="new-password" className={fieldClass} /></label>{notice && <p role="alert" className="text-sm">{notice}</p>}<div className="flex gap-2"><button className={buttonClass} type="submit">Xác nhận đổi</button><button className={buttonClass} type="button" onClick={close}>Hủy</button></div></form>}
  </section>;
}

function MetadataForm({ source, onSubmit, onCancel }: { source?: DataSourceView; onSubmit: (event: FormEvent<HTMLFormElement>) => void; onCancel: () => void }) {
  return <form aria-label={source ? `Sửa metadata · ${source.logicalName}` : "Thêm nguồn dữ liệu"} onSubmit={onSubmit} className="grid gap-4 rounded-2xl border border-black/10 p-5 dark:border-white/15 sm:grid-cols-2"><h3 className="font-semibold sm:col-span-2">{source ? "Sửa metadata" : "Thêm nguồn dữ liệu"}</h3><label className="text-sm">Tên logic<input name="logicalName" defaultValue={source?.logicalName} className={fieldClass} /></label><label className="text-sm">Mục đích<input name="purpose" defaultValue={source?.purpose} className={fieldClass} /></label><label className="text-sm">Loại<select name="kind" defaultValue={source?.kind ?? "sql-server"} className={fieldClass}><option value="sql-server">SQL Server</option><option value="erp">ERP</option><option value="api">API</option></select></label><label className="text-sm">Môi trường<select name="environment" defaultValue={source?.environment ?? "production"} className={fieldClass}><option value="production">Production</option><option value="staging">Staging</option><option value="development">Development</option></select></label><label className="text-sm">Quyền<select name="access" defaultValue={source?.access ?? "read"} className={fieldClass}><option value="read">Chỉ đọc</option><option value="read-write">Đọc / ghi</option></select></label><label className="text-sm">Đồng thời tối đa<input name="maxConcurrency" type="number" min="1" defaultValue={source?.maxConcurrency ?? 1} className={fieldClass} /></label><label className="flex items-center gap-2 text-sm sm:col-span-2"><input name="enabled" type="checkbox" defaultChecked={source?.enabled ?? true} />Đang bật</label><p className="text-xs opacity-60 sm:col-span-2">Metadata không chứa thông tin bí mật. Hãy dùng thao tác đổi thông tin bí mật riêng khi cần.</p>{!source && <p className="text-xs opacity-60 sm:col-span-2">Nguồn mới được tạo chưa có thông tin bí mật.</p>}<div className="flex gap-2 sm:col-span-2"><button className={buttonClass} type="submit">Lưu metadata</button><button className={buttonClass} type="button" onClick={onCancel}>Hủy</button></div></form>;
}
