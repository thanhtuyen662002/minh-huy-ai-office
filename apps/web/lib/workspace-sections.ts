// Main menu of the signed-in workspace. Each section has its own address so a
// link can be shared and a reload stays on the same screen.
export const workspaceSections = {
  "tong-quan": "Tổng quan",
  "tin-nhan": "Tin nhắn",
  "cong-viec": "Công việc",
  "tro-ly": "Trợ lý",
  "kien-thuc": "Kiến thức",
  "ket-noi": "Kết nối",
} as const;
export type WorkspaceSection = keyof typeof workspaceSections;
export const defaultWorkspaceSection: WorkspaceSection = "tong-quan";

export const isWorkspaceSection = (value: unknown): value is WorkspaceSection =>
  typeof value === "string" && Object.hasOwn(workspaceSections, value);

export const workspaceSectionPath = (section: WorkspaceSection) => section === defaultWorkspaceSection ? "/" : `/${section}`;

export function workspaceSectionFromPath(pathname: string): WorkspaceSection | null {
  const slug = pathname.replace(/^\/+|\/+$/g, "");
  if (!slug) return defaultWorkspaceSection;
  return isWorkspaceSection(slug) ? slug : null;
}
