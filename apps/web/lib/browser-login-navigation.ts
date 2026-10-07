export type PublicBrowserLogin = Readonly<{
  authorizationEndpoint: string;
  clientId: string;
  redirectUri: string;
  localHttp: boolean;
}>;

// These public values come from validated server configuration, never cookies,
// browser location, provider responses or caller-supplied return URLs.
export function browserLoginDestination(value: unknown, settings: PublicBrowserLogin): string | null {
  if (!value || typeof value !== "object" || Array.isArray(value)
    || Object.keys(value).join(",") !== "authorizationUrl") return null;
  const raw = Object.getOwnPropertyDescriptor(value, "authorizationUrl")?.value;
  if (typeof raw !== "string" || raw.length > 8192 || /[\s\\]/.test(raw)) return null;
  try {
    const url = new URL(raw), endpoint = new URL(settings.authorizationEndpoint);
    const local = settings.localHttp && endpoint.protocol === "http:"
      && ["127.0.0.1", "localhost", "[::1]"].includes(endpoint.hostname);
    if ((!local && endpoint.protocol !== "https:") || endpoint.search || endpoint.hash || endpoint.username || endpoint.password
      || url.origin !== endpoint.origin || url.pathname !== endpoint.pathname || url.username || url.password || url.hash
      || url.href !== raw) return null;
    const required: Record<string, string> = {
      client_id: settings.clientId, redirect_uri: settings.redirectUri, response_type: "code",
      scope: "openid", code_challenge_method: "S256",
    };
    for (const [name, expected] of Object.entries(required)) {
      if (url.searchParams.getAll(name).length !== 1 || url.searchParams.get(name) !== expected) return null;
    }
    for (const name of ["state", "nonce", "code_challenge"]) {
      if (url.searchParams.getAll(name).length !== 1 || !/^[A-Za-z0-9_-]{43}$/.test(url.searchParams.get(name) ?? "")) return null;
    }
    if ([...url.searchParams.keys()].some(name => !Object.hasOwn(required, name)
      && !["state", "nonce", "code_challenge"].includes(name))) return null;
    return raw;
  } catch { return null; }
}

export function navigateBrowserLogin(destination: string) { window.location.assign(destination); }
