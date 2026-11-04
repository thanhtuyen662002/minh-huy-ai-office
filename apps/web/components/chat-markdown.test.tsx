import { render, screen } from "@testing-library/react";
import { expect, it } from "vitest";
import { ChatMarkdown } from "./chat-markdown";

it("renders the assistant's Markdown as formatted text without raw markers", () => {
  const { container } = render(<ChatMarkdown text={[
    "**Kết luận ngắn:** chưa xác định được, xem `CF_ObjectTbl`.", "", "## Các bước", "1. **Không chạy** thêm", "2. Nhờ DBA", "   - cấp quyền *SELECT*", "",
    "| Bảng | Số dòng |", "|---|---:|", "| A | 3 |", "", "```sql", "SELECT 1 <b>x</b>", "```", "> Lưu ý"].join("\n")} />);
  expect(container.textContent).not.toContain("**");
  expect(screen.getByText("Kết luận ngắn:").tagName).toBe("STRONG");
  expect(screen.getByText("CF_ObjectTbl").tagName).toBe("CODE");
  expect(screen.getByRole("heading", { name: "Các bước" })).toBeTruthy();
  expect(container.querySelectorAll("ol > li").length).toBeGreaterThanOrEqual(2);
  expect(screen.getByText("SELECT").tagName).toBe("EM");
  expect(screen.getByRole("table")).toBeTruthy();
  expect(container.querySelector("pre")?.textContent).toContain("SELECT 1 <b>x</b>");
  expect(container.querySelector("b")).toBeNull();
  expect(container.querySelector("blockquote")?.textContent).toBe("Lưu ý");
});

it("renders tables without outer pipes, short dividers, escaped pipes and ragged rows", () => {
  const { container } = render(<ChatMarkdown text={[
    "Kết quả:", "Mã | Tên | Tỉnh", "-|:-:|-", "KH01 | A \\| B | Hà Nội", "KH02 | C", "", "| X | Y |", "|-|-|", "| 1 | 2 |"].join("\n")} />);
  const tables = container.querySelectorAll("table");
  expect(tables.length).toBe(2);
  expect(tables[0].querySelectorAll("th").length).toBe(3);
  expect(tables[0].querySelectorAll("tbody tr").length).toBe(2);
  expect(tables[0].querySelectorAll("tbody tr")[1].querySelectorAll("td").length).toBe(3);
  expect(tables[0].textContent).toContain("A | B");
  expect(container.textContent).not.toContain("-|:-:|-");
  expect(container.querySelector("p")?.textContent).toBe("Kết quả:");
});
