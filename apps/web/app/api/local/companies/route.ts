import { parseCompanyChoices } from "../../../../lib/company-choices";
import { fetchCoreApi, isCanonicalCompanyId, isOfficeAiUiEnabled, localUiDisabledResponse,
  unauthenticatedResponse } from "../../../../lib/local-ai-bff";

const unavailable = (status: number) => Response.json({ error: "Không xác minh được danh sách công ty." },
  { status, headers: { "Cache-Control": "no-store" } });

async function readChoices(response: Response, signal: AbortSignal) {
  const reader = response.body?.getReader(); if (!reader) return null;
  let onAbort: (() => void) | undefined;
  try {
    if (signal.aborted) throw new Error();
    const consume = async () => {
      const chunks: Uint8Array[] = []; let length = 0;
      while (true) {
        const next = await reader.read(); if (next.done) break;
        length += next.value.byteLength;
        if (length > 128 * 1024) { void reader.cancel().catch(() => {}); throw new Error(); }
        chunks.push(next.value);
      }
      const bytes = new Uint8Array(length); let offset = 0;
      for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
      // JSON Response helpers repair invalid UTF-8. Authoritative company
      // names must be accepted from the exact bytes or refused, never repaired.
      return parseCompanyChoices(JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)));
    };
    return await Promise.race([consume(), new Promise<never>((_resolve, reject) => {
      onAbort = () => { void reader.cancel().catch(() => {}); reject(new Error()); };
      signal.addEventListener("abort", onAbort, { once: true });
    })]);
  } finally {
    if (onAbort) signal.removeEventListener("abort", onAbort);
    reader.releaseLock();
  }
}

export async function GET(request: Request) {
  if (!isOfficeAiUiEnabled()) return localUiDisabledResponse();
  const selectors = new URL(request.url).searchParams.getAll("companyId");
  if (selectors.length !== 1 || !isCanonicalCompanyId(selectors[0])) return unavailable(400);
  try {
    const deadline = AbortSignal.timeout(10_000);
    const response = await fetchCoreApi("/api/auth/companies", selectors[0], { signal: deadline });
    if (!response) return unauthenticatedResponse();
    if (!response.ok) return unavailable(response.status === 401 ? 401 : response.status === 403 ? 403 : 503);
    if (response.headers.get("content-type")?.split(";")[0].trim().toLowerCase() !== "application/json") return unavailable(503);
    const items = await readChoices(response, deadline);
    if (!items) return unavailable(503);
    return Response.json({ items }, { headers: { "Cache-Control": "no-store" } });
  } catch { return unavailable(503); }
}
