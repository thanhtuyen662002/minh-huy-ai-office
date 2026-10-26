"use client";
import { useEffect, useRef, useState } from "react";
import { CompanyMember, MemberAccessInput, MemberPage, parseMemberAccessResult, parseMemberPage } from "../lib/company-members";
import { AdministratorInput, parseAdministratorResult } from "../lib/company-administrators";
import { CompanyInvitation, CompanyInvitationPage, InvitationRole, parseInvitation, parseInvitationPage } from "../lib/company-invitations";

type MemberOperation = { userId: string; name: string } &
  ({ kind: "access"; input: MemberAccessInput } | { kind: "administrator"; input: AdministratorInput });

type Props = { companyId: string; userId?: string; generation: number; isCurrent: (generation: number) => boolean;
  validate: (generation: number) => Promise<boolean>; request: (generation: number, url: string, init?: RequestInit) => Promise<Response>; onUnauthorized: () => void };

export function CompanyMemberPanel({ companyId, userId, generation, isCurrent, validate, request, onUnauthorized }: Props) {
  const [page, setPage] = useState<MemberPage | null>(null);
  const [offset, setOffset] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [pending, setPending] = useState<MemberOperation | null>(null);
  const [notice, setNotice] = useState("");
  const [invitationPage, setInvitationPage] = useState<CompanyInvitationPage | null>(null);
  const [invitationError, setInvitationError] = useState("");
  const [invitationEmail, setInvitationEmail] = useState("");
  const [invitationRole, setInvitationRole] = useState<InvitationRole>("viewer");
  const [signInUrl, setSignInUrl] = useState("");
  const mounted = useRef(false);
  const lock = useRef(false);
  const current = () => mounted.current && isCurrent(generation);
  async function confirmAuthority() {
    const valid = await validate(generation);
    if (!valid && current()) { setPage(null); setPending(null); }
    return valid && current();
  }
  async function load(nextOffset: number) {
    if (lock.current || !current()) return;
    lock.current = true; setLoading(true); setPage(null); setError(""); setOffset(nextOffset);
    try {
      if (!await confirmAuthority()) return;
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
      if (!await confirmAuthority()) return;
      setPage(received);
      await loadInvitations();
    } catch {
      if (current()) { setPage(null); setError("Không thể xác nhận danh sách thành viên. Hãy tải lại."); }
    } finally { lock.current = false; if (current()) setLoading(false); }
  }
  async function loadInvitations() {
    setInvitationPage(null); setInvitationError("");
    try {
      const response = await request(generation, `/api/local/company/invitations?companyId=${encodeURIComponent(companyId)}&offset=0&limit=25`, { cache: "no-store" });
      if (!current()) return;
      if (response.status === 401) { onUnauthorized(); return; }
      if (response.status === 403) {
        await validate(generation);
        if (current()) setInvitationError("Không còn quyền xem lời mời. Hãy xác nhận lại quyền quản trị.");
        return;
      }
      if (!response.ok) throw new Error("Unavailable");
      const received = parseInvitationPage(await response.json());
      if (!received || received.companyId !== companyId || received.offset !== 0 || received.limit !== 25) throw new Error("Invalid page");
      if (!await confirmAuthority()) return;
      setInvitationPage(received);
    } catch {
      if (current()) setInvitationError("Không thể xác nhận danh sách lời mời đang chờ. Hãy tải lại.");
    }
  }
  async function changeInvitation(path: string, body?: { email: string; roles: InvitationRole[] }) {
    if (lock.current || !current()) return;
    lock.current = true; setLoading(true); setInvitationError(""); setNotice("");
    try {
      if (!await confirmAuthority()) return;
      const response = await request(generation, `${path}?companyId=${encodeURIComponent(companyId)}`, {
        method: "POST", headers: body ? { "Content-Type": "application/json" } : undefined,
        body: body ? JSON.stringify(body) : undefined,
      });
      if (!current()) return;
      if (response.status === 401) { setPage(null); setInvitationPage(null); onUnauthorized(); return; }
      if (response.status === 403) {
        setPage(null); setInvitationPage(null); await validate(generation);
        if (current()) setInvitationError("Không còn quyền quản lý lời mời. Hãy xác nhận lại quyền quản trị.");
        return;
      }
      if (response.status === 409) {
        const received = await response.json().catch(() => ({}));
        if (!await confirmAuthority()) return;
        const code = typeof received?.code === "string" ? received.code : "";
        setInvitationError(code === "duplicate-invitation" ? "Email này đã có lời mời đang chờ."
          : code === "existing-member" ? "Email này đã là thành viên công ty."
          : "Lời mời đã thay đổi. Hãy tải lại danh sách.");
        return;
      }
      if (!response.ok) throw new Error("Unavailable");
      const result = parseInvitation(await response.json());
      if (!result || result.companyId !== companyId || path.includes("/revoke") && result.revokedAtUtc === null) throw new Error("Invalid result");
      if (!await confirmAuthority()) return;
      if (body) setInvitationEmail("");
      lock.current = false;
      await load(offset);
      if (current()) setNotice(body ? "Đã tạo lời mời thành viên." : "Đã thu hồi lời mời thành viên.");
    } catch {
      if (current()) {
        let valid = false; try { valid = await validate(generation); } catch { }
        if (valid && current()) setInvitationError("Chưa xác nhận được kết quả. Hãy tải lại danh sách lời mời.");
      }
    } finally { lock.current = false; if (current()) setLoading(false); }
  }
  function submitInvitation(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const email = invitationEmail.trim();
    if (!/^[\x21-\x7e]+@[\x21-\x7e]+$/.test(email) || email.length > 254) {
      setInvitationError("Nhập địa chỉ email hợp lệ."); return;
    }
    void changeInvitation("/api/local/company/invitations", { email, roles: [invitationRole] });
  }
  async function copySignInLink() {
    if (!signInUrl || !current()) return;
    try { await navigator.clipboard.writeText(signInUrl); if (current()) setNotice("Đã sao chép liên kết đăng nhập."); }
    catch { if (current()) setInvitationError("Không sao chép được liên kết. Hãy sao chép thủ công."); }
  }
  async function setAccess(operation: NonNullable<typeof pending>) {
    if (lock.current || !current()) return;
    lock.current = true; setLoading(true); setPending(operation); setError(""); setNotice("");
    try {
      if (!await confirmAuthority()) return;
      const response = await request(generation, `/api/local/company/members/${encodeURIComponent(operation.userId)}/${operation.kind}?companyId=${encodeURIComponent(companyId)}`,
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
          "self-role-change": "Bạn không thể thay đổi quyền quản trị của tài khoản đang sử dụng.",
          "last-administrator": "Công ty cần giữ ít nhất một quản trị viên đang hoạt động.", "inactive-user": "Tài khoản này đang bị khóa. Hãy liên hệ quản trị viên tài khoản." };
        if (!await confirmAuthority()) return;
        setPage(null); setPending(null); setError(typeof body?.code === "string" && Object.hasOwn(messages, body.code)
          ? messages[body.code] : "Quyền thành viên đã thay đổi. Hãy tải lại danh sách trước khi tiếp tục.");
        return;
      }
      if (response.status === 400 || response.status === 404) {
        if (!await confirmAuthority()) return;
        setPage(null); setPending(null); setError("Không thể thay đổi thành viên này. Hãy tải lại danh sách."); return;
      }
      if (!response.ok) throw new Error("Unavailable");
      const body: unknown = await response.json();
      const result = operation.kind === "access" ? parseMemberAccessResult(body) : parseAdministratorResult(body);
      if (!current()) return;
      if (!result || result.companyId !== companyId || result.userId !== operation.userId ||
        result.operationId !== operation.input.operationId) throw new Error("Invalid result");
      if (operation.kind === "access" ? !("membershipActive" in result) || result.membershipActive !== operation.input.isActive
        : !("isAdministrator" in result) || result.isAdministrator !== operation.input.isAdministrator) throw new Error("Invalid result");
      if (!await confirmAuthority()) return;
      setPending(null); lock.current = false; await load(offset);
      if (current()) setNotice(operation.kind === "access"
        ? operation.input.isActive ? "Đã mở lại quyền truy cập thành viên." : "Đã khóa quyền truy cập thành viên."
        : operation.input.isAdministrator ? "Đã cấp quyền quản trị công ty." : "Đã gỡ quyền quản trị công ty.");
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
    void setAccess({ kind: "access", userId: member.userId, name: member.displayName,
      input: { operationId: crypto.randomUUID(), expectedVersion: member.membershipVersion, isActive: !member.membershipActive } });
  }
  function startAdministrator(member: CompanyMember) {
    if (!member.membershipVersion || !member.membershipActive || !member.userActive || member.userId === userId) return;
    void setAccess({ kind: "administrator", userId: member.userId, name: member.displayName,
      input: { operationId: crypto.randomUUID(), expectedVersion: member.membershipVersion, isAdministrator: !member.roles.includes("admin") } });
  }
  useEffect(() => {
    mounted.current = true; setSignInUrl(new URL("/", window.location.origin).toString()); void load(0);
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
    <section aria-label="Lời mời thành viên" className="mt-5 rounded-xl border border-slate-200 p-4 dark:border-white/10">
      <h3 className="font-semibold">Thêm thành viên</h3>
      <form className="mt-3 grid gap-3 sm:grid-cols-[minmax(0,1fr)_180px_auto] sm:items-end" onSubmit={submitInvitation}>
        <label className="grid gap-1 text-sm">Email
          <input aria-label="Email thành viên" type="email" required maxLength={254} autoComplete="email" value={invitationEmail}
            onChange={event => setInvitationEmail(event.target.value)} disabled={loading || !!pending}
            className="rounded-lg border border-slate-300 bg-transparent px-3 py-2 dark:border-white/15" />
        </label>
        <label className="grid gap-1 text-sm">Vai trò
          <select aria-label="Vai trò lời mời" value={invitationRole} onChange={event => setInvitationRole(event.target.value as InvitationRole)}
            disabled={loading || !!pending} className="rounded-lg border border-slate-300 bg-transparent px-3 py-2 dark:border-white/15">
            <option value="viewer">Thành viên</option><option value="admin">Quản trị viên</option>
          </select>
        </label>
        <button type="submit" disabled={loading || !!pending} className="rounded-lg border px-3 py-2 text-sm font-medium">Thêm thành viên</button>
      </form>
      <div className="mt-3 grid gap-1 text-sm"><label htmlFor="company-sign-in-link">Liên kết đăng nhập để gửi cho thành viên</label>
        <div className="flex gap-2"><input id="company-sign-in-link" readOnly value={signInUrl} className="min-w-0 flex-1 rounded-lg border border-slate-300 bg-transparent px-3 py-2 dark:border-white/15" />
          <button type="button" disabled={!signInUrl} onClick={() => void copySignInLink()} className="rounded-lg border px-3 py-2">Sao chép</button></div></div>
      {invitationError ? <p role="alert" className="mt-3 text-sm text-rose-600">{invitationError}</p> : null}
      <h4 className="mt-5 font-medium">Lời mời đang chờ</h4>
      {invitationPage ? invitationPage.items.length ? <ul className="mt-2 grid gap-2">{invitationPage.items.map(invitation =>
        <li key={invitation.id} className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-slate-100 p-3 text-sm dark:border-white/10">
          <span><strong>{invitation.email}</strong> · {invitation.roles.includes("admin") ? "Quản trị viên" : "Thành viên"}
            <span className="ml-2 text-slate-500">Hết hạn {new Date(invitation.expiresAtUtc).toLocaleDateString("vi-VN")}</span></span>
          <button type="button" disabled={loading || !!pending} onClick={() => void changeInvitation(`/api/local/company/invitations/${encodeURIComponent(invitation.id)}/revoke`)}
            className="rounded-lg border px-3 py-2" aria-label={`Thu hồi lời mời ${invitation.email}`}>Thu hồi</button>
        </li>)}</ul> : <p className="mt-2 text-sm text-slate-500">Chưa có lời mời đang chờ.</p> : null}
    </section>
    {pending && !loading ? <button type="button" onClick={() => void setAccess(pending)} className="mt-3 rounded-lg border px-3 py-2 text-sm">Thử lại thao tác với {pending.name}</button> : null}
    {page ? <><div className="mt-5 overflow-x-auto"><table className="w-full text-left text-sm"><thead><tr>
      <th className="p-2">Thành viên</th><th className="p-2">Tài khoản</th><th className="p-2">Quyền thành viên</th><th className="p-2">Vai trò</th><th className="p-2">Thao tác</th>
    </tr></thead><tbody>{page.items.map(member => <tr key={member.userId} className="border-t border-slate-100 dark:border-white/10">
      <td className="p-2">{member.displayName}</td><td className="p-2">{member.userActive ? "Đang hoạt động" : "Đã khóa tài khoản"}</td>
      <td className="p-2">{member.membershipActive ? "Đang hoạt động" : "Đã tắt thành viên"}</td><td className="p-2">{member.roles.join(", ") || "Chưa gán vai trò"}</td>
      <td className="p-2"><button type="button" disabled={loading || !!pending || !member.membershipVersion || !member.userActive || member.userId === userId}
        onClick={() => startAccess(member)} className="rounded-lg border px-3 py-2 text-sm" aria-label={`${member.membershipActive ? "Khóa quyền" : "Mở lại quyền"} ${member.displayName}`}>{member.membershipActive ? "Khóa quyền" : "Mở lại quyền"}</button>
        <button type="button" disabled={loading || !!pending || !member.membershipVersion || !member.userActive || !member.membershipActive || member.userId === userId}
          onClick={() => startAdministrator(member)} className="ml-2 rounded-lg border px-3 py-2 text-sm" aria-label={`${member.roles.includes("admin") ? "Gỡ quyền quản trị" : "Cấp quyền quản trị"} ${member.displayName}`}>{member.roles.includes("admin") ? "Gỡ quyền quản trị" : "Cấp quyền quản trị"}</button></td>
    </tr>)}</tbody></table></div>{!page.items.length ? <p className="mt-4">Chưa có thành viên trong trang này.</p> : null}
    <div className="mt-4 flex items-center gap-3"><button type="button" disabled={loading || !!pending || offset === 0} onClick={() => void load(Math.max(0, offset - 25))} className="rounded-lg border px-3 py-2 text-sm">Trang trước</button>
      <span className="text-sm">Trang {Math.floor(offset / 25) + 1}</span>
      <button type="button" disabled={loading || !!pending || !page.hasMore || offset + 25 > 1000} onClick={() => void load(offset + 25)} className="rounded-lg border px-3 py-2 text-sm">Trang sau</button></div>
    {page.hasMore && offset + 25 > 1000 ? <p className="mt-3 text-sm">Đã đến giới hạn danh sách. Liên hệ quản trị viên hạ tầng để xem thêm.</p> : null}</> : null}
  </section>;
}
