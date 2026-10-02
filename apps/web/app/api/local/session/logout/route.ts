import { NextResponse } from "next/server";
import {
  LOCAL_ACCESS_TOKEN_COOKIE,
  localCookieSecure,
} from "../../../../../lib/local-ai-bff";

export async function POST() {
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
