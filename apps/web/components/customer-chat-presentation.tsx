type ChatAuthority = Readonly<{
  tenantId: string;
  companyId: string;
  userId: string;
  conversationId: string;
  authorityVersion: number;
}>;

type ChatMessage = Readonly<{
  messageId: string;
  tenantId: string;
  companyId: string;
  userId: string;
  conversationId: string;
  authorityVersion: number;
  occurredAt: string;
  role: "customer" | "assistant";
  text: string;
}>;

const canonical = (value: unknown): value is string =>
  typeof value === "string" && value.length > 0 && value.trim() === value;

const own = (value: object, key: PropertyKey): unknown => {
  try {
    const descriptor = Object.getOwnPropertyDescriptor(value, key);
    return descriptor && "value" in descriptor ? descriptor.value : undefined;
  } catch {
    return undefined;
  }
};

function readAuthority(value: unknown): ChatAuthority | null {
  if (value === null || typeof value !== "object") return null;
  const tenantId = own(value, "tenantId");
  const companyId = own(value, "companyId");
  const userId = own(value, "userId");
  const conversationId = own(value, "conversationId");
  const authorityVersion = own(value, "authorityVersion");
  if (!canonical(tenantId) || !canonical(companyId) || !canonical(userId) || !canonical(conversationId)) return null;
  if (!Number.isSafeInteger(authorityVersion) || (authorityVersion as number) <= 0) return null;
  return { tenantId, companyId, userId, conversationId, authorityVersion: authorityVersion as number };
}

function readMessages(value: unknown, authority: ChatAuthority): ChatMessage[] | null {
  if (!Array.isArray(value)) return null;
  const length = own(value, "length");
  if (!Number.isSafeInteger(length) || (length as number) < 0 || (length as number) > 500) return null;
  const messages: ChatMessage[] = [];
  const byId = new Map<string, string>();

  for (let index = 0; index < (length as number); index += 1) {
    const item = own(value, String(index));
    if (item === null || typeof item !== "object") return null;
    const messageId = own(item, "messageId");
    const tenantId = own(item, "tenantId");
    const companyId = own(item, "companyId");
    const userId = own(item, "userId");
    const conversationId = own(item, "conversationId");
    const authorityVersion = own(item, "authorityVersion");
    const occurredAt = own(item, "occurredAt");
    const role = own(item, "role");
    const text = own(item, "text");
    if (!canonical(messageId) || !canonical(occurredAt) || !canonical(text)) return null;
    if (role !== "customer" && role !== "assistant") return null;
    if (tenantId !== authority.tenantId || companyId !== authority.companyId || userId !== authority.userId || conversationId !== authority.conversationId || authorityVersion !== authority.authorityVersion) return null;
    if (Number.isNaN(Date.parse(occurredAt))) return null;
    const fingerprint = JSON.stringify([tenantId, companyId, userId, conversationId, authorityVersion, occurredAt, role, text]);
    const prior = byId.get(messageId);
    if (prior !== undefined) {
      if (prior !== fingerprint) return null;
      continue;
    }
    byId.set(messageId, fingerprint);
    messages.push({ messageId, tenantId, companyId, userId, conversationId, authorityVersion: authorityVersion as number, occurredAt, role, text });
  }

  return messages.sort((left, right) => left.occurredAt.localeCompare(right.occurredAt) || left.messageId.localeCompare(right.messageId));
}

export function CustomerChatPresentation({ authority: authorityInput, messages: messagesInput }: { authority: unknown; messages: unknown }) {
  const authority = readAuthority(authorityInput);
  const messages = authority ? readMessages(messagesInput, authority) : null;
  if (!authority || !messages) {
    return <section role="alert"><h2>Không thể mở cuộc trao đổi</h2><p>Dữ liệu hội thoại không khớp phạm vi đã được máy chủ xác thực.</p></section>;
  }
  if (messages.length === 0) {
    return <section aria-label="Cuộc trao đổi"><h2>Trao đổi</h2><p>Chưa có tin nhắn.</p></section>;
  }
  return <section aria-label="Cuộc trao đổi"><h2>Trao đổi</h2><ol>{messages.map((message) => <li key={message.messageId} data-role={message.role}><p>{message.text}</p></li>)}</ol></section>;
}
