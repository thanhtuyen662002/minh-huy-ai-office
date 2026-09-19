import { CompanyShell } from "../components/company-shell";

const demoMembership = {
  tenantId: "minh-huy",
  companyId: "internal",
  companyName: "Minh Huy",
  userId: "demo-user",
  userName: "Nhân viên nội bộ",
  roles: ["Workspace member"],
} as const;

export default function Home() {
  return <CompanyShell membership={demoMembership} />;
}
