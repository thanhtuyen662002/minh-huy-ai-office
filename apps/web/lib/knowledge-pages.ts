import { isCanonicalCompanyId } from "./company-scope";

export type KnowledgeSummary = { id: string; customerName: string | null; title: string; updatedBy: string; updatedAt: string };
export type KnowledgeList = { items: KnowledgeSummary[]; hasMore: boolean };
export type KnowledgePage = { id: string; customerName: string | null; title: string; body: string; createdBy: string; updatedBy: string;
  createdAt: string; updatedAt: string; rowVersion: string };
export type KnowledgeInput = { title: string; body: string; customerName: string | null; rowVersion?: string };
export const knowledgeLimits = { name: 200, body: 500_000, pages: 500 } as const;

// Uploaded files are read into text on the server; long text comes back split into page-sized parts.
export type KnowledgeExtract = { title: string; parts: string[]; truncated: boolean };
export const knowledgeFileLimits = { bytes: 20 * 1024 * 1024, name: 255, parts: 4 } as const;
export const knowledgeFileAccept = ".pdf,.docx,.xlsx,.pptx,.txt,.md,.markdown,.csv,.tsv";
export const knowledgeFileReasons: Record<string, string> = {
  legacy: "File Word/Excel đời cũ (.doc, .xls, .ppt) chưa đọc được. Hãy mở file và Lưu thành .docx, .xlsx hoặc .pptx rồi tải lại.",
  unsupported: "Chỉ đọc được file PDF, Word (.docx), Excel (.xlsx), PowerPoint (.pptx), .txt, .md và .csv.",
  empty: "File không có chữ đọc được. PDF dạng ảnh scan cần được nhận dạng chữ (OCR) trước khi tải lên.",
  encrypted: "File đang đặt mật khẩu. Hãy bỏ mật khẩu rồi tải lại.",
  damaged: "File bị lỗi hoặc không đúng định dạng nên không đọc được.",
  tooLarge: "File quá lớn. Mỗi file tối đa 20 MB.",
};
export const knowledgeFileName = (value: unknown): value is string => typeof value === "string" && value.trim().length > 0
  && value.length <= knowledgeFileLimits.name && ![...value].some(char => char < " " || char === "\u007f") && !lone.test(value);
export function parseKnowledgeExtract(value: unknown): KnowledgeExtract | null {
  if (!object(value) || !keys(value, ["title", "parts", "truncated"]) || !knowledgeName(value.title) || typeof value.truncated !== "boolean"
    || !Array.isArray(value.parts) || value.parts.length < 1 || value.parts.length > knowledgeFileLimits.parts
    || !value.parts.every(part => knowledgeBody(part) && part.length > 0)) return null;
  return { title: value.title, parts: [...value.parts] as string[], truncated: value.truncated };
}
/** "Tên (phần 2/3)" kept inside the title limit. */
export function knowledgePartTitle(title: string, index: number, count: number): string {
  if (count <= 1) return title;
  const suffix = ` (phần ${index + 1}/${count})`;
  const base = title.slice(0, knowledgeLimits.name - suffix.length).replace(/[\uD800-\uDBFF]$/, "").trimEnd();
  return `${base}${suffix}`;
}

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
