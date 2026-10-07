import { NextResponse } from "next/server";
import { hasSameOrigin } from "../../../../../lib/request-origin";
import {
  COMPANY_SELECTOR_HEADER,
  LOCAL_ACCESS_TOKEN_COOKIE,
  isCanonicalCompanyId,
  isLocalAiUiEnabled,
  localClientId,
  localCookieSecure,
  localCoreApiUrl,
  localTokenUrl,
  localUiDisabledResponse,
} from "../../../../../lib/local-ai-bff";

type LoginBody = {
  username?: unknown;
  password?: unknown;
  companyId?: unknown;
};

function canonicalCredential(value: unknown, maximumLength: number): value is string {
  return (
    typeof value === "string"
    && value.length > 0
    && value.length <= maximumLength
    && value.trim() === value
    && !Array.from(value).some((character) => /[\r\n\0]/.test(character))
  );
}

function loginError(message: string, status: number) {
  return Response.json({ error: message }, { status, headers: { "Cache-Control": "no-store" } });
}

async function readLoginBody(request: Request): Promise<{ value: unknown } | { error: Response }> {
  if (request.headers.get("content-type")?.split(";")[0].trim().toLowerCase() !== "application/json") {
    return { error: loginError("JSON login payload is required.", 415) };
  }
  const reader = request.body?.getReader();
  if (!reader) return { error: loginError("Invalid login payload.", 400) };
  try {
    const chunks: Uint8Array[] = [];
    let length = 0;
    while (true) {
      const chunk = await reader.read();
      if (chunk.done) break;
      length += chunk.value.byteLength;
      if (length > 8192) {
        try { await reader.cancel(); } catch { /* Refusal does not depend on stream cleanup. */ }
        return { error: loginError("Login payload is too large.", 413) };
      }
      chunks.push(chunk.value);
    }
    const bytes = new Uint8Array(length);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    return { value: JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)) };
  } catch {
    return { error: loginError("Invalid login payload.", 400) };
  } finally {
    reader.releaseLock();
  }
}

export async function POST(request: Request) {
  if (!isLocalAiUiEnabled()) return localUiDisabledResponse();
  if (!hasSameOrigin(request)) return loginError("Same-origin request is required.", 403);
  const parsed = await readLoginBody(request);
  if ("error" in parsed) return parsed.error;
  const body = parsed.value as LoginBody;

  if (
    body === null
    || typeof body !== "object"
    || Array.isArray(body)
    || Object.keys(body).length !== 3
    || Object.keys(body).some((key) => key !== "username" && key !== "password" && key !== "companyId")
    || !canonicalCredential(body.username, 200)
    || !canonicalCredential(body.password, 1024)
    || !isCanonicalCompanyId(body.companyId)
  ) {
    return loginError("Invalid login payload.", 400);
  }

  const form = new URLSearchParams({
    grant_type: "password",
    client_id: localClientId(),
    username: body.username,
    password: body.password,
  });

  let tokenResponse: Response;
  try {
    tokenResponse = await fetch(localTokenUrl(), {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: form,
      cache: "no-store",
    });
  } catch {
    return loginError("Identity service is unavailable.", 503);
  }

  if (!tokenResponse.ok) {
    return loginError("Tên đăng nhập hoặc mật khẩu không đúng.", 401);
  }

  let tokenPayload: unknown;
  try {
    tokenPayload = await tokenResponse.json();
  } catch {
    return loginError("Identity service returned an invalid response.", 502);
  }

  if (tokenPayload === null || typeof tokenPayload !== "object") {
    return loginError("Identity service returned an invalid response.", 502);
  }

  const token = Reflect.get(tokenPayload, "access_token");
  const expiresIn = Reflect.get(tokenPayload, "expires_in");

  if (typeof token !== "string" || token.length === 0 || token.length > 16_384) {
    return loginError("Identity service returned an invalid access token.", 502);
  }

  let contextResponse: Response;
  try {
    contextResponse = await fetch(`${localCoreApiUrl()}/api/auth/context`, {
      headers: {
        Authorization: `Bearer ${token}`,
        [COMPANY_SELECTOR_HEADER]: body.companyId,
        Accept: "application/json",
      },
      cache: "no-store",
    });
  } catch {
    return loginError("Core API is unavailable.", 503);
  }

  if (contextResponse.status === 403) {
    return loginError("Tài khoản không có quyền vào công ty này.", 403);
  }

  if (!contextResponse.ok) {
    return loginError("Không thể xác thực phạm vi công ty.", 502);
  }

  let context: unknown;
  try {
    context = await contextResponse.json();
  } catch {
    return loginError("Core API returned an invalid response.", 502);
  }
  const maxAge = typeof expiresIn === "number" && Number.isSafeInteger(expiresIn)
    ? Math.min(Math.max(expiresIn, 60), 3600)
    : 900;

  const response = NextResponse.json({ ok: true, context });
  response.cookies.set({
    name: LOCAL_ACCESS_TOKEN_COOKIE,
    value: token,
    httpOnly: true,
    sameSite: "lax",
    secure: localCookieSecure(),
    path: "/",
    maxAge,
  });
  response.headers.set("Cache-Control", "no-store");
  return response;
}
