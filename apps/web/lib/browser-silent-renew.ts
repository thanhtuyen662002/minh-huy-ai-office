import { browserLoginDestination, type PublicBrowserLogin } from "./browser-login-navigation";

// One tab renews at a time; the others pick up the shared cookie on their next check.
const RENEW_LOCK_KEY = "aioffice-silent-renew-at";
const RENEW_LOCK_MS = 20_000;
// Same as OIDC_CALLBACK_PATH; that module is server-only.
const CALLBACK_PATH = "/api/local/session/oidc/callback";
export const SILENT_RENEW_INTERVAL_MS = 20 * 60_000;

function claimRenewal(): boolean {
  try {
    const last = Number(window.localStorage.getItem(RENEW_LOCK_KEY));
    if (Number.isFinite(last) && Date.now() - last < RENEW_LOCK_MS) return false;
    window.localStorage.setItem(RENEW_LOCK_KEY, String(Date.now()));
    return true;
  } catch { return true; }
}

async function post(url: string, body?: unknown): Promise<unknown> {
  const response = await fetch(url, {
    method: "POST", cache: "no-store", redirect: "error", credentials: "same-origin",
    ...(body === undefined ? {} : { headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) }),
  });
  if (!response.ok) throw new Error();
  return await response.json();
}

// Renews the browser session from the provider's existing sign-in in a hidden frame, so the
// user is never sent to the sign-in page while that sign-in is alive. Resolves "renewed",
// "busy" when another tab is renewing, or "failed" when the provider needs the user.
export async function renewBrowserSessionSilently(companyId: string, settings: PublicBrowserLogin,
  timeoutMs = 10_000): Promise<"renewed" | "busy" | "failed"> {
  if (!claimRenewal()) return "busy";
  try {
    const prepared = await post("/api/local/session/oidc/binding");
    if (!prepared || typeof prepared !== "object" || Object.getOwnPropertyDescriptor(prepared, "ok")?.value !== true) return "failed";
    const destination = browserLoginDestination(await post("/api/local/session/oidc/start?prompt=none", { companyId }), settings, true);
    if (!destination) return "failed";
    return await new Promise(resolve => {
      const frame = document.createElement("iframe");
      frame.hidden = true; frame.tabIndex = -1; frame.setAttribute("aria-hidden", "true"); frame.title = "AI Office";
      const finish = (result: "renewed" | "failed") => { clearTimeout(timer); frame.remove(); resolve(result); };
      const timer = setTimeout(() => finish("failed"), timeoutMs);
      frame.addEventListener("load", () => {
        try {
          // Reading the frame throws while it is still on the provider's origin.
          if (frame.contentWindow?.location.pathname !== CALLBACK_PATH) return;
          finish(frame.contentDocument?.documentElement.dataset.signIn === "ok" ? "renewed" : "failed");
        } catch { /* Still at the provider. */ }
      });
      frame.src = destination;
      document.body.appendChild(frame);
    });
  } catch { return "failed"; }
}
