import type { AuthenticatedSessionState } from "../lib/authenticated-session";

const hasCanonicalText = (value: unknown): value is string =>
  typeof value === "string" && value.length > 0 && value.trim() === value;

function ownDataValue(value: object, key: PropertyKey): unknown {
  try {
    const descriptor = Object.getOwnPropertyDescriptor(value, key);
    return descriptor && "value" in descriptor ? descriptor.value : undefined;
  } catch {
    return undefined;
  }
}

function readAuthority(state: AuthenticatedSessionState) {
  if (state === null || typeof state !== "object" || ownDataValue(state, "status") !== "ready") return null;
  const membership = ownDataValue(state, "membership");
  if (membership === null || typeof membership !== "object") return null;

  const tenantId = ownDataValue(membership, "tenantId");
  const companyId = ownDataValue(membership, "companyId");
  const companyName = ownDataValue(membership, "companyName");
  const userId = ownDataValue(membership, "userId");
  const userName = ownDataValue(membership, "userName");

  if (
    !hasCanonicalText(tenantId) ||
    !hasCanonicalText(companyId) ||
    !hasCanonicalText(companyName) ||
    !hasCanonicalText(userId) ||
    !hasCanonicalText(userName)
  ) {
    return null;
  }

  return { tenantId, companyId, companyName, userId, userName } as const;
}

export function CustomerPortalShell({ state }: { state: AuthenticatedSessionState }) {
  const authority = readAuthority(state);
  if (!authority) {
    return (
      <main className="mx-auto flex min-h-screen max-w-4xl items-center px-6 py-16">
        <section role="alert" className="w-full rounded-2xl border border-black/10 p-6 dark:border-white/15">
          <p className="text-sm font-medium uppercase tracking-[0.18em] opacity-60">Minh Huy AI Office</p>
          <h1 className="mt-3 text-3xl font-semibold tracking-tight">Không thể mở cổng khách hàng</h1>
          <p className="mt-3 leading-7 opacity-75">Phiên làm việc chưa có phạm vi công ty được máy chủ xác thực.</p>
        </section>
      </main>
    );
  }

  return (
    <main className="mx-auto min-h-screen max-w-6xl px-6 py-10">
      <header className="rounded-2xl border border-black/10 p-6 dark:border-white/15">
        <p className="text-sm font-medium uppercase tracking-[0.18em] opacity-60">Cổng khách hàng</p>
        <h1 className="mt-2 text-3xl font-semibold tracking-tight">{authority.companyName}</h1>
        <p className="mt-2 text-sm opacity-70">Đăng nhập với {authority.userName}</p>
      </header>
      <section aria-label="Không gian làm việc" className="mt-6 rounded-2xl border border-black/10 p-6 dark:border-white/15">
        <h2 className="text-lg font-semibold">Không gian làm việc</h2>
        <p className="mt-2 leading-7 opacity-75">Các chức năng khách hàng sẽ chỉ xuất hiện khi phạm vi và quyền tương ứng được máy chủ xác thực.</p>
      </section>
    </main>
  );
}
