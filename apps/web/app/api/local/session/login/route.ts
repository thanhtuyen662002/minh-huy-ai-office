import { NextResponse } from "next/server";
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

export async function POST(request: Request) {
  if (!isLocalAiUiEnabled()) return localUiDisabledResponse();

  let body: LoginBody;
  try {
    body = await request.json() as LoginBody;
  } catch {
    return Response.json({ error: "Invalid login payload." }, { status: 400 });
  }

  if (
    !canonicalCredential(body.username, 200)
    || !canonicalCredential(body.password, 1024)
    || !isCanonicalCompanyId(body.companyId)
  ) {
    return Response.json({ error: "Invalid login payload." }, { status: 400 });
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
    return Response.json({ error: "Identity service is unavailable." }, { status: 503 });
  }

  if (!tokenResponse.ok) {
    return Response.json({ error: "Tên đăng nhập hoặc mật khẩu không đúng." }, { status: 401 });
  }

  let tokenPayload: unknown;
  try {
    tokenPayload = await tokenResponse.json();
  } catch {
    return Response.json({ error: "Identity service returned an invalid response." }, { status: 502 });
  }

  if (tokenPayload === null || typeof tokenPayload !== "object") {
    return Response.json({ error: "Identity service returned an invalid response." }, { status: 502 });
  }

  const token = Reflect.get(tokenPayload, "access_token");
  const expiresIn = Reflect.get(tokenPayload, "expires_in");

  if (typeof token !== "string" || token.length === 0 || token.length > 16_384) {
    return Response.json({ error: "Identity service returned an invalid access token." }, { status: 502 });
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
    return Response.json({ error: "Core API is unavailable." }, { status: 503 });
  }

  if (contextResponse.status === 403) {
    return Response.json({ error: "Tài khoản không có quyền vào công ty này." }, { status: 403 });
  }

  if (!contextResponse.ok) {
    return Response.json({ error: "Không thể xác thực phạm vi công ty." }, { status: 502 });
  }

  const context = await contextResponse.json();
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
