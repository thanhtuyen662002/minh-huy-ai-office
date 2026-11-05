import { BotChannel, botChannel, botObject } from "./bot-channels";
export type BotMessageGroup = { channel: BotChannel; externalId: string; displayName: string; customerName: string;
  lastMessageAt: string | null; messageCount: number };
// imageCount = pictures the bot uploaded for this message (shown as thumbnails); older servers do not send it.
export type BotMessage = { messageId: string; senderName: string; text: string; hasImage: boolean; sentAt: string; imageCount: number };
// senders: everyone who has written in the group; used to mark "@Name" mentions by real names. Older servers omit it.
export type BotMessagePage = { items: BotMessage[]; hasMore: boolean; senders: string[] };
const line = (v: unknown, max: number): v is string => typeof v === "string" && !!v.trim() && v.length <= max && !/[\u0000-\u001f\u007f]/.test(v);
// Message text keeps line breaks and tabs; other control characters are refused.
const body = (v: unknown): v is string => typeof v === "string" && v.length <= 4000 && !/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/.test(v);
const date = (v: unknown): v is string => typeof v === "string" && v.length <= 40 && Number.isFinite(Date.parse(v));
export function parseBotMessageGroups(v: unknown): BotMessageGroup[] | null {
  if (!Array.isArray(v) || v.length > 200) return null;
  const groups = v.map(g => botObject(g) && botChannel(g.channel) && line(g.externalId, 200) && line(g.displayName, 200) && line(g.customerName, 200)
    && (g.lastMessageAt === null || date(g.lastMessageAt)) && Number.isSafeInteger(g.messageCount) && Number(g.messageCount) >= 0
    ? { channel: g.channel, externalId: g.externalId, displayName: g.displayName, customerName: g.customerName,
      lastMessageAt: g.lastMessageAt as string | null, messageCount: Number(g.messageCount) } : null);
  return groups.some(g => g === null) ? null : groups as BotMessageGroup[];
}
export function parseBotMessagePage(v: unknown, limit: number): BotMessagePage | null {
  if (!botObject(v) || typeof v.hasMore !== "boolean" || !Array.isArray(v.items) || v.items.length > limit) return null;
  const items = v.items.map(m => botObject(m) && line(m.messageId, 200) && line(m.senderName, 1000) && body(m.text) && typeof m.hasImage === "boolean"
    && (m.text !== "" || m.hasImage) && date(m.sentAt) && (m.imageCount === undefined || Number.isSafeInteger(m.imageCount) && Number(m.imageCount) >= 0 && Number(m.imageCount) <= 10)
    ? { messageId: m.messageId, senderName: m.senderName, text: m.text, hasImage: m.hasImage, sentAt: m.sentAt, imageCount: Number(m.imageCount ?? 0) } : null);
  const senders = v.senders ?? [];
  if (!Array.isArray(senders) || senders.length > 500 || !senders.every(x => line(x, 1000))) return null;
  return items.some(m => m === null) ? null : { items: items as BotMessage[], hasMore: v.hasMore, senders: senders as string[] };
}
