import { NextResponse } from "next/server";
import {
  LOCAL_ACCESS_TOKEN_COOKIE,
  isLocalAiUiEnabled,
  localCookieSecure,
  localUiDisabledResponse,
} from "../../../../../lib/local-ai-bff";
import { hasSameOrigin } from "../../../../../lib/request-origin";

export async function POST(request: Request) {
  if (!isLocalAiUiEnabled()) return localUiDisabledResponse();
  if (!hasSameOrigin(request)) return Response.json({ error: "Same-origin request is required." }, {
    status: 403, headers: { "Cache-Control": "no-store" },
  });
  const response = NextResponse.json({ ok: true });
  response.cookies.set({
    name: LOCAL_ACCESS_TOKEN_COOKIE,
    value: "",
    httpOnly: true,
    sameSite: "lax",
    secure: localCookieSecure(),
    path: "/",
    maxAge: 0,
  });
  response.headers.set("Cache-Control", "no-store");
  return response;
}
