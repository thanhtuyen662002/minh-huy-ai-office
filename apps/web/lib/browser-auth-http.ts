import { NextResponse } from "next/server";
import { isCanonicalCompanyId } from "./local-ai-bff";
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
