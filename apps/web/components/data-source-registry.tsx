import type { DataSourceView } from "../lib/data-source-registry";
import { dataSourcesForCompany } from "../lib/data-source-registry";

type Props = { companyId: string; companyName: string; sources: DataSourceView[] };

export function DataSourceRegistry({ companyId, companyName, sources }: Props) {
  if (!companyId.trim()) {
    return <section role="alert" className="rounded-2xl border border-black/10 p-6 dark:border-white/15"><h2 className="text-xl font-semibold">Không thể mở nguồn dữ liệu</h2><p className="mt-2 opacity-70">Cần xác định công ty hợp lệ trước khi hiển thị cấu hình nguồn dữ liệu.</p></section>;
  }
  const scoped = dataSourcesForCompany(companyId, sources);
  return (
    <section aria-labelledby="datasource-heading" className="space-y-5">
      <div><p className="text-sm font-medium opacity-60">Cấu hình theo công ty</p><h2 id="datasource-heading" className="mt-1 text-2xl font-semibold">Nguồn dữ liệu · {companyName}</h2><p className="mt-2 max-w-2xl text-sm leading-6 opacity-70">Quản lý metadata và quyền truy cập. Thông tin bí mật đã lưu không bao giờ được hiển thị lại; thay đổi bí mật là một thao tác riêng.</p></div>
      <div className="grid gap-4 xl:grid-cols-2">{scoped.map((source) => <article key={source.id} className="rounded-2xl border border-black/10 p-5 dark:border-white/15"><div className="flex items-start justify-between gap-4"><div><h3 className="font-semibold">{source.logicalName}</h3><p className="mt-1 text-sm opacity-60">{source.purpose}</p></div><span className="rounded-full bg-black/5 px-2.5 py-1 text-xs dark:bg-white/10">{source.enabled ? "Đang bật" : "Đã tắt"}</span></div><dl className="mt-5 grid grid-cols-2 gap-3 text-sm"><div><dt className="opacity-50">Loại</dt><dd className="font-medium">{source.kind}</dd></div><div><dt className="opacity-50">Môi trường</dt><dd className="font-medium">{source.environment}</dd></div><div><dt className="opacity-50">Quyền</dt><dd className="font-medium">{source.access === "read" ? "Chỉ đọc" : "Đọc / ghi"}</dd></div><div><dt className="opacity-50">Đồng thời tối đa</dt><dd className="font-medium">{source.maxConcurrency}</dd></div></dl><div className="mt-5 rounded-xl bg-black/[0.035] p-4 text-sm dark:bg-white/[0.06]"><p className="font-medium">Trạng thái kết nối</p><p className="mt-1 opacity-70">{source.connectionMessage}</p></div><div className="mt-4 flex flex-wrap gap-2"><button type="button" className="rounded-lg border border-black/15 px-3 py-2 text-sm dark:border-white/20">Sửa metadata</button><button type="button" className="rounded-lg border border-black/15 px-3 py-2 text-sm dark:border-white/20">Đổi thông tin bí mật</button></div><p className="mt-3 text-xs opacity-55">{source.secretConfigured ? "Thông tin bí mật: đã cấu hình · giá trị được ẩn" : "Thông tin bí mật: chưa cấu hình"}</p></article>)}</div>
      <button type="button" className="rounded-lg bg-black px-4 py-2.5 text-sm font-medium text-white dark:bg-white dark:text-black">Thêm nguồn dữ liệu</button>
    </section>
  );
}
