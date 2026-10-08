import { cookies } from "next/headers";
import { getBrowserAuthRuntime } from "./browser-auth-runtime";
import { browserBindingCookieName, browserSessionCookieName } from "./browser-session-store";

// A selected company comes only from the private issued session after Core
// membership validation at callback. URL/storage values cannot choose scope.
export async function readSelectedBrowserCompany(): Promise<string | null> {
  try {
    const runtime = getBrowserAuthRuntime(); if (!runtime) return null;
    const store = await cookies();
    const binding = store.get(browserBindingCookieName(runtime.settings))?.value;
    const sid = store.get(browserSessionCookieName(runtime.settings))?.value;
    if (!binding || !sid) return null;
    return (await runtime.sessions.read(binding, sid))?.companyId ?? null;
  } catch { return null; }
}
