import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import Home from "./page";
vi.mock("../lib/browser-selected-company", () => ({ readSelectedBrowserCompany: vi.fn(async () => null) }));

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("local AI Office root workspace", () => {
  it("renders the browser-first AI Office shell while session state is checked", async () => {
    vi.stubGlobal("fetch", vi.fn(() => new Promise(() => undefined)));

    // Await the server page before mounting its client workspace boundary.
    render(await Home());

    expect(screen.getByRole("main")).toBeTruthy();
    expect(screen.getByText("MINH HUY AI OFFICE")).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Đang kiểm tra phiên đăng nhập…" })).toBeTruthy();
  });
});
