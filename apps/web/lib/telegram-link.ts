// Telegram link of the signed-in member, as Core.Api returns it. Unknown fields are dropped.
export type TelegramLink = { linked: boolean; telegramName: string | null; linkedAt: string | null; botUsername: string | null };
export type TelegramLinkCode = { code: string; url: string | null; expiresAt: string };

const record = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null && !Array.isArray(value);
const text = (value: unknown, max = 200): value is string => typeof value === "string" && value.length > 0 && value.length <= max;
const optional = (value: unknown, max = 200) => value === null || text(value, max);
const time = (value: unknown): value is string => text(value, 64) && !Number.isNaN(Date.parse(value));

export function parseTelegramLink(value: unknown): TelegramLink | null {
  if (!record(value) || typeof value.linked !== "boolean" || !optional(value.telegramName) || !(value.linkedAt === null || time(value.linkedAt))
    || !optional(value.botUsername, 40)) return null;
  return { linked: value.linked, telegramName: value.telegramName as string | null, linkedAt: value.linkedAt as string | null,
    botUsername: value.botUsername as string | null };
}

export function parseTelegramLinkCode(value: unknown): TelegramLinkCode | null {
  if (!record(value) || !text(value.code, 22) || !/^[A-Za-z0-9_-]{22}$/.test(value.code) || !time(value.expiresAt)) return null;
  const url = value.url;
  if (url !== null && !(text(url, 120) && /^https:\/\/t\.me\/[A-Za-z0-9_]{5,32}\?start=[A-Za-z0-9_-]{22}$/.test(url))) return null;
  return { code: value.code, url, expiresAt: value.expiresAt };
}
