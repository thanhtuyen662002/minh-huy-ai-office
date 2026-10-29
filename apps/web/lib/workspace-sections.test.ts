import { expect, it } from "vitest";
import { workspaceSectionFromPath, workspaceSectionPath } from "./workspace-sections";

it("maps every main menu section to its own address and back", () => {
  expect(workspaceSectionPath("tong-quan")).toBe("/");
  expect(workspaceSectionPath("cong-viec")).toBe("/cong-viec");
  expect(workspaceSectionFromPath("/")).toBe("tong-quan");
  expect(workspaceSectionFromPath("/ket-noi/")).toBe("ket-noi");
  expect(workspaceSectionFromPath("/api")).toBeNull();
  expect(workspaceSectionFromPath("/__proto__")).toBeNull();
});
