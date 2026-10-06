import { copyCoreResponse, fetchCoreApi, isCanonicalCompanyId, isLocalAiUiEnabled, localUiDisabledResponse, unauthenticatedResponse } from "./local-ai-bff";

function failure(message: string, status: number) {
  return Response.json({ error: message }, { status, headers: { "Cache-Control": "no-store" } });
}

function hasSameOrigin(request: Request, url: URL) {
  if (url.protocol !== "http:" && url.protocol !== "https:") return false;
  try {
    let expected = url.origin;
    const host = request.headers.get("host");
    if (host) {
      // Next.js may construct request.url from its container hostname. Host is
      // the authority the browser addressed; caller-supplied forwarded hosts
      // must not change the origin allowed to use the session cookie.
      const authority = new URL(`${url.protocol}//${host}`);
      if (authority.username || authority.password || authority.pathname !== "/" || authority.search || authority.hash) return false;
      expected = authority.origin;
    }
    return request.headers.get("origin") === expected;
  } catch {
    return false;
  }
}

export async function mutateLocalDataSource(request: Request, method: "POST" | "PUT", path: string,
  strict?: { validate: (value: unknown) => boolean; maxBodyBytes: number }) {
  if (!isLocalAiUiEnabled()) return localUiDisabledResponse();
  const url = new URL(request.url);
  if (!hasSameOrigin(request, url)) return failure("Same-origin request is required.", 403);
  const companyId = url.searchParams.get("companyId");
  if (!isCanonicalCompanyId(companyId)) return failure("Invalid company selection.", 400);
  let payload: unknown;
  try {
    if (strict) {
      const reader = request.body?.getReader();
      if (!reader) return failure("Invalid source metadata.", 400);
      const chunks: Uint8Array[] = [];
      let length = 0;
      try {
        while (true) {
          const chunk = await reader.read();
          if (chunk.done) break;
          length += chunk.value.byteLength;
          if (length > strict.maxBodyBytes) { await reader.cancel(); return failure("Registration request is too large.", 413); }
          chunks.push(chunk.value);
        }
      } finally { reader.releaseLock(); }
      const body = new Uint8Array(length);
      let offset = 0;
      for (const chunk of chunks) { body.set(chunk, offset); offset += chunk.byteLength; }
      payload = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(body));
      if (!strict.validate(payload)) return failure("Invalid source registration.", 400);
    } else payload = await request.json();
  } catch { return failure("Invalid source metadata.", 400); }
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) return failure("Invalid source metadata.", 400);
  try {
    const response = await fetchCoreApi(path, companyId, { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload) });
    return response ? copyCoreResponse(response) : unauthenticatedResponse();
  } catch {
    return failure("Core API is unavailable.", 502);
  }
}
