import type { BrowserOidcSettings } from "./browser-oidc";

export type BrowserProofStep = "configuration" | "origin" | "transaction" | "response" | "claim"
  | "exchange" | "signed-exchange" | "authority" | "publish" | "lifetime";
const codes = new Set(["OAUTH_INVALID_RESPONSE", "OAUTH_RESPONSE_BODY_ERROR", "OAUTH_INVALID_REQUEST",
  "OAUTH_JWT_CLAIM_COMPARISON_FAILED", "OAUTH_JWT_TIMESTAMP_CHECK_FAILED", "OAUTH_KEY_SELECTION_FAILED",
  "OAUTH_SIGNATURE_VERIFICATION_FAILED", "OAUTH_UNSUPPORTED_OPERATION", "OAUTH_INVALID_SERVER_METADATA"]);

// Explicit disposable-local proof only. Never emit provider bodies, messages,
// causes, token/claim values, cookies or configuration; public errors stay generic.
export function reportBrowserProof(settings: BrowserOidcSettings | undefined, step: BrowserProofStep, error?: unknown) {
  if (process.env.AIOFFICE_BROWSER_CI_PROOF !== "true" || !settings?.localHttp
    || !settings.publicOrigin.startsWith("http://127.0.0.1:")) return;
  let code: unknown;
  try { code = error && typeof error === "object" ? Object.getOwnPropertyDescriptor(error, "code")?.value : null; }
  catch { /* Diagnostics never change authentication refusal. */ }
  console.warn(`[aioffice-browser-proof] ${step}${typeof code === "string" && codes.has(code) ? " " + code : ""}`);
}
