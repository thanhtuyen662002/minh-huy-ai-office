"use client";
import { useEffect, useRef, useState } from "react";
import { Send } from "lucide-react";
import { TelegramLink, TelegramLinkCode, parseTelegramLink, parseTelegramLinkCode } from "../lib/telegram-link";

type Props = { companyId: string; generation: number;
  isCurrent: (generation: number) => boolean; validate: (generation: number) => Promise<boolean>;
  request: (generation: number, url: string, init?: RequestInit) => Promise<Response>; onUnauthorized: () => void };
const button = "inline-flex h-8 items-center justify-center gap-1.5 rounded-lg border bg-card px-3 text-sm font-medium hover:bg-muted disabled:pointer-events-none disabled:opacity-50";
const primary = "inline-flex h-8 items-center justify-center gap-1.5 rounded-lg bg-primary px-3 text-sm font-medium text-primary-foreground hover:opacity-90 disabled:pointer-events-none disabled:opacity-50";

// Lets a technician link their Telegram account so the bot can hand them tickets. Hidden while the feature is off (404).
export function TelegramLinkCard({ companyId, generation, isCurrent, validate, request, onUnauthorized }: Props) {
  const [link, setLink] = useState<TelegramLink | null>(null);
  const [code, setCode] = useState<TelegramLinkCode | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const mounted = useRef(false);
  const path = `/api/local/telegram-link?companyId=${encodeURIComponent(companyId)}`;
  const live = () => mounted.current && isCurrent(generation);

  async function call(init: RequestInit = {}): Promise<Response | null> {
    if (!live() || !await validate(generation) || !live()) return null;
    const response = await request(generation, path, { cache: "no-store", ...init });
    if (!live()) return null;
    if (response.status === 401) { onUnauthorized(); return null; }
    return await validate(generation) && live() ? response : null;
  }

  async function load() {
    try {
      const response = await call();
      if (!response) return;
      const view = response.ok ? parseTelegramLink(await response.json()) : null;
      if (!live()) return;
      setLink(view);
      if (view?.linked) setCode(null);
    } catch { if (live()) setLink(null); }
  }

  async function change(method: "POST" | "DELETE") {
    setBusy(true); setError("");
    try {
      const response = await call({ method });
      if (!response) return;
      if (!response.ok) { setError("Không thực hiện được. Hãy thử lại sau."); return; }
      if (method === "POST") {
        const value = parseTelegramLinkCode(await response.json());
        if (live()) setCode(value);
        if (!value) setError("Không tạo được mã liên kết. Hãy thử lại sau.");
      } else { setCode(null); await load(); }
    } catch { if (live()) setError("Không thực hiện được. Hãy thử lại sau."); }
    finally { if (live()) setBusy(false); }
  }

  useEffect(() => {
    mounted.current = true;
    void load();
    return () => { mounted.current = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [companyId, generation]);

  if (!link) return null;
  return (
    <section aria-label="Liên kết Telegram" className="mb-4 flex flex-wrap items-center justify-between gap-3 rounded-xl border bg-card px-4 py-3 shadow-sm">
      <div className="flex items-center gap-3 text-sm">
        <Send className="size-4 text-sky-500" aria-hidden />
        {link.linked
          ? <span>Đã liên kết Telegram <b>{link.telegramName}</b>. Bot sẽ gửi việc và nút nhận việc cho bạn.</span>
          : code
            ? <span>{code.url
                ? <>Mở <a href={code.url} target="_blank" rel="noreferrer" className="font-medium text-sky-600 underline">Telegram</a> và bấm <b>Start</b>.</>
                : <>Nhắn bot Telegram: <code className="rounded bg-muted px-1">/start {code.code}</code></>}
              {" "}Mã hết hạn lúc {new Date(code.expiresAt).toLocaleTimeString("vi-VN", { timeStyle: "short" })}.</span>
            : <span>Liên kết Telegram để nhận việc và cập nhật trạng thái bằng nút bấm.</span>}
        {error ? <span role="alert" className="text-destructive">{error}</span> : null}
      </div>
      <div className="flex gap-2">
        {link.linked
          ? <button type="button" className={button} disabled={busy} onClick={() => void change("DELETE")}>Huỷ liên kết</button>
          : code
            ? <button type="button" className={button} disabled={busy} onClick={() => void load()}>Đã bấm Start</button>
            : <button type="button" className={primary} disabled={busy} onClick={() => void change("POST")}>Liên kết Telegram</button>}
      </div>
    </section>
  );
}
