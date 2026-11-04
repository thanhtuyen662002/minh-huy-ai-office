import { Fragment, type ReactNode } from "react";

/**
 * Small, safe Markdown renderer for Trợ lý answers: headings, bold/italic, inline code, code blocks,
 * lists, tables, quotes and rules. Everything is rendered as React text nodes (no HTML injection).
 */
export function ChatMarkdown({ text }: { text: string }) {
  return <div className="chat-markdown space-y-3 text-sm leading-6">{blocks(text.replace(/\r\n?/g, "\n"))}</div>;
}

type Block = { kind: "code"; lang: string; lines: string[] } | { kind: "table"; rows: string[][] } | { kind: "list"; ordered: boolean; items: { depth: number; text: string; ordered: boolean }[] }
  | { kind: "heading"; level: number; text: string } | { kind: "quote"; lines: string[] } | { kind: "rule" } | { kind: "para"; lines: string[] };

const listItem = /^(\s*)([-*+]|\d+[.)])\s+(.*)$/;
const tableRow = /^\s*\|.*\|\s*$/;
const tableDivider = /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/;

function parse(source: string): Block[] {
  const lines = source.split("\n"), out: Block[] = [];
  for (let index = 0; index < lines.length;) {
    const line = lines[index];
    const fence = line.match(/^\s*```\s*([\w-]*)/);
    if (fence) {
      const body: string[] = []; index++;
      while (index < lines.length && !/^\s*```/.test(lines[index])) body.push(lines[index++]);
      index++; out.push({ kind: "code", lang: fence[1], lines: body }); continue;
    }
    if (!line.trim()) { index++; continue; }
    const heading = line.match(/^\s*(#{1,6})\s+(.*)$/);
    if (heading) { out.push({ kind: "heading", level: heading[1].length, text: heading[2].replace(/\s#+\s*$/, "") }); index++; continue; }
    if (/^\s*([-*_])(\s*\1){2,}\s*$/.test(line)) { out.push({ kind: "rule" }); index++; continue; }
    if (tableRow.test(line) && index + 1 < lines.length && tableDivider.test(lines[index + 1])) {
      const rows: string[][] = [cells(line)]; index += 2;
      while (index < lines.length && tableRow.test(lines[index])) rows.push(cells(lines[index++]));
      out.push({ kind: "table", rows }); continue;
    }
    if (/^\s*>/.test(line)) {
      const body: string[] = [];
      while (index < lines.length && /^\s*>/.test(lines[index])) body.push(lines[index++].replace(/^\s*>\s?/, ""));
      out.push({ kind: "quote", lines: body }); continue;
    }
    const first = line.match(listItem);
    if (first) {
      const items: { depth: number; text: string; ordered: boolean }[] = [];
      while (index < lines.length) {
        const match = lines[index].match(listItem);
        if (match) { items.push({ depth: Math.floor(match[1].replace(/\t/g, "  ").length / 2), text: match[3], ordered: /\d/.test(match[2]) }); index++; continue; }
        // A wrapped continuation line belongs to the previous item.
        if (lines[index].trim() && /^\s+/.test(lines[index]) && items.length) { items[items.length - 1].text += " " + lines[index].trim(); index++; continue; }
        break;
      }
      out.push({ kind: "list", ordered: /\d/.test(first[2]), items }); continue;
    }
    const body: string[] = [];
    while (index < lines.length && lines[index].trim() && !/^\s*(```|#{1,6}\s|>)/.test(lines[index]) && !listItem.test(lines[index])
      && !(tableRow.test(lines[index]) && index + 1 < lines.length && tableDivider.test(lines[index + 1]))) body.push(lines[index++]);
    out.push({ kind: "para", lines: body });
  }
  return out;
}

function cells(line: string) {
  return line.trim().replace(/^\|/, "").replace(/\|$/, "").split("|").map(cell => cell.trim());
}

function blocks(source: string): ReactNode[] {
  return parse(source).map((block, key) => {
    switch (block.kind) {
      case "code":
        return <pre key={key} className="overflow-x-auto rounded-lg border bg-slate-950 p-3 font-mono text-xs leading-5 text-slate-100 dark:bg-black/40">
          {block.lang ? <span className="mb-1 block text-[10px] uppercase tracking-wider text-slate-400">{block.lang}</span> : null}
          <code>{block.lines.join("\n")}</code></pre>;
      case "heading": {
        const size = block.level <= 1 ? "text-lg" : block.level === 2 ? "text-base" : "text-sm";
        return <p key={key} role="heading" aria-level={Math.min(block.level + 2, 6)} className={`${size} mt-1 font-semibold text-foreground`}>{inline(block.text)}</p>;
      }
      case "rule": return <hr key={key} className="border-border" />;
      case "quote":
        return <blockquote key={key} className="rounded-r-lg border-l-4 border-sky-400 bg-sky-50 px-3 py-2 text-slate-700 dark:bg-sky-400/10 dark:text-slate-200">
          {block.lines.map((line, index) => <Fragment key={index}>{index ? <br /> : null}{inline(line)}</Fragment>)}</blockquote>;
      case "table":
        return <div key={key} className="overflow-x-auto rounded-lg border"><table className="w-full text-left text-xs">
          <thead className="bg-muted"><tr>{block.rows[0].map((cell, index) => <th key={index} className="px-3 py-2 font-semibold">{inline(cell)}</th>)}</tr></thead>
          <tbody>{block.rows.slice(1).map((row, rowIndex) => <tr key={rowIndex} className="border-t">
            {row.map((cell, index) => <td key={index} className="px-3 py-2 align-top">{inline(cell)}</td>)}</tr>)}</tbody></table></div>;
      case "list": return <Fragment key={key}>{list(block.items, 0, 0).node}</Fragment>;
      default:
        return <p key={key}>{block.lines.map((line, index) => <Fragment key={index}>{index ? <br /> : null}{inline(line.trim())}</Fragment>)}</p>;
    }
  });
}

function list(items: { depth: number; text: string; ordered: boolean }[], start: number, depth: number): { node: ReactNode; next: number } {
  const children: ReactNode[] = []; let index = start;
  const ordered = items[start]?.ordered ?? false;
  while (index < items.length && items[index].depth >= depth) {
    if (items[index].depth > depth) {
      const nested = list(items, index, items[index].depth);
      children.push(<li key={`n${index}`} className="list-none">{nested.node}</li>); index = nested.next; continue;
    }
    children.push(<li key={index} className="pl-1">{inline(items[index].text)}</li>); index++;
  }
  const className = `${ordered ? "list-decimal" : "list-disc"} space-y-1 pl-5 marker:text-sky-600 dark:marker:text-sky-400`;
  return { node: ordered ? <ol className={className}>{children}</ol> : <ul className={className}>{children}</ul>, next: index };
}

/** Inline marks: `code`, **bold**, __bold__, *italic*, _italic_, ~~strike~~ and [text](url) shown as text. */
export function inline(text: string): ReactNode[] {
  const out: ReactNode[] = [];
  const pattern = /(`[^`]+`)|(\*\*[^*]+?\*\*|__[^_]+?__)|(~~[^~]+?~~)|(\*[^*\s][^*]*?\*|\b_[^_\s][^_]*?_\b)|(\[[^\]]+\]\([^)]+\))/g;
  let last = 0, match: RegExpExecArray | null, key = 0;
  while ((match = pattern.exec(text))) {
    if (match.index > last) out.push(text.slice(last, match.index));
    const token = match[0];
    if (match[1]) out.push(<code key={key++} className="rounded bg-sky-50 px-1 py-0.5 font-mono text-[0.85em] text-sky-800 dark:bg-sky-400/10 dark:text-sky-200">{token.slice(1, -1)}</code>);
    else if (match[2]) out.push(<strong key={key++} className="font-semibold text-foreground">{inline(token.slice(2, -2))}</strong>);
    else if (match[3]) out.push(<s key={key++}>{inline(token.slice(2, -2))}</s>);
    else if (match[4]) out.push(<em key={key++}>{inline(token.slice(1, -1))}</em>);
    else { const label = token.slice(1, token.indexOf("]")); out.push(<span key={key++} className="text-sky-700 underline dark:text-sky-300">{label}</span>); }
    last = match.index + token.length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}
