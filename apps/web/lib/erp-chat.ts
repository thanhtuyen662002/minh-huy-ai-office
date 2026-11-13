/** Web ERP clients of the chat API (Kết nối → Chi phí AI → Web ERP kết nối). The signing key is shown once. */

export type ErpChatClient = { id: string; name: string; billingAccountId: string; isEnabled: boolean; version: string; createdAtUtc: string };
export type ErpChatClientSecret = { client: ErpChatClient; signingKey: string };

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === "object" && !Array.isArray(value);
const exact = (value: Record<string, unknown>, keys: string[]) => Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));

export function isErpChatClientCreate(value: unknown): value is { name: string; billingAccountId: string } {
  return record(value) && exact(value, ["name", "billingAccountId"]) && typeof value.name === "string" && value.name.trim().length > 0
    && value.name.length <= 200 && !/[\u0000-\u001f\u007f]/.test(value.name) && typeof value.billingAccountId === "string" && guid.test(value.billingAccountId);
}

export function isErpChatClientUpdate(value: unknown): value is { isEnabled: boolean; rotateKey: boolean; version: string } {
  return record(value) && exact(value, ["isEnabled", "rotateKey", "version"]) && typeof value.isEnabled === "boolean"
    && typeof value.rotateKey === "boolean" && typeof value.version === "string" && /^[1-9][0-9]{0,18}$/.test(value.version);
}

function client(value: unknown): ErpChatClient | null {
  return record(value) && typeof value.id === "string" && guid.test(value.id) && typeof value.name === "string" && value.name.length > 0
    && value.name.length <= 200 && typeof value.billingAccountId === "string" && guid.test(value.billingAccountId)
    && typeof value.isEnabled === "boolean" && typeof value.version === "string" && typeof value.createdAtUtc === "string"
    ? { id: value.id, name: value.name, billingAccountId: value.billingAccountId, isEnabled: value.isEnabled, version: value.version,
      createdAtUtc: value.createdAtUtc } : null;
}

export function parseErpChatClients(value: unknown): ErpChatClient[] | null {
  if (!Array.isArray(value) || value.length > 200) return null;
  const rows = value.map(client);
  return rows.every(row => row !== null) ? rows as ErpChatClient[] : null;
}

export function parseErpChatSecret(value: unknown): ErpChatClientSecret | null {
  if (!record(value) || typeof value.signingKey !== "string" || !/^[A-Za-z0-9+/]{43}=$/.test(value.signingKey)) return null;
  const parsed = client(value.client);
  return parsed ? { client: parsed, signingKey: value.signingKey } : null;
}
