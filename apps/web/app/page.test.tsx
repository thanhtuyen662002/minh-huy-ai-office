import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import Home from "./page";

afterEach(() => {
  cleanup();
});

describe("Home", () => {
  it("renders the AI Office foundation screen with a main landmark and product heading", () => {
    render(<Home />);

    expect(screen.getByRole("main")).toBeTruthy();
    expect(
      screen.getByRole("heading", { level: 1, name: "AI Office" }),
    ).toBeTruthy();
    expect(
      screen.getByText(
        "Nền tảng agent đa công ty cho ERP, kế toán, hỗ trợ khách hàng và vận hành.",
      ),
    ).toBeTruthy();
    expect(screen.getByText("Foundation status")).toBeTruthy();
  });
});
