import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { CustomerChatPresentation } from "./customer-chat-presentation";

const authority = { tenantId: "tenant-1", companyId: "company-1", userId: "user-1", conversationId: "conversation-1", authorityVersion: 4 };
const message = (overrides: Record<string, unknown> = {}) => ({
  messageId: "message-1",
  ...authority,
  occurredAt: "2026-09-23T06:00:00Z",
  role: "customer",
  text: "Cần hỗ trợ đối chiếu công nợ",
  ...overrides,
});

describe("CustomerChatPresentation", () => {
  it("renders exact-authority messages in durable order without presentation identity fabrication", () => {
    render(<CustomerChatPresentation authority={authority} messages={[message({ messageId: "b", occurredAt: "2026-09-23T06:01:00Z", text: "Tin sau" }), message({ messageId: "a", text: "Tin trước" })]} />);
    const items = screen.getAllByRole("listitem");
    expect(items[0]).toHaveTextContent("Tin trước");
    expect(items[1]).toHaveTextContent("Tin sau");
    expect(screen.queryByText(/company-1|user-1/)).not.toBeInTheDocument();
  });

  it.each([
    ["tenantId", "tenant-2"],
    ["companyId", "company-2"],
    ["userId", "user-2"],
    ["conversationId", "conversation-2"],
    ["authorityVersion", 3],
  ])("fails closed when %s crosses authority", (key, value) => {
    render(<CustomerChatPresentation authority={authority} messages={[message({ [key]: value })]} />);
    expect(screen.getByRole("alert")).toHaveTextContent("không khớp phạm vi");
    expect(screen.queryByText("Cần hỗ trợ đối chiếu công nợ")).not.toBeInTheDocument();
  });

  it("deduplicates identical durable evidence but rejects conflicting duplicate ids", () => {
    const { rerender } = render(<CustomerChatPresentation authority={authority} messages={[message(), message()]} />);
    expect(screen.getAllByRole("listitem")).toHaveLength(1);
    rerender(<CustomerChatPresentation authority={authority} messages={[message(), message({ text: "Nội dung xung đột" })]} />);
    expect(screen.getByRole("alert")).toBeInTheDocument();
  });

  it("does not read provider diagnostics or browser-selected presentation metadata", () => {
    render(<CustomerChatPresentation authority={{ ...authority, companyName: "Tên từ trình duyệt", userName: "Người dùng giả" }} messages={[message({ provider: "provider-secret", model: "model-secret", workerId: "worker-secret" })]} />);
    expect(screen.getByText("Cần hỗ trợ đối chiếu công nợ")).toBeInTheDocument();
    expect(screen.queryByText(/Tên từ trình duyệt|Người dùng giả|provider-secret|model-secret|worker-secret/)).not.toBeInTheDocument();
  });

  it("shows an explicit empty state without inventing messages", () => {
    render(<CustomerChatPresentation authority={authority} messages={[]} />);
    expect(screen.getByText("Chưa có tin nhắn.")).toBeInTheDocument();
    expect(screen.queryByRole("listitem")).not.toBeInTheDocument();
  });
});
