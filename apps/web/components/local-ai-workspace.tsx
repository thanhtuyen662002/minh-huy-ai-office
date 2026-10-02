"use client";

import { FormEvent, useCallback, useEffect, useMemo, useState } from "react";
import {
  LocalAiCheckpoint,
  LocalAuthContext,
  LocalDataSource,
  parseAcceptedTask,
  parseAiCheckpoint,
  parseAuthContext,
  parseDataSources,
  parseTaskSnapshot,
} from "../lib/local-ai-workspace";

type Props = {
  companyId: string;
  companyName: string;
};

type ChatMessage = {
  id: string;
  role: "user" | "assistant";
  text: string;
  checkpoint?: LocalAiCheckpoint;
};

type Surface = "assistant" | "data-sources";

const sleep = (milliseconds: number) => new Promise((resolve) => setTimeout(resolve, milliseconds));

async function readJson(response: Response): Promise<unknown> {
  try {
    return await response.json();
  } catch {
    return null;
  }
}

function errorText(value: unknown, fallback: string) {
  if (value && typeof value === "object") {
    const descriptor = Object.getOwnPropertyDescriptor(value, "error");
    if (descriptor && "value" in descriptor && typeof descriptor.value === "string") return descriptor.value;
  }
  return fallback;
}

function sourceLabel(source: LocalDataSource) {
  if (!source.isEnabled) return "Đã tắt";
  if (source.allowRead && !source.allowWrite) return "Chỉ đọc";
  if (source.allowRead && source.allowWrite) return "Đọc / ghi";
  return "Không có quyền đọc";
}

