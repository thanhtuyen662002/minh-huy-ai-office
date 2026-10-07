import { createHash } from "node:crypto";
import { readBrowserOidcSettings } from "./browser-oidc";
import { readBrowserCoreApiOrigin } from "./browser-oidc-authority";
import { createBrowserSessionCoordinator, createBrowserSessionRedis, readBrowserSessionRedisUrl } from "./browser-session-store";

type Runtime = Readonly<{
  settings: NonNullable<ReturnType<typeof readBrowserOidcSettings>>;
  coreApiOrigin: string;
  sessions: ReturnType<typeof createBrowserSessionCoordinator>;
}>;
let cached: { fingerprint: string; runtime: Runtime; close: () => void } | null = null;

// Only connection/configuration is cached. Every session/binding operation
// still crosses the shared Redis authority; no auth result is cached locally.
export function getBrowserAuthRuntime(): Runtime | null {
  try {
    const settings = readBrowserOidcSettings();
    if (!settings) { cached?.close(); cached = null; return null; }
    // Validate the entire enabled boundary BEFORE a provider redirect or store
    // connection; there are no default production provider/API/cache endpoints.
    const coreApiOrigin = readBrowserCoreApiOrigin(settings);
    const redisUrl = readBrowserSessionRedisUrl(settings);
    const fingerprint = createHash("sha256").update(JSON.stringify([settings, coreApiOrigin, redisUrl])).digest("hex");
    if (cached?.fingerprint === fingerprint) return cached.runtime;
    cached?.close(); cached = null;
    const transport = createBrowserSessionRedis(settings);
    const runtime = Object.freeze({ settings, coreApiOrigin, sessions: createBrowserSessionCoordinator(transport.evaluate, settings) });
    cached = { fingerprint, runtime, close: transport.close };
    return runtime;
  } catch {
    cached?.close(); cached = null;
    throw new Error("Browser sign-in is unavailable.");
  }
}
