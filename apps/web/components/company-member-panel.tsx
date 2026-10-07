"use client";
import { useEffect, useRef, useState } from "react";
import { MemberPage, parseMemberPage } from "../lib/company-members";

type Props = { companyId: string; generation: number; isCurrent: (generation: number) => boolean;
  validate: (generation: number) => Promise<boolean>; request: (generation: number, url: string, init?: RequestInit) => Promise<Response>; onUnauthorized: () => void };

export function CompanyMemberPanel({ companyId, generation, isCurrent, validate, request, onUnauthorized }: Props) {
  const [page, setPage] = useState<MemberPage | null>(null);
  const [offset, setOffset] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const mounted = useRef(false);
  const lock = useRef(false);
  const current = () => mounted.current && isCurrent(generation);
  async function load(nextOffset: number) {
    if (lock.current || !current()) return;
    lock.current = true; setLoading(true); setPage(null); setError(""); setOffset(nextOffset);
    try {
      if (!await validate(generation) || !current()) return;
      const response = await request(generation, `/api/local/company/members?companyId=${encodeURIComponent(companyId)}&offset=${nextOffset}&limit=25`, { cache: "no-store" });
      if (!current()) return;
      if (response.status === 401) { onUnauthorized(); return; }
      if (response.status === 403) {
        await validate(generation);
        if (!current()) return;
        throw new Error("Không còn quyền xem thành viên. Hãy xác nhận lại quyền quản trị.");
      }
      if (!response.ok) throw new Error("Không tải được thành viên. Hãy thử lại.");
      const received = parseMemberPage(await response.json());
      if (!current()) return;
      if (!received || received.companyId !== companyId || received.offset !== nextOffset || received.limit !== 25)
        throw new Error("Danh sách thành viên không hợp lệ. Hãy thử lại.");
      if (!await validate(generation) || !current()) return;
      setPage(received);
    } catch {
      if (current()) { setPage(null); setError("Không thể xác nhận danh sách thành viên. Hãy tải lại."); }
    } finally { lock.current = false; if (current()) setLoading(false); }
  }
  useEffect(() => {
    mounted.current = true; void load(0);
    return () => { mounted.current = false; };
    // Company/session key in the parent owns this private directory lifetime.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  return <section aria-label="Thành viên công ty" className="rounded-2xl border border-slate-200 bg-white p-5 dark:border-white/10 dark:bg-[#11182a]">
    <div className="flex items-center justify-between gap-3"><h2 className="text-lg font-semibold">Thành viên công ty</h2>
      <button type="button" disabled={loading} onClick={() => void load(offset)} className="rounded-lg border px-3 py-2 text-sm">Tải lại thành viên</button></div>
    {loading ? <p role="status" className="mt-4">Đang tải thành viên…</p> : null}
    {error ? <p role="alert" className="mt-4 text-rose-600">{error}</p> : null}
    {page ? <><div className="mt-5 overflow-x-auto"><table className="w-full text-left text-sm"><thead><tr>
      <th className="p-2">Thành viên</th><th className="p-2">Tài khoản</th><th className="p-2">Quyền thành viên</th><th className="p-2">Vai trò</th>
    </tr></thead><tbody>{page.items.map(member => <tr key={member.userId} className="border-t border-slate-100 dark:border-white/10">
      <td className="p-2">{member.displayName}</td><td className="p-2">{member.userActive ? "Đang hoạt động" : "Đã khóa tài khoản"}</td>
      <td className="p-2">{member.membershipActive ? "Đang hoạt động" : "Đã tắt thành viên"}</td><td className="p-2">{member.roles.join(", ") || "Chưa gán vai trò"}</td>
    </tr>)}</tbody></table></div>{!page.items.length ? <p className="mt-4">Chưa có thành viên trong trang này.</p> : null}
    <div className="mt-4 flex items-center gap-3"><button type="button" disabled={loading || offset === 0} onClick={() => void load(Math.max(0, offset - 25))} className="rounded-lg border px-3 py-2 text-sm">Trang trước</button>
      <span className="text-sm">Trang {Math.floor(offset / 25) + 1}</span>
      <button type="button" disabled={loading || !page.hasMore || offset + 25 > 1000} onClick={() => void load(offset + 25)} className="rounded-lg border px-3 py-2 text-sm">Trang sau</button></div>
    {page.hasMore && offset + 25 > 1000 ? <p className="mt-3 text-sm">Đã đến giới hạn danh sách. Liên hệ quản trị viên hạ tầng để xem thêm.</p> : null}</> : null}
  </section>;
}