export function LocalAiWorkspace({ companyId, companyName }: Props) {
  const [surface, setSurface] = useState<Surface>("assistant");
  const [sessionState, setSessionState] = useState<"checking" | "signed-out" | "ready">("checking");
  const [auth, setAuth] = useState<LocalAuthContext | null>(null);
  const [sources, setSources] = useState<readonly LocalDataSource[]>([]);
  const [selectedSourceId, setSelectedSourceId] = useState("");
  const [sourceLoading, setSourceLoading] = useState(false);
  const [connectionState, setConnectionState] = useState<Record<string, string>>({});
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);
  const [taskStage, setTaskStage] = useState("");
  const [messages, setMessages] = useState<ChatMessage[]>([
    {
      id: "welcome",
      role: "assistant",
      text: "Xin chào. Tôi là AI Office của Minh Huy. Hãy chọn nguồn dữ liệu rồi đặt câu hỏi; tôi chỉ sử dụng dữ liệu đã được hệ thống cấp quyền.",
    },
  ]);

  const readOnlySources = useMemo(
    () => sources.filter((source) => source.isEnabled && source.allowRead && !source.allowWrite),
    [sources],
  );
  const selectedSource = sources.find((source) => source.id === selectedSourceId) ?? null;

  const loadSources = useCallback(async () => {
    setSourceLoading(true);
    setNotice("");
    try {
      const response = await fetch(`/api/local/data-sources?companyId=${encodeURIComponent(companyId)}`, {
        cache: "no-store",
      });
      if (response.status === 401) {
        setSessionState("signed-out");
        setAuth(null);
        setSources([]);
        return;
      }
      const payload = await readJson(response);
      if (!response.ok) {
        setNotice(errorText(payload, "Không tải được nguồn dữ liệu."));
        return;
      }
      const parsed = parseDataSources(payload);
      if (!parsed) {
        setNotice("Core API trả về danh sách nguồn dữ liệu không hợp lệ.");
        return;
      }
      setSources(parsed);
      const preferred = parsed.find((source) => source.isEnabled && source.allowRead && !source.allowWrite)
        ?? parsed.find((source) => source.isEnabled && source.allowRead)
        ?? parsed[0];
      setSelectedSourceId((current) => current || preferred?.id || "");
    } finally {
      setSourceLoading(false);
    }
  }, [companyId]);

  const restoreSession = useCallback(async () => {
    setSessionState("checking");
    try {
      const response = await fetch(`/api/local/session?companyId=${encodeURIComponent(companyId)}`, {
        cache: "no-store",
      });
      if (!response.ok) {
        setSessionState("signed-out");
        setAuth(null);
        return;
      }
      const payload = await readJson(response);
      const parsed = parseAuthContext(payload);
      if (!parsed || parsed.companyId !== companyId) {
        setSessionState("signed-out");
        setAuth(null);
        return;
      }
      setAuth(parsed);
      setSessionState("ready");
    } catch {
      setSessionState("signed-out");
      setAuth(null);
    }
  }, [companyId]);

  useEffect(() => {
    void restoreSession();
  }, [restoreSession]);

  useEffect(() => {
    if (sessionState === "ready") void loadSources();
  }, [sessionState, loadSources]);

  async function signIn(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setNotice("");
    const form = event.currentTarget;
    const data = new FormData(form);
    const username = String(data.get("username") ?? "").trim();
    const password = String(data.get("password") ?? "");

    if (!username || !password) {
      setNotice("Nhập tên đăng nhập và mật khẩu.");
      return;
    }

    setBusy(true);
    try {
      const response = await fetch("/api/local/session/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username, password, companyId }),
      });
      const payload = await readJson(response);
      if (!response.ok) {
        setNotice(errorText(payload, "Đăng nhập thất bại."));
        return;
      }
      const contextValue = payload && typeof payload === "object"
        ? Object.getOwnPropertyDescriptor(payload, "context")?.value
        : null;
      const context = parseAuthContext(contextValue);
      if (!context) {
        setNotice("Phiên đăng nhập không hợp lệ.");
        return;
      }
      setAuth(context);
      setSessionState("ready");
      form.reset();
    } catch {
      setNotice("Không kết nối được dịch vụ đăng nhập.");
    } finally {
      setBusy(false);
    }
  }

  async function signOut() {
    setBusy(true);
    try {
      await fetch("/api/local/session/logout", { method: "POST" });
    } finally {
      setAuth(null);
      setSources([]);
      setSelectedSourceId("");
      setSessionState("signed-out");
      setBusy(false);
    }
  }

  async function testConnection(source: LocalDataSource) {
    setConnectionState((current) => ({ ...current, [source.id]: "Đang kiểm tra…" }));
    try {
      const response = await fetch(
        `/api/local/data-sources/${encodeURIComponent(source.id)}/connection-test?companyId=${encodeURIComponent(companyId)}`,
        { method: "POST" },
      );
      const payload = await readJson(response);
      const code = payload && typeof payload === "object"
        ? Object.getOwnPropertyDescriptor(payload, "code")?.value
        : null;
      setConnectionState((current) => ({
        ...current,
        [source.id]: response.ok && code === "success" ? "Kết nối tốt" : "Kết nối thất bại",
      }));
    } catch {
      setConnectionState((current) => ({ ...current, [source.id]: "Kết nối thất bại" }));
    }
  }

  async function askAi(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;

    const form = event.currentTarget;
    const data = new FormData(form);
    const question = String(data.get("question") ?? "").trim();

    if (!selectedSourceId) {
      setNotice("Hãy chọn nguồn dữ liệu trước.");
      return;
    }
    if (!question) {
      setNotice("Nhập câu hỏi cho AI Office.");
      return;
    }
    if (question.length > 4000) {
      setNotice("Câu hỏi tối đa 4.000 ký tự.");
      return;
    }

    const userMessage: ChatMessage = {
      id: crypto.randomUUID(),
      role: "user",
      text: question,
    };
    setMessages((current) => [...current, userMessage]);
    form.reset();
    setBusy(true);
    setNotice("");
    setTaskStage("Đang gửi công việc…");

    try {
      const submitResponse = await fetch(
        `/api/local/tasks?companyId=${encodeURIComponent(companyId)}`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ dataSourceId: selectedSourceId, question }),
        },
      );
      const submitPayload = await readJson(submitResponse);
      if (!submitResponse.ok) {
        throw new Error(errorText(submitPayload, "Không gửi được công việc."));
      }
      const accepted = parseAcceptedTask(submitPayload);
      if (!accepted) throw new Error("Core API không trả về TaskId hợp lệ.");

      setTaskStage("Đã vào hàng đợi · AI đang xử lý");

      for (let attempt = 0; attempt < 90; attempt += 1) {
        await sleep(1500);
        const resultResponse = await fetch(
          `/api/local/tasks/${encodeURIComponent(accepted.taskId)}?companyId=${encodeURIComponent(companyId)}`,
          { cache: "no-store" },
        );
        const resultPayload = await readJson(resultResponse);
        if (!resultResponse.ok) {
          if (resultResponse.status === 401) {
            setSessionState("signed-out");
            setAuth(null);
            throw new Error("Phiên đăng nhập đã hết hạn.");
          }
          continue;
        }

        const snapshot = parseTaskSnapshot(resultPayload);
        if (!snapshot) continue;

        setTaskStage(`Đang xử lý · lần chạy ${snapshot.attempt}`);

        if (snapshot.failureReason) throw new Error(snapshot.failureReason);
        if (!snapshot.resultPayloadJson) continue;

        const checkpoint = parseAiCheckpoint(snapshot.resultPayloadJson);
        if (!checkpoint) throw new Error("Kết quả AI không đúng định dạng.");

        setMessages((current) => [
          ...current,
          {
            id: `assistant-${accepted.taskId}`,
            role: "assistant",
            text: checkpoint.answer,
            checkpoint,
          },
        ]);
        setTaskStage("");
        return;
      }

      throw new Error("Công việc chưa hoàn tất trong thời gian chờ của giao diện.");
    } catch (error) {
      const message = error instanceof Error ? error.message : "AI Office gặp lỗi khi xử lý.";
      setMessages((current) => [
        ...current,
        { id: crypto.randomUUID(), role: "assistant", text: `Không thể hoàn tất: ${message}` },
      ]);
      setTaskStage("");
    } finally {
      setBusy(false);
    }
  }

  if (sessionState === "checking") {
    return (
      <main className="min-h-screen bg-[#f5f6f8] text-[#172033] dark:bg-[#0b1020] dark:text-[#edf2ff]">
        <div className="mx-auto flex min-h-screen max-w-7xl items-center justify-center px-6">
          <div className="rounded-3xl border border-black/10 bg-white p-8 shadow-sm dark:border-white/10 dark:bg-[#11182a]">
            <p className="text-sm font-semibold tracking-[0.2em] text-indigo-600 dark:text-indigo-300">MINH HUY AI OFFICE</p>
            <h1 className="mt-3 text-2xl font-semibold">Đang kiểm tra phiên đăng nhập…</h1>
          </div>
        </div>
      </main>
    );
  }

  if (sessionState === "signed-out") {
    return (
      <main className="min-h-screen bg-[#f5f6f8] px-6 py-10 text-[#172033] dark:bg-[#0b1020] dark:text-[#edf2ff]">
        <div className="mx-auto grid min-h-[calc(100vh-5rem)] max-w-6xl items-center gap-10 lg:grid-cols-[1.1fr_0.9fr]">
          <section>
            <div className="inline-flex rounded-full border border-indigo-200 bg-indigo-50 px-3 py-1 text-xs font-semibold text-indigo-700 dark:border-indigo-400/20 dark:bg-indigo-400/10 dark:text-indigo-200">
              Local Pilot
            </div>
            <p className="mt-7 text-sm font-semibold tracking-[0.2em] text-indigo-600 dark:text-indigo-300">MINH HUY</p>
            <h1 className="mt-3 max-w-2xl text-5xl font-semibold tracking-[-0.04em] sm:text-6xl">AI Office</h1>
            <p className="mt-5 max-w-xl text-lg leading-8 text-slate-600 dark:text-slate-300">
              Làm việc với ERP và AI trên một giao diện duy nhất. Phiên local hiện dùng quyền chỉ đọc và OpenAI trực tiếp.
            </p>
            <div className="mt-8 grid max-w-xl gap-3 sm:grid-cols-3">
              {["ERP chỉ đọc", "Task bền vững", "AI có bằng chứng"].map((item) => (
                <div key={item} className="rounded-2xl border border-black/10 bg-white p-4 text-sm font-medium shadow-sm dark:border-white/10 dark:bg-white/5">
                  {item}
                </div>
              ))}
            </div>
          </section>

          <form onSubmit={signIn} className="rounded-[28px] border border-black/10 bg-white p-7 shadow-xl shadow-slate-900/5 dark:border-white/10 dark:bg-[#11182a]">
            <p className="text-sm font-medium text-slate-500 dark:text-slate-400">Đăng nhập local</p>
            <h2 className="mt-2 text-2xl font-semibold">{companyName}</h2>
            <label className="mt-7 block text-sm font-medium">
              Tên đăng nhập
              <input
                name="username"
                defaultValue="pilot-admin"
                autoComplete="username"
                className="mt-2 w-full rounded-xl border border-slate-200 bg-transparent px-4 py-3 outline-none transition focus:border-indigo-500 dark:border-white/15"
              />
            </label>
            <label className="mt-4 block text-sm font-medium">
              Mật khẩu
              <input
                name="password"
                type="password"
                autoComplete="current-password"
                className="mt-2 w-full rounded-xl border border-slate-200 bg-transparent px-4 py-3 outline-none transition focus:border-indigo-500 dark:border-white/15"
              />
            </label>
            {notice ? <p role="alert" className="mt-4 rounded-xl bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-200">{notice}</p> : null}
            <button
              type="submit"
              disabled={busy}
              className="mt-6 w-full rounded-xl bg-indigo-600 px-4 py-3 font-semibold text-white transition hover:bg-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {busy ? "Đang đăng nhập…" : "Vào AI Office"}
            </button>
            <p className="mt-4 text-xs leading-5 text-slate-500 dark:text-slate-400">
              Token được giữ trong cookie HttpOnly của web local, không hiển thị cho JavaScript phía trình duyệt.
            </p>
          </form>
        </div>
      </main>
    );
  }

  return (
    <main className="min-h-screen bg-[#f5f6f8] text-[#172033] dark:bg-[#0b1020] dark:text-[#edf2ff]">
      <div className="mx-auto flex min-h-screen max-w-[1500px]">
        <aside className="hidden w-64 shrink-0 border-r border-slate-200 bg-white px-4 py-6 dark:border-white/10 dark:bg-[#0f1627] lg:flex lg:flex-col">
          <div className="px-3">
            <p className="text-xs font-semibold tracking-[0.2em] text-indigo-600 dark:text-indigo-300">MINH HUY</p>
            <h1 className="mt-1 text-xl font-semibold">AI Office</h1>
          </div>
          <nav className="mt-8 space-y-1" aria-label="Điều hướng chính">
            <button type="button" onClick={() => setSurface("assistant")} className={`w-full rounded-xl px-3 py-2.5 text-left text-sm font-medium ${surface === "assistant" ? "bg-indigo-50 text-indigo-700 dark:bg-indigo-400/10 dark:text-indigo-200" : "text-slate-600 hover:bg-slate-50 dark:text-slate-300 dark:hover:bg-white/5"}`}>
              Trợ lý AI
            </button>
            <button type="button" onClick={() => setSurface("data-sources")} className={`w-full rounded-xl px-3 py-2.5 text-left text-sm font-medium ${surface === "data-sources" ? "bg-indigo-50 text-indigo-700 dark:bg-indigo-400/10 dark:text-indigo-200" : "text-slate-600 hover:bg-slate-50 dark:text-slate-300 dark:hover:bg-white/5"}`}>
              Nguồn dữ liệu
            </button>
            <div className="px-3 py-2.5 text-sm text-slate-400">Công việc · sắp có</div>
            <div className="px-3 py-2.5 text-sm text-slate-400">Nhật ký · sắp có</div>
          </nav>
          <div className="mt-auto rounded-2xl bg-slate-50 p-4 text-xs leading-5 text-slate-500 dark:bg-white/5 dark:text-slate-400">
            <p className="font-semibold text-slate-700 dark:text-slate-200">{companyName}</p>
            <p className="mt-1">Vai trò: {auth?.roles.join(", ") || "member"}</p>
            <p className="mt-1">Local Pilot · Read-only</p>
          </div>
        </aside>

        <div className="min-w-0 flex-1">
          <header className="flex min-h-20 items-center justify-between border-b border-slate-200 bg-white/90 px-5 backdrop-blur dark:border-white/10 dark:bg-[#0f1627]/90 sm:px-8">
            <div>
              <p className="text-xs font-medium text-slate-500 dark:text-slate-400">Công ty đang làm việc</p>
              <h2 className="mt-0.5 font-semibold">{companyName}</h2>
            </div>
            <div className="flex items-center gap-3">
              <span className="hidden rounded-full bg-emerald-50 px-3 py-1.5 text-xs font-semibold text-emerald-700 dark:bg-emerald-400/10 dark:text-emerald-200 sm:inline-flex">
                Worker online
              </span>
              <button type="button" onClick={() => void signOut()} disabled={busy} className="rounded-xl border border-slate-200 px-3 py-2 text-sm font-medium dark:border-white/15">
                Đăng xuất
              </button>
            </div>
          </header>

          <div className="px-4 py-5 sm:px-8 sm:py-7">
            <div className="mb-5 flex gap-2 lg:hidden">
              <button type="button" onClick={() => setSurface("assistant")} className="rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm dark:border-white/10 dark:bg-white/5">Trợ lý AI</button>
              <button type="button" onClick={() => setSurface("data-sources")} className="rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm dark:border-white/10 dark:bg-white/5">Nguồn dữ liệu</button>
            </div>

            {surface === "assistant" ? (
              <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_330px]">
                <section className="flex min-h-[calc(100vh-9rem)] flex-col overflow-hidden rounded-[24px] border border-slate-200 bg-white shadow-sm dark:border-white/10 dark:bg-[#11182a]">
                  <div className="border-b border-slate-100 px-5 py-4 dark:border-white/10 sm:px-6">
                    <div className="flex flex-wrap items-center justify-between gap-3">
                      <div>
                        <h2 className="text-lg font-semibold">Trợ lý AI</h2>
                        <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                          {selectedSource ? `Nguồn: ${selectedSource.logicalName}` : "Chưa có nguồn dữ liệu"}
                        </p>
                      </div>
                      <select
                        aria-label="Nguồn dữ liệu"
                        value={selectedSourceId}
                        onChange={(event) => setSelectedSourceId(event.target.value)}
                        className="max-w-xs rounded-xl border border-slate-200 bg-transparent px-3 py-2 text-sm dark:border-white/15"
                      >
                        <option value="">Chọn nguồn dữ liệu</option>
                        {readOnlySources.map((source) => <option key={source.id} value={source.id}>{source.logicalName}</option>)}
                      </select>
                    </div>
                  </div>

                  <div className="flex-1 space-y-5 overflow-y-auto px-4 py-6 sm:px-6">
                    {messages.map((message) => (
                      <article key={message.id} className={`flex ${message.role === "user" ? "justify-end" : "justify-start"}`}>
                        <div className={`max-w-[88%] rounded-2xl px-4 py-3 sm:max-w-[78%] ${message.role === "user" ? "bg-indigo-600 text-white" : "bg-slate-100 text-slate-800 dark:bg-white/7 dark:text-slate-100"}`}>
                          <p className="whitespace-pre-wrap text-sm leading-6">{message.text}</p>
                          {message.checkpoint ? (
                            <div className="mt-3 flex flex-wrap gap-x-3 gap-y-1 border-t border-current/10 pt-2 text-[11px] opacity-65">
                              <span>{message.checkpoint.provider}</span>
                              <span>{message.checkpoint.model}</span>
                              <span>{message.checkpoint.totalTokens.toLocaleString("vi-VN")} tokens</span>
                            </div>
                          ) : null}
                        </div>
                      </article>
                    ))}
                    {taskStage ? (
                      <div className="flex justify-start">
                        <div className="rounded-2xl bg-indigo-50 px-4 py-3 text-sm text-indigo-700 dark:bg-indigo-400/10 dark:text-indigo-200">{taskStage}</div>
                      </div>
                    ) : null}
                  </div>

                  <form onSubmit={askAi} className="border-t border-slate-100 p-4 dark:border-white/10 sm:p-5">
                    {notice ? <p role="status" className="mb-3 text-sm text-rose-600 dark:text-rose-300">{notice}</p> : null}
                    <div className="flex items-end gap-3 rounded-2xl border border-slate-200 bg-slate-50 p-2 focus-within:border-indigo-400 dark:border-white/10 dark:bg-white/5">
                      <textarea
                        name="question"
                        rows={2}
                        disabled={busy}
                        placeholder="Hỏi AI Office về nguồn dữ liệu đã chọn…"
                        className="min-h-12 flex-1 resize-none bg-transparent px-3 py-2 text-sm outline-none placeholder:text-slate-400"
                      />
                      <button type="submit" disabled={busy || !selectedSourceId} className="rounded-xl bg-indigo-600 px-5 py-3 text-sm font-semibold text-white hover:bg-indigo-500 disabled:cursor-not-allowed disabled:opacity-40">
                        Gửi
                      </button>
                    </div>
                    <p className="mt-2 px-1 text-xs text-slate-400">AI chỉ được dùng evidence mà runtime đã cấp quyền; không tự viết SQL từ câu hỏi.</p>
                  </form>
                </section>

                <aside className="space-y-5">
                  <section className="rounded-[24px] border border-slate-200 bg-white p-5 shadow-sm dark:border-white/10 dark:bg-[#11182a]">
                    <p className="text-xs font-semibold uppercase tracking-[0.16em] text-slate-400">Nguồn đang dùng</p>
                    {selectedSource ? (
                      <>
                        <h3 className="mt-3 font-semibold">{selectedSource.logicalName}</h3>
                        <p className="mt-1 text-sm leading-6 text-slate-500 dark:text-slate-400">{selectedSource.purpose}</p>
                        <dl className="mt-5 grid grid-cols-2 gap-3 text-sm">
                          <div className="rounded-xl bg-slate-50 p-3 dark:bg-white/5"><dt className="text-xs text-slate-400">Quyền</dt><dd className="mt-1 font-medium">{sourceLabel(selectedSource)}</dd></div>
                          <div className="rounded-xl bg-slate-50 p-3 dark:bg-white/5"><dt className="text-xs text-slate-400">Đồng thời</dt><dd className="mt-1 font-medium">{selectedSource.maxConcurrency}</dd></div>
                        </dl>
                      </>
                    ) : <p className="mt-3 text-sm text-slate-500">Chưa có nguồn read-only khả dụng.</p>}
                  </section>

                  {messages.slice().reverse().find((message) => message.checkpoint)?.checkpoint ? (() => {
                    const checkpoint = messages.slice().reverse().find((message) => message.checkpoint)!.checkpoint!;
                    return (
                      <section className="rounded-[24px] border border-slate-200 bg-white p-5 shadow-sm dark:border-white/10 dark:bg-[#11182a]">
                        <p className="text-xs font-semibold uppercase tracking-[0.16em] text-slate-400">Evidence gần nhất</p>
                        <dl className="mt-4 space-y-3 text-sm">
                          <div><dt className="text-slate-400">Database</dt><dd className="mt-1 font-medium">{checkpoint.erpDatabase ?? "—"}</dd></div>
                          <div><dt className="text-slate-400">Số bảng</dt><dd className="mt-1 font-medium">{checkpoint.erpTableCount ?? "—"}</dd></div>
                          <div><dt className="text-slate-400">Model</dt><dd className="mt-1 font-medium">{checkpoint.model}</dd></div>
                          <div><dt className="text-slate-400">Token</dt><dd className="mt-1 font-medium">{checkpoint.inputTokens} in · {checkpoint.outputTokens} out</dd></div>
                        </dl>
                      </section>
                    );
                  })() : null}
                </aside>
              </div>
            ) : (
              <section className="rounded-[24px] border border-slate-200 bg-white p-5 shadow-sm dark:border-white/10 dark:bg-[#11182a] sm:p-7">
                <div className="flex flex-wrap items-center justify-between gap-4">
                  <div>
                    <p className="text-xs font-semibold uppercase tracking-[0.16em] text-slate-400">Resource Plane</p>
                    <h2 className="mt-2 text-2xl font-semibold">Nguồn dữ liệu</h2>
                    <p className="mt-2 text-sm text-slate-500 dark:text-slate-400">Danh sách thật từ Core API, không còn demoDataSources.</p>
                  </div>
                  <button type="button" onClick={() => void loadSources()} disabled={sourceLoading} className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-medium dark:border-white/15">
                    {sourceLoading ? "Đang tải…" : "Làm mới"}
                  </button>
                </div>
                <div className="mt-6 grid gap-4 xl:grid-cols-2">
                  {sources.map((source) => (
                    <article key={source.id} className="rounded-2xl border border-slate-200 p-5 dark:border-white/10">
                      <div className="flex items-start justify-between gap-4">
                        <div>
                          <h3 className="font-semibold">{source.logicalName}</h3>
                          <p className="mt-1 text-sm text-slate-500 dark:text-slate-400">{source.purpose}</p>
                        </div>
                        <span className={`rounded-full px-2.5 py-1 text-xs font-semibold ${source.allowRead && !source.allowWrite ? "bg-emerald-50 text-emerald-700 dark:bg-emerald-400/10 dark:text-emerald-200" : "bg-amber-50 text-amber-700 dark:bg-amber-400/10 dark:text-amber-200"}`}>
                          {sourceLabel(source)}
                        </span>
                      </div>
                      <dl className="mt-5 grid grid-cols-3 gap-3 text-sm">
                        <div><dt className="text-xs text-slate-400">Loại</dt><dd className="mt-1 font-medium">{source.kind}</dd></div>
                        <div><dt className="text-xs text-slate-400">Môi trường</dt><dd className="mt-1 font-medium">{source.environment}</dd></div>
                        <div><dt className="text-xs text-slate-400">Đồng thời</dt><dd className="mt-1 font-medium">{source.maxConcurrency}</dd></div>
                      </dl>
                      <div className="mt-5 flex items-center justify-between gap-3 rounded-xl bg-slate-50 p-3 dark:bg-white/5">
                        <span className="text-sm">{connectionState[source.id] ?? "Chưa kiểm tra trong phiên này"}</span>
                        <button type="button" onClick={() => void testConnection(source)} className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs font-semibold dark:border-white/10 dark:bg-white/5">
                          Kiểm tra
                        </button>
                      </div>
                    </article>
                  ))}
                </div>
                {!sourceLoading && sources.length === 0 ? <p className="mt-6 rounded-xl bg-slate-50 p-5 text-sm text-slate-500 dark:bg-white/5">Chưa có nguồn dữ liệu được cấp quyền.</p> : null}
              </section>
            )}
          </div>
        </div>
      </div>
    </main>
  );
}
