import { NextResponse } from "next/server";
import { isCanonicalCompanyId } from "./company-scope";
import type { BrowserOidcSettings } from "./browser-oidc";

export function privateBrowserResponse<T extends Response>(response: T): T {
  response.headers.set("Cache-Control", "no-store");
  response.headers.set("Pragma", "no-cache");
  response.headers.set("Referrer-Policy", "no-referrer");
  return response;
}
export function browserAuthError(status: number): Response {
  return privateBrowserResponse(Response.json({ error: "Browser sign-in could not be verified." }, { status }));
}
// The provider callback is a top-level page, so a refused sign-in (most often an expired
// sign-in page) shows a short notice and returns to the app, which starts a fresh sign-in.
export function browserCallbackRefusal(status: number): Response {
  const page = `<!doctype html><html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="refresh" content="3;url=/"><title>Đăng nhập lại</title>
<style>body{margin:0;min-height:100vh;display:grid;place-items:center;font:15px/1.6 system-ui,sans-serif;background:#f8fafc;color:#0f172a}
main{max-width:420px;padding:24px;text-align:center}a{color:#2563eb}</style></head>
<body><main><h1 style="font-size:20px">Phiên đăng nhập đã hết hạn</h1>
<p>Trang đăng nhập đã mở quá lâu hoặc không còn hợp lệ. AI Office sẽ tự mở lại sau vài giây.</p>
<p><a href="/">Quay lại AI Office</a></p></main></body></html>`;
  return privateBrowserResponse(new Response(page, { status, headers: { "Content-Type": "text/html; charset=utf-8" } }));
}
export function setPrivateBrowserCookie(response: NextResponse, settings: BrowserOidcSettings,
  name: string, value: string, maxAge: number) {
  response.cookies.set({ name, value, maxAge, httpOnly: true, secure: !settings.localHttp, sameSite: "lax", path: "/" });
}

export async function readBrowserCompany(request: Request): Promise<string | null> {
  if (request.headers.get("content-type")?.split(";")[0].trim().toLowerCase() !== "application/json") return null;
  const reader = request.body?.getReader();
  if (!reader) return null;
  let timer: ReturnType<typeof setTimeout> | undefined;
  async function consume(): Promise<string | null> {
    const chunks: Uint8Array[] = [];
    let length = 0;
    while (true) {
      const chunk = await reader!.read();
      if (chunk.done) break;
      length += chunk.value.byteLength;
      if (length > 2048) {
        try { await reader!.cancel(); } catch { /* Refusal survives cleanup failure. */ }
        return null;
      }
      chunks.push(chunk.value);
    }
    const bytes = new Uint8Array(length);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    const value = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
    return value && typeof value === "object" && !Array.isArray(value) && Object.keys(value).join(",") === "companyId"
      && isCanonicalCompanyId(value.companyId) ? value.companyId : null;
  }
  try {
    return await Promise.race([consume(), new Promise<null>((resolve) => {
      timer = setTimeout(() => { void reader.cancel().catch(() => {}); resolve(null); }, 10_000);
    })]);
  } catch { return null; }
  finally { clearTimeout(timer); reader.releaseLock(); }
}
