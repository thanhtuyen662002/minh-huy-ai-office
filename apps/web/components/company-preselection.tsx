"use client";

import { useEffect, useRef, useState } from "react";
import { parseCompanyChoices, type CompanyChoice } from "../lib/company-choices";
import { navigateBrowserLogin } from "../lib/browser-login-navigation";

const endpoint = "/api/local/session/oidc/company";
export function CompanyPreselection() {
  const [choices, setChoices] = useState<readonly CompanyChoice[] | null>(null);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const mounted = useRef(false), pending = useRef(false);
  useEffect(() => {
    mounted.current = true;
    const abort = new AbortController();
    void (async () => {
      try {
        const response = await fetch(endpoint, { cache: "no-store", redirect: "error", credentials: "same-origin", signal: abort.signal });
        const parsed = response.ok ? parseCompanyChoices(await response.json()) : null;
        if (!parsed || parsed.length < 2) throw new Error();
        if (mounted.current && !abort.signal.aborted) setChoices(parsed);
      } catch {
        if (mounted.current && !abort.signal.aborted) setError("Phiên chọn công ty đã hết hạn hoặc không còn hợp lệ. Hãy đăng nhập lại.");
      }
    })();
    return () => { mounted.current = false; abort.abort(); };
  }, []);
  async function submit(companyId: string | null) {
    if (pending.current) return;
    pending.current = true; setBusy(true); setError("");
    try {
      // Do not abort cookie-mutating responses. One mutation settles before
      // navigation; a failed/unknown reply cannot be retried as a new issuance.
      const response = await fetch(endpoint, { method: companyId === null ? "DELETE" : "POST", cache: "no-store",
        redirect: "error", credentials: "same-origin",
        ...(companyId === null ? {} : { headers: { "Content-Type": "application/json" }, body: JSON.stringify({ companyId }) }) });
      const payload = await response.json();
      if (!response.ok || !payload || Object.keys(payload).join(",") !== "redirectTo" || payload.redirectTo !== "/") throw new Error();
      if (mounted.current) navigateBrowserLogin("/");
    } catch {
      if (mounted.current) {
        setChoices(null);
        setError("Không xác nhận được công ty. Hãy trở về trang chủ và đăng nhập lại.");
      }
    } finally { if (mounted.current) setBusy(false); }
  }
  return <main className="mx-auto max-w-xl p-6">
    <h1 className="text-2xl font-semibold">Chọn công ty để tiếp tục</h1>
    <p className="my-4">Bạn đã xác thực danh tính. Chọn một công ty được cấp quyền để mở phiên làm việc.</p>
    {error && <p role="alert">{error}</p>}
    {!choices && !error && <p role="status">Đang tải công ty được cấp quyền…</p>}
    {choices && <ul className="space-y-3">{choices.map((choice) => <li key={choice.companyId}>
      <button type="button" disabled={busy} onClick={() => void submit(choice.companyId)}
        className="rounded border px-4 py-2">{choice.companyName}</button>
    </li>)}</ul>}
    <div className="mt-6">
      {choices ? <button type="button" disabled={busy} onClick={() => void submit(null)}>Hủy và trở về trang chủ</button>
        : <a href="/">Trở về trang chủ</a>}
    </div>
  </main>;
}
