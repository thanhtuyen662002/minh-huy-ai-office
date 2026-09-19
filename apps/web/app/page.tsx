export default function Home() {
  return (
    <main className="mx-auto flex min-h-screen max-w-5xl flex-col justify-center gap-6 px-8 py-16">
      <p className="text-sm font-medium uppercase tracking-[0.2em] opacity-60">
        Minh Huy
      </p>
      <h1 className="text-5xl font-semibold tracking-tight">AI Office</h1>
      <p className="max-w-2xl text-lg leading-8 opacity-75">
        Nền tảng agent đa công ty cho ERP, kế toán, hỗ trợ khách hàng và vận hành.
      </p>
      <div className="rounded-xl border border-black/10 p-5 dark:border-white/15">
        <p className="font-medium">Foundation status</p>
        <p className="mt-2 text-sm opacity-70">
          Monorepo bootstrap in progress. Durable task, context and agent subsystems
          will be added phase by phase.
        </p>
      </div>
    </main>
  );
}
