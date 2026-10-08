"use client";
import { useEffect, useRef, useState } from "react";
import { CompanyMember, MemberAccessInput, MemberPage, parseMemberAccessResult, parseMemberPage } from "../lib/company-members";

type Props = { companyId: string; userId?: string; generation: number; isCurrent: (generation: number) => boolean;
  validate: (generation: number) => Promise<boolean>; request: (generation: number, url: string, init?: RequestInit) => Promise<Response>; onUnauthorized: () => void };

export function CompanyMemberPanel({ companyId, userId, generation, isCurrent, validate, request, onUnauthorized }: Props) {
  const [page, setPage] = useState<MemberPage | null>(null);
  const [offset, setOffset] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [pending, setPending] = useState<{ userId: string; name: string; input: MemberAccessInput } | null>(null);
  const [notice, setNotice] = useState("");
  const mounted = useRef(false);
  const lock = useRef(false);
  const current = () => mounted.current && isCurrent(generation);
  async function load(nextOffset: number) {
    if (lock.current || !current()) return;
    lock.current = true; setLoading(true); setPage(null); setError(""); setOffset(nextOffset);
    try {
      if (!await validate(generation) || !current()) return;
      const response = await request(generation, `/api/local/company/members?companyId=${encodeURIComponent(companyId)}&offset=${nextOffset}&limit=25&includeAccessVersion=true`, { cache: "no-store" });
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
  async function setAccess(operation: NonNullable<typeof pending>) {
    if (lock.current || !current()) return;
    lock.current = true; setLoading(true); setPending(operation); setError(""); setNotice("");
    try {
      if (!await validate(generation) || !current()) return;
      const response = await request(generation, `/api/local/company/members/${encodeURIComponent(operation.userId)}/access?companyId=${encodeURIComponent(companyId)}`,
        { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(operation.input) });
      if (!current()) return;
      if (response.status === 401) { setPage(null); setPending(null); onUnauthorized(); return; }
      if (response.status === 403) {
        setPage(null); setPending(null); await validate(generation);
        if (current()) setError("Không còn quyền thay đổi thành viên. Hãy xác nhận lại quyền quản trị.");
        return;
      }
      if (response.status === 409) {
        const body = await response.json().catch(() => ({}));
        const messages: Record<string, string> = { "self-deactivation": "Bạn không thể khóa quyền của tài khoản đang sử dụng.",
          "last-administrator": "Công ty cần giữ ít nhất một quản trị viên đang hoạt động.", "inactive-user": "Tài khoản này đang bị khóa. Hãy liên hệ quản trị viên tài khoản." };
        if (!await validate(generation) || !current()) return;
        setPage(null); setPending(null); setError(typeof body?.code === "string" && Object.hasOwn(messages, body.code)
          ? messages[body.code] : "Quyền thành viên đã thay đổi. Hãy tải lại danh sách trước khi tiếp tục.");
        return;
      }
      if (response.status === 400 || response.status === 404) {
        if (!await validate(generation) || !current()) return;
        setPage(null); setPending(null); setError("Không thể thay đổi thành viên này. Hãy tải lại danh sách."); return;
      }
      if (!response.ok) throw new Error("Unavailable");
      const result = parseMemberAccessResult(await response.json());
      if (!current()) return;
      if (!result || result.companyId !== companyId || result.userId !== operation.userId ||
        result.operationId !== operation.input.operationId || result.membershipActive !== operation.input.isActive) throw new Error("Invalid result");
      if (!await validate(generation) || !current()) return;
      setPending(null); lock.current = false; await load(offset);
      if (current()) setNotice(operation.input.isActive ? "Đã mở lại quyền truy cập thành viên." : "Đã khóa quyền truy cập thành viên.");
    } catch {
      if (current()) {
        setPage(null);
        let valid = false;
        try { valid = await validate(generation); } catch { }
        if (!valid || !current()) { setPending(null); return; }
        setError("Chưa xác nhận được kết quả. Thử lại thao tác để tránh thay đổi trùng lặp.");
      }
    } finally { lock.current = false; if (current()) setLoading(false); }
  }
  function startAccess(member: CompanyMember) {
    if (!member.membershipVersion) return;
    void setAccess({ userId: member.userId, name: member.displayName,
      input: { operationId: crypto.randomUUID(), expectedVersion: member.membershipVersion, isActive: !member.membershipActive } });
  }
  useEffect(() => {
    mounted.current = true; void load(0);
    return () => { mounted.current = false; };
    // Company/session key in the parent owns this private directory lifetime.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  return <section aria-label="Thành viên công ty" className="rounded-2xl border border-slate-200 bg-white p-5 dark:border-white/10 dark:bg-[#11182a]">
    <div className="flex items-center justify-between gap-3"><h2 className="text-lg font-semibold">Thành viên công ty</h2>
      <button type="button" disabled={loading || !!pending} onClick={() => void load(offset)} className="rounded-lg border px-3 py-2 text-sm">Tải lại thành viên</button></div>
    {loading ? <p role="status" className="mt-4">Đang tải thành viên…</p> : null}
    {error ? <p role="alert" className="mt-4 text-rose-600">{error}</p> : null}
    {notice ? <p role="status" className="mt-4">{notice}</p> : null}
    {pending && !loading ? <button type="button" onClick={() => void setAccess(pending)} className="mt-3 rounded-lg border px-3 py-2 text-sm">Thử lại thao tác với {pending.name}</button> : null}
    {page ? <><div className="mt-5 overflow-x-auto"><table className="w-full text-left text-sm"><thead><tr>
      <th className="p-2">Thành viên</th><th className="p-2">Tài khoản</th><th className="p-2">Quyền thành viên</th><th className="p-2">Vai trò</th><th className="p-2">Thao tác</th>
    </tr></thead><tbody>{page.items.map(member => <tr key={member.userId} className="border-t border-slate-100 dark:border-white/10">
      <td className="p-2">{member.displayName}</td><td className="p-2">{member.userActive ? "Đang hoạt động" : "Đã khóa tài khoản"}</td>
      <td className="p-2">{member.membershipActive ? "Đang hoạt động" : "Đã tắt thành viên"}</td><td className="p-2">{member.roles.join(", ") || "Chưa gán vai trò"}</td>
      <td className="p-2"><button type="button" disabled={loading || !!pending || !member.membershipVersion || !member.userActive || member.userId === userId}
        onClick={() => startAccess(member)} className="rounded-lg border px-3 py-2 text-sm" aria-label={`${member.membershipActive ? "Khóa quyền" : "Mở lại quyền"} ${member.displayName}`}>{member.membershipActive ? "Khóa quyền" : "Mở lại quyền"}</button></td>
    </tr>)}</tbody></table></div>{!page.items.length ? <p className="mt-4">Chưa có thành viên trong trang này.</p> : null}
    <div className="mt-4 flex items-center gap-3"><button type="button" disabled={loading || !!pending || offset === 0} onClick={() => void load(Math.max(0, offset - 25))} className="rounded-lg border px-3 py-2 text-sm">Trang trước</button>
      <span className="text-sm">Trang {Math.floor(offset / 25) + 1}</span>
      <button type="button" disabled={loading || !!pending || !page.hasMore || offset + 25 > 1000} onClick={() => void load(offset + 25)} className="rounded-lg border px-3 py-2 text-sm">Trang sau</button></div>
    {page.hasMore && offset + 25 > 1000 ? <p className="mt-3 text-sm">Đã đến giới hạn danh sách. Liên hệ quản trị viên hạ tầng để xem thêm.</p> : null}</> : null}
  </section>;
}
