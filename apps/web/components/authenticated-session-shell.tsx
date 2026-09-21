"use client";

import type { AuthenticatedSessionState } from "../lib/authenticated-session";
import { CompanyShell } from "./company-shell";

type CompanyOption = { companyId: string; companyName: string };

type AuthenticatedSessionShellProps = {
  state: AuthenticatedSessionState;
  companies?: readonly CompanyOption[];
  onSelectCompany?: (companyId: string) => void;
};

const failureCopy = {
  forbidden: "Bạn không có quyền truy cập công ty đã chọn.",
  "inactive-membership": "Quyền thành viên của bạn tại công ty này không còn hoạt động.",
  "invalid-response": "Không thể xác thực phạm vi công ty. Dữ liệu sẽ không được hiển thị.",
} as const;

const getOwnDataProperty = (value: object, key: PropertyKey) => {
  try {
    const descriptor = Object.getOwnPropertyDescriptor(value, key);
    return descriptor && "value" in descriptor ? descriptor.value : undefined;
  } catch {
    return undefined;
  }
};

const normalizeCompanyOption = (company: CompanyOption): CompanyOption | null => {
  if (company === null || typeof company !== "object") return null;
  const companyId = getOwnDataProperty(company, "companyId");
  const companyName = getOwnDataProperty(company, "companyName");
  if (
    typeof companyId !== "string" ||
    typeof companyName !== "string" ||
    companyId.length === 0 ||
    companyId.trim() !== companyId ||
    companyName.length === 0 ||
    companyName.trim() !== companyName
  ) return null;
  return { companyId, companyName };
};

const getUnambiguousCompanyOptions = (companies: readonly CompanyOption[]) => {
  try {
    if (!Array.isArray(companies)) return [];
    const length = getOwnDataProperty(companies, "length");
    if (!Number.isSafeInteger(length) || (length as number) < 0) return [];
    const normalized: CompanyOption[] = [];
    const seen = new Set<string>();
    for (let index = 0; index < (length as number); index += 1) {
      const company = getOwnDataProperty(companies, String(index));
      if (company === undefined) return [];
      const option = normalizeCompanyOption(company as CompanyOption);
      if (!option || seen.has(option.companyId)) return [];
      seen.add(option.companyId);
      normalized.push(option);
    }
    return normalized;
  } catch {
    return [];
  }
};

const readReadyCompanyId = (state: Extract<AuthenticatedSessionState, { status: "ready" }>) => {
  try {
    const membership = getOwnDataProperty(state, "membership");
    if (membership === null || typeof membership !== "object") return null;
    const companyId = getOwnDataProperty(membership, "companyId");
    return typeof companyId === "string" && companyId.length > 0 && companyId.trim() === companyId ? companyId : null;
  } catch {
    return null;
  }
};

export function AuthenticatedSessionShell({ state, companies = [], onSelectCompany }: AuthenticatedSessionShellProps) {
  if (state.status === "loading") {
    return <main aria-busy="true" className="mx-auto flex min-h-screen max-w-3xl items-center px-6 py-16"><section role="status" aria-live="polite" aria-labelledby="session-loading" className="w-full rounded-2xl border border-black/10 p-6 dark:border-white/15"><p className="text-sm font-medium uppercase tracking-[0.18em] opacity-60">Minh Huy AI Office</p><h1 id="session-loading" className="mt-3 text-3xl font-semibold tracking-tight">Đang xác thực phiên làm việc</h1><p className="mt-3 leading-7 opacity-75">Đang kiểm tra quyền truy cập công ty trước khi hiển thị dữ liệu.</p></section></main>;
  }

  if (state.status === "unauthenticated") {
    return <main className="mx-auto flex min-h-screen max-w-3xl items-center px-6 py-16"><section role="alert" aria-labelledby="session-required" className="w-full rounded-2xl border border-black/10 p-6 dark:border-white/15"><p className="text-sm font-medium uppercase tracking-[0.18em] opacity-60">Minh Huy AI Office</p><h1 id="session-required" className="mt-3 text-3xl font-semibold tracking-tight">Cần đăng nhập</h1><p className="mt-3 leading-7 opacity-75">Phiên đăng nhập chưa được xác thực. Không có dữ liệu công ty nào được hiển thị.</p></section></main>;
  }

  if (state.status === "forbidden") {
    return <main className="mx-auto flex min-h-screen max-w-3xl items-center px-6 py-16"><section role="alert" aria-labelledby="scope-denied" className="w-full rounded-2xl border border-black/10 p-6 dark:border-white/15"><p className="text-sm font-medium uppercase tracking-[0.18em] opacity-60">Minh Huy AI Office</p><h1 id="scope-denied" className="mt-3 text-3xl font-semibold tracking-tight">Không thể mở phạm vi công ty</h1><p className="mt-3 leading-7 opacity-75">{failureCopy[state.reason]}</p></section></main>;
  }

  const authoritativeCompanyId = readReadyCompanyId(state);
  if (!authoritativeCompanyId) {
    return <main className="mx-auto flex min-h-screen max-w-3xl items-center px-6 py-16"><section role="alert" aria-labelledby="scope-invalid" className="w-full rounded-2xl border border-black/10 p-6 dark:border-white/15"><p className="text-sm font-medium uppercase tracking-[0.18em] opacity-60">Minh Huy AI Office</p><h1 id="scope-invalid" className="mt-3 text-3xl font-semibold tracking-tight">Không thể mở phạm vi công ty</h1><p className="mt-3 leading-7 opacity-75">{failureCopy["invalid-response"]}</p></section></main>;
  }

  const switchableCompanies = getUnambiguousCompanyOptions(companies);
  const hasAuthoritativeOption = switchableCompanies.some((company) => company.companyId === authoritativeCompanyId);
  const hasAlternativeScope = switchableCompanies.some((company) => company.companyId !== authoritativeCompanyId);
  const canSwitchCompany = hasAuthoritativeOption && hasAlternativeScope && typeof onSelectCompany === "function";
  const requestCompanySwitch = (companyId: string) => {
    if (companyId === authoritativeCompanyId) return;
    if (!switchableCompanies.some((company) => company.companyId === companyId)) return;
    if (typeof onSelectCompany !== "function") return;
    onSelectCompany(companyId);
  };

  return <><CompanyShell membership={state.membership} />{canSwitchCompany ? <aside aria-label="Đổi công ty" className="fixed bottom-4 right-4 rounded-xl border border-black/10 bg-white p-3 shadow-sm dark:border-white/15 dark:bg-black"><label className="text-xs font-medium" htmlFor="company-selector">Đổi công ty</label><select id="company-selector" className="ml-2 rounded-lg border border-black/15 bg-transparent px-2 py-1 text-sm dark:border-white/20" value={authoritativeCompanyId} onChange={(event) => requestCompanySwitch(event.target.value)}>{switchableCompanies.map((company) => <option key={company.companyId} value={company.companyId}>{company.companyName}</option>)}</select><p className="mt-1 max-w-xs text-xs opacity-60">Lựa chọn này chỉ yêu cầu đổi phạm vi; máy chủ vẫn xác thực quyền truy cập.</p></aside> : null}</>;
}
