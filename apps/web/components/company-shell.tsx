import type { CompanyMembershipView } from "../lib/company-context";
import { resolveCompanyContext } from "../lib/company-context";

type CompanyShellProps = {
  membership: CompanyMembershipView | null;
};

export function CompanyShell({ membership }: CompanyShellProps) {
  const context = resolveCompanyContext(membership);

  if (context.status === "unavailable") {
    return (
      <main className="mx-auto flex min-h-screen max-w-3xl items-center px-6 py-16">
        <section aria-labelledby="scope-error" className="w-full rounded-2xl border border-black/10 p-6 dark:border-white/15">
          <p className="text-sm font-medium uppercase tracking-[0.18em] opacity-60">Minh Huy AI Office</p>
          <h1 id="scope-error" className="mt-3 text-3xl font-semibold tracking-tight">Chưa có phạm vi làm việc</h1>
          <p className="mt-3 max-w-xl leading-7 opacity-75">{context.reason}</p>
          <p className="mt-5 text-sm opacity-60">Hệ thống sẽ không hiển thị dữ liệu công ty cho đến khi phạm vi truy cập được xác thực.</p>
        </section>
      </main>
    );
  }

  return (
    <main className="mx-auto min-h-screen max-w-6xl px-6 py-8 lg:px-10">
      <header className="flex flex-col gap-5 border-b border-black/10 pb-6 dark:border-white/15 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <p className="text-sm font-medium uppercase tracking-[0.18em] opacity-60">Minh Huy</p>
          <h1 className="mt-2 text-4xl font-semibold tracking-tight">AI Office</h1>
        </div>
        <section aria-label="Công ty đang làm việc" className="rounded-xl border border-black/10 px-4 py-3 dark:border-white/15">
          <p className="text-xs uppercase tracking-[0.14em] opacity-55">Công ty đang làm việc</p>
          <p className="mt-1 font-semibold">{context.companyName}</p>
          <p className="mt-1 text-sm opacity-65">{context.userName} · {context.roles.length > 0 ? context.roles.join(", ") : "Chưa gán vai trò"}</p>
        </section>
      </header>

      <div className="grid gap-6 py-8 lg:grid-cols-[220px_1fr]">
        <nav aria-label="Điều hướng chính" className="rounded-2xl border border-black/10 p-4 dark:border-white/15">
          <p className="px-2 text-xs font-medium uppercase tracking-[0.14em] opacity-50">Không gian làm việc</p>
          <ul className="mt-3 grid gap-1 text-sm">
            <li className="rounded-lg bg-black/5 px-3 py-2 font-medium dark:bg-white/10">Tổng quan</li>
            <li className="px-3 py-2 opacity-60">Công việc</li>
            <li className="px-3 py-2 opacity-60">Nguồn dữ liệu</li>
          </ul>
        </nav>

        <section aria-labelledby="workspace-heading" className="rounded-2xl border border-black/10 p-6 dark:border-white/15">
          <p className="text-sm font-medium opacity-60">Phạm vi đã xác định</p>
          <h2 id="workspace-heading" className="mt-2 text-2xl font-semibold tracking-tight">Không gian làm việc của {context.companyName}</h2>
          <p className="mt-3 max-w-2xl leading-7 opacity-70">Mọi tác vụ và dữ liệu hiển thị trong khu vực này phải giữ nguyên phạm vi tenant, công ty và người dùng đã được xác thực.</p>
          <dl className="mt-6 grid gap-3 text-sm sm:grid-cols-3">
            <div className="rounded-xl bg-black/[0.035] p-4 dark:bg-white/[0.06]"><dt className="opacity-55">Tenant</dt><dd className="mt-1 font-medium">{context.tenantId}</dd></div>
            <div className="rounded-xl bg-black/[0.035] p-4 dark:bg-white/[0.06]"><dt className="opacity-55">Company</dt><dd className="mt-1 font-medium">{context.companyId}</dd></div>
            <div className="rounded-xl bg-black/[0.035] p-4 dark:bg-white/[0.06]"><dt className="opacity-55">User</dt><dd className="mt-1 font-medium">{context.userId}</dd></div>
          </dl>
        </section>
      </div>
    </main>
  );
}
