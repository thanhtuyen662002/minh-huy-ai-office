import { isCanonicalCompanyId } from "./company-scope";

export type KnowledgeSummary = { id: string; customerName: string | null; title: string; updatedBy: string; updatedAt: string };
export type KnowledgeList = { items: KnowledgeSummary[]; hasMore: boolean };
export type KnowledgePage = { id: string; customerName: string | null; title: string; body: string; createdBy: string; updatedBy: string;
  createdAt: string; updatedAt: string; rowVersion: string };
export type KnowledgeInput = { title: string; body: string; customerName: string | null; rowVersion?: string };
export const knowledgeLimits = { name: 200, body: 20000, pages: 500 } as const;

const object = (value: unknown): value is Record<string, unknown> => !!value && typeof value === "object" && !Array.isArray(value);
const keys = (value: Record<string, unknown>, allowed: readonly string[]) => Object.keys(value).length === allowed.length
  && Object.keys(value).every(key => allowed.includes(key));
const lone = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u;
export const knowledgeName = (value: unknown): value is string => typeof value === "string" && value.trim().length > 0
  && value.length <= knowledgeLimits.name && !value.includes("\0") && !lone.test(value);
export const knowledgeBody = (value: unknown): value is string => typeof value === "string" && value.length <= knowledgeLimits.body
  && !value.includes("\0") && !lone.test(value);
const date = (value: unknown): value is string => typeof value === "string" && value.length <= 40 && Number.isFinite(Date.parse(value));
export const knowledgeVersion = (value: unknown): value is string => typeof value === "string" && /^[A-Za-z0-9+/]{10}[AEIMQUYcgkosw048]=$/.test(value);
const customer = (value: unknown) => value === null || knowledgeName(value);

export function parseKnowledgeList(value: unknown): KnowledgeList | null {
  if (!object(value) || typeof value.hasMore !== "boolean" || !Array.isArray(value.items)
    || value.items.length > knowledgeLimits.pages) return null;
  const items: KnowledgeSummary[] = [];
  for (const item of value.items) {
    if (!object(item) || !isCanonicalCompanyId(item.id)
      || !customer(item.customerName) || !knowledgeName(item.title) || !isCanonicalCompanyId(item.updatedBy) || !date(item.updatedAt)
      || items.some(old => old.id === item.id)) return null;
    items.push({ id: item.id, customerName: item.customerName as string | null, title: item.title, updatedBy: item.updatedBy, updatedAt: item.updatedAt });
  }
  return { items, hasMore: value.hasMore };
}

export function parseKnowledgePage(value: unknown, id?: string): KnowledgePage | null {
  if (!object(value) || !isCanonicalCompanyId(value.id) || id !== undefined && value.id.toLowerCase() !== id.toLowerCase() || !customer(value.customerName)
    || !knowledgeName(value.title) || !knowledgeBody(value.body) || !isCanonicalCompanyId(value.createdBy) || !isCanonicalCompanyId(value.updatedBy)
    || !date(value.createdAt) || !date(value.updatedAt) || !knowledgeVersion(value.rowVersion)) return null;
  return { id: value.id, customerName: value.customerName as string | null, title: value.title, body: value.body, createdBy: value.createdBy,
    updatedBy: value.updatedBy, createdAt: value.createdAt, updatedAt: value.updatedAt, rowVersion: value.rowVersion };
}

export function isKnowledgeInput(value: unknown, update: boolean): value is KnowledgeInput {
  if (!object(value)) return false;
  const allowed = update ? ["title", "body", "customerName", "rowVersion"] : ["title", "body", "customerName"];
  return keys(value, allowed) && knowledgeName(value.title) && knowledgeBody(value.body) && customer(value.customerName)
    && (!update || knowledgeVersion(value.rowVersion));
}

// The page body is plain text with a few line markers, like a small Notion/AppFlowy page.
export type KnowledgeBlock = { kind: "heading"; text: string } | { kind: "todo"; text: string; done: boolean; line: number }
  | { kind: "bullet"; text: string } | { kind: "callout"; text: string } | { kind: "text"; text: string };
export function knowledgeBlocks(body: string): KnowledgeBlock[] {
  return body.split("\n").flatMap((line, index): KnowledgeBlock[] => {
    const todo = /^- \[( |x|X)\] (.*)$/.exec(line);
    if (todo) return [{ kind: "todo", text: todo[2], done: todo[1] !== " ", line: index }];
    if (line.startsWith("## ")) return [{ kind: "heading", text: line.slice(3) }];
    if (line.startsWith("# ")) return [{ kind: "heading", text: line.slice(2) }];
    if (line.startsWith("> ")) return [{ kind: "callout", text: line.slice(2) }];
    if (line.startsWith("- ")) return [{ kind: "bullet", text: line.slice(2) }];
    return line.trim() ? [{ kind: "text", text: line }] : [];
  });
}
export function toggleKnowledgeTodo(body: string, line: number): string {
  const lines = body.split("\n"), match = /^- \[( |x|X)\] /.exec(lines[line] ?? "");
  if (!match) return body;
  lines[line] = `- [${match[1] === " " ? "x" : " "}] ${lines[line].slice(6)}`;
  return lines.join("\n");
}
export const knowledgeTicketCodes = (body: string) => [...new Set(body.match(/\bYC-[0-9]{4,21}\b/g) ?? [])].slice(0, 20);
