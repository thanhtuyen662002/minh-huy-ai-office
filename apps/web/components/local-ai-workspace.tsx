"use client";

import { FormEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  LocalAiCheckpoint,
  LocalDataSource,
  parseAcceptedTask,
  parseAiCheckpoint,
  parseDataSources,
  parseTaskSnapshot,
} from "../lib/local-ai-workspace";
import { useLocalSession } from "./use-local-session";
import { SourceMetadataEditor } from "./source-metadata-editor";
import { SourceRegistrationPanel } from "./source-registration-panel";
import { CompanyMemberPanel } from "./company-member-panel";
import { sameSourceMetadata, sourceMetadataUpdate, SourceMetadataDraft, taskSourceSelection } from "../lib/source-metadata-editor";
import { browserLoginDestination, navigateBrowserLogin, type PublicBrowserLogin } from "../lib/browser-login-navigation";

type Props = {
  companyId: string;
  companyName: string;
  loginMode?: "local" | "browser";
  browserLogin?: PublicBrowserLogin;
};

type ChatMessage = {
  id: string;
  role: "user" | "assistant";
  text: string;
  checkpoint?: LocalAiCheckpoint;
};

type Surface = "assistant" | "data-sources" | "members";

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

export function LocalAiWorkspace({ companyId, companyName, loginMode = "local", browserLogin }: Props) {
  const [surface, setSurface] = useState<Surface>("assistant");
  const [sources, setSources] = useState<readonly LocalDataSource[]>([]);
  const [selectedSourceId, setSelectedSourceId] = useState("");
  const [sourceLoading, setSourceLoading] = useState(false);
  const [connectionState, setConnectionState] = useState<Record<string, string>>({});
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);
  const [taskStage, setTaskStage] = useState("");
  const [editingSource, setEditingSource] = useState<LocalDataSource | null>(null);
  const [addingSource, setAddingSource] = useState(false);
  const [sourceSaving, setSourceSaving] = useState(false);
  const [editorError, setEditorError] = useState("");
  const sourceSave = useRef<object | null>(null);
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

  const clearPrivateState = useCallback(() => {
    setSources([]);
    setSelectedSourceId("");
    setSourceLoading(false);
    setBusy(false);
    setConnectionState({});
    setNotice("");
    setTaskStage("");
    setEditingSource(null);
    setAddingSource(false);
    setSourceSaving(false);
    setEditorError("");
    sourceSave.current = null;
    setSurface("assistant");
    setMessages((current) => current.filter((message) => message.id === "welcome"));
  }, []);

  const { phase: sessionState, auth, generation: sessionGeneration, isCurrent, reset, request,
    validate, pause, restore, beginMutation, ready } = useLocalSession(companyId, clearPrivateState);

  // Another validation can invalidate this session between validate resolving
  // and its caller resuming. Recheck the generation after every awaited result.
  const loadSources = useCallback(async () => {
    if (!ready()) return;
    const generation = sessionGeneration.current;
    setSourceLoading(true);
    setNotice("");
    setEditorError("");
    try {
      if (!await validate(generation) || !isCurrent(generation)) return;
      const response = await request(generation, `/api/local/data-sources?companyId=${encodeURIComponent(companyId)}`, {
        cache: "no-store",
      });
      if (!isCurrent(generation)) return;
      if (response.status === 401) {
        reset("signed-out");
        return;
      }
      const payload = await readJson(response);
      if (!isCurrent(generation)) return;
      if (!await validate(generation) || !isCurrent(generation)) return;
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
      setSelectedSourceId((current) => taskSourceSelection(parsed, current));
    } catch {
      if (isCurrent(generation)) setNotice("Không kết nối được nguồn dữ liệu.");
    } finally {
      if (isCurrent(generation)) setSourceLoading(false);
    }
  }, [companyId, isCurrent, request, reset, validate, sessionGeneration, ready]);

  useEffect(() => {
    if (sessionState === "ready") void loadSources();
  }, [sessionState, auth, loadSources]);

  async function editSource(source: LocalDataSource) {
    if (!ready() || sourceLoading || sourceSave.current || !auth?.roles.includes("admin")) return;
    const generation = sessionGeneration.current;
    if (!await validate(generation) || !isCurrent(generation)) return;
    setNotice("");
    setEditorError("");
    setEditingSource(source);
  }

  async function saveSource(draft: SourceMetadataDraft) {
    if (!ready() || sourceLoading || sourceSave.current || !editingSource || !auth?.roles.includes("admin")) return;
    const source = editingSource;
    const generation = sessionGeneration.current;
    const operation = {};
    sourceSave.current = operation;
    setSourceSaving(true);
    setEditorError("");
    setNotice("");
    let writeAccepted = false;
    async function authoritativeSources() {
      const response = await request(generation, `/api/local/data-sources?companyId=${encodeURIComponent(companyId)}`, { cache: "no-store" });
      if (!isCurrent(generation)) return null;
      if (response.status === 401) { reset("signed-out"); return null; }
      const payload = await readJson(response);
      if (!isCurrent(generation) || !await validate(generation) || !isCurrent(generation)) return null;
      if (!response.ok) throw new Error("Không xác minh được nguồn dữ liệu. Hãy thử lại.");
      const parsed = parseDataSources(payload);
      if (!parsed) throw new Error("Danh sách nguồn trả về không hợp lệ. Hãy làm mới.");
      return parsed;
    }
    try {
      sourceMetadataUpdate(draft);
      if (!await validate(generation) || !isCurrent(generation)) return;
      const before = await authoritativeSources();
      if (!before || !isCurrent(generation)) return;
      const current = before.find(item => item.id === source.id);
      if (!current || !sameSourceMetadata(source, current)) {
        throw new Error("Nguồn đã thay đổi. Hãy hủy, làm mới và mở lại trước khi lưu.");
      }
      const update = sourceMetadataUpdate(draft);
      const response = await request(generation, `/api/local/data-sources/${encodeURIComponent(source.id)}/metadata?companyId=${encodeURIComponent(companyId)}`, {
        method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify(update),
      });
      if (!isCurrent(generation)) return;
      if (response.status === 401) { reset("signed-out"); return; }
      const payload = await readJson(response);
      if (!isCurrent(generation) || !await validate(generation) || !isCurrent(generation)) return;
      if (!response.ok) {
        throw new Error(response.status === 403 ? "Máy chủ từ chối quyền quản trị nguồn. Hãy kiểm tra lại quyền truy cập."
          : response.status === 400 ? "Máy chủ từ chối thông tin nguồn. Kiểm tra tên, mục đích và số tác vụ rồi thử lại."
            : response.status === 404 ? "Nguồn không còn khả dụng. Hãy hủy và làm mới."
              : "Không lưu được nguồn dữ liệu. Hãy thử lại.");
      }
      writeAccepted = true;
      const returned = parseDataSources([payload])?.[0];
      if (!returned || returned.id !== source.id) throw new Error("Phản hồi cập nhật không hợp lệ.");
      const after = await authoritativeSources();
      if (!after || !isCurrent(generation)) return;
      const saved = after.find(item => item.id === source.id);
      const expected = { ...current, ...update };
      if (!saved || !sameSourceMetadata(saved, expected)) throw new Error("Chưa xác nhận được dữ liệu sau cập nhật.");
      setSources(after);
      setSelectedSourceId(selected => taskSourceSelection(after, selected));
      setConnectionState(states => { const next = { ...states }; delete next[source.id]; return next; });
      setEditingSource(null);
      setNotice("Đã lưu thay đổi và xác nhận lại nguồn dữ liệu.");
    } catch (error) {
      if (!isCurrent(generation)) return;
      if (writeAccepted) {
        // The write may have disabled this source. Discard cached eligibility
        // until a fresh registry read succeeds instead of offering stale tasks.
        setSources([]);
        setSelectedSourceId("");
        setConnectionState({});
        setEditingSource(null);
      }
      setEditorError(writeAccepted ? "Máy chủ đã nhận cập nhật nhưng chưa xác nhận lại được dữ liệu. Hãy làm mới trước khi tiếp tục."
        : error instanceof Error ? error.message : "Không kết nối được dịch vụ. Hãy thử lại.");
    } finally {
      if (sourceSave.current === operation) sourceSave.current = null;
      if (isCurrent(generation)) setSourceSaving(false);
    }
  }

  async function signIn(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (sessionState !== "signed-out") return;
    if (loginMode === "browser") {
      if (!browserLogin) { setNotice("Dịch vụ đăng nhập chưa sẵn sàng."); return; }
      const finish = beginMutation("signed-out");
      if (!finish) return;
      const generation = sessionGeneration.current;
      setNotice(""); setBusy(true);
      let navigating = false;
      try {
        // Settle both cookie mutations in order. Aborting would not undo a
        // received cookie, so company changes wait before restoring context.
        const prepared = await fetch("/api/local/session/oidc/binding", {
          method: "POST", cache: "no-store", redirect: "error", credentials: "same-origin",
        });
        const preparation = await readJson(prepared);
        if (!isCurrent(generation)) return;
        if (!prepared.ok || !preparation || typeof preparation !== "object"
          || Object.keys(preparation).join(",") !== "ok" || Object.getOwnPropertyDescriptor(preparation, "ok")?.value !== true) throw new Error();
        const started = await fetch("/api/local/session/oidc/start", {
          method: "POST", cache: "no-store", redirect: "error", credentials: "same-origin",
          headers: { "Content-Type": "application/json" }, body: JSON.stringify({ companyId }),
        });
        const payload = await readJson(started);
        if (!isCurrent(generation)) return;
        const destination = started.ok ? browserLoginDestination(payload, browserLogin) : null;
        if (!destination) throw new Error();
        navigateBrowserLogin(destination);
        navigating = true;
      } catch {
        if (isCurrent(generation)) setNotice("Không bắt đầu được đăng nhập. Hãy thử lại.");
      } finally {
        finish();
        if (isCurrent(generation) && !navigating) setBusy(false);
      }
      return;
    }
    const form = event.currentTarget;
    const data = new FormData(form);
    const username = String(data.get("username") ?? "").trim();
    const password = String(data.get("password") ?? "");
    if (!username || !password) {
      setNotice("Nhập tên đăng nhập và mật khẩu.");
      return;
    }
    const finish = beginMutation("signed-out");
    if (!finish) return;
    const generation = sessionGeneration.current;
    setNotice("");
    setBusy(true);
    let succeeded = false;
    try {
      // Do not abort cookie-mutating requests: serialize their settlement with
      // subsequent context reads rather than accepting a replacement early.
      const response = await fetch("/api/local/session/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username, password, companyId }),
      });
      const payload = await readJson(response);
      if (!isCurrent(generation)) return;
      if (!response.ok) {
        setNotice(errorText(payload, "Đăng nhập thất bại."));
        return;
      }
      succeeded = true;
      form.reset();
    } catch {
      if (isCurrent(generation)) setNotice("Không kết nối được dịch vụ đăng nhập.");
    } finally {
      finish();
      if (isCurrent(generation)) {
        setBusy(false);
        if (succeeded) await restore();
      }
    }
  }

  async function signOut() {
    if (!ready()) return;
    const finish = beginMutation("ready");
    if (!finish) return;
    const generation = reset("closing");
    let failed = false;
    try {
      const response = await fetch("/api/local/session/logout", { method: "POST" });
      failed = !response.ok;
    } catch {
      failed = true;
    } finally {
      finish();
      if (isCurrent(generation)) {
        reset("signed-out");
        if (failed) setNotice("Không xác nhận được đăng xuất. Hãy thử đăng nhập lại.");
      }
    }
  }

  async function testConnection(source: LocalDataSource) {
    if (!ready()) return;
    const generation = sessionGeneration.current;
    setConnectionState((current) => ({ ...current, [source.id]: "Đang kiểm tra…" }));
    try {
      if (!await validate(generation) || !isCurrent(generation)) return;
      const response = await request(generation,
        `/api/local/data-sources/${encodeURIComponent(source.id)}/connection-test?companyId=${encodeURIComponent(companyId)}`,
        { method: "POST" },
      );
      if (!isCurrent(generation)) return;
      if (response.status === 401) { reset("signed-out"); return; }
      const payload = await readJson(response);
      if (!isCurrent(generation)) return;
      if (!await validate(generation) || !isCurrent(generation)) return;
      const code = payload && typeof payload === "object"
        ? Object.getOwnPropertyDescriptor(payload, "code")?.value
        : null;
      setConnectionState((current) => ({
        ...current,
        [source.id]: response.ok && code === "success" ? "Kết nối tốt"
          : code === "read_only_unqualified" ? "Cần tài khoản ERP chỉ đọc và quyền xem metadata phù hợp. Liên hệ quản trị viên để cấu hình."
            : "Kết nối thất bại",
      }));
    } catch {
      if (!isCurrent(generation)) return;
      setConnectionState((current) => ({ ...current, [source.id]: "Kết nối thất bại" }));
    }
  }

  async function askAi(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy || !ready()) return;
    const generation = sessionGeneration.current;

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
      if (!await validate(generation) || !isCurrent(generation)) return;
      const submitResponse = await request(generation,
        `/api/local/tasks?companyId=${encodeURIComponent(companyId)}`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ dataSourceId: selectedSourceId, question }),
        },
      );
      if (!isCurrent(generation)) return;
      if (submitResponse.status === 401) {
        reset("signed-out");
        return;
      }
      const submitPayload = await readJson(submitResponse);
      if (!isCurrent(generation)) return;
      if (!await validate(generation) || !isCurrent(generation)) return;
      if (!submitResponse.ok) {
        throw new Error(errorText(submitPayload, "Không gửi được công việc."));
      }
      const accepted = parseAcceptedTask(submitPayload);
      if (!accepted) throw new Error("Core API không trả về TaskId hợp lệ.");

      setTaskStage("Đã vào hàng đợi · AI đang xử lý");

      for (let attempt = 0; attempt < 90; attempt += 1) {
        if (!await pause(generation, 1500) || !await validate(generation) || !isCurrent(generation)) return;
        const resultResponse = await request(generation,
          `/api/local/tasks/${encodeURIComponent(accepted.taskId)}?companyId=${encodeURIComponent(companyId)}`,
          { cache: "no-store" },
        );
        if (!isCurrent(generation)) return;
        if (!resultResponse.ok) {
          if (resultResponse.status === 401) {
            reset("signed-out");
            return;
          }
          continue;
        }
        const resultPayload = await readJson(resultResponse);
        if (!isCurrent(generation)) return;
        if (!await validate(generation) || !isCurrent(generation)) return;
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
      if (!isCurrent(generation)) return;
      const message = error instanceof Error ? error.message : "AI Office gặp lỗi khi xử lý.";
      setMessages((current) => [
        ...current,
        { id: crypto.randomUUID(), role: "assistant", text: `Không thể hoàn tất: ${message}` },
      ]);
      setTaskStage("");
    } finally {
      if (isCurrent(generation)) setBusy(false);
    }
  }

  if (sessionState === "checking" || sessionState === "closing" || (auth && auth.companyId !== companyId)) {
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
              {loginMode === "browser" ? "Không gian làm việc" : "Local Pilot"}
            </div>
            <p className="mt-7 text-sm font-semibold tracking-[0.2em] text-indigo-600 dark:text-indigo-300">MINH HUY</p>
            <h1 className="mt-3 max-w-2xl text-5xl font-semibold tracking-[-0.04em] sm:text-6xl">AI Office</h1>
            <p className="mt-5 max-w-xl text-lg leading-8 text-slate-600 dark:text-slate-300">
              {loginMode === "browser" ? "Làm việc với ERP và AI bằng tài khoản doanh nghiệp. Mỗi nguồn dữ liệu được kiểm tra theo quyền truy cập của bạn."
                : "Làm việc với ERP và AI trên một giao diện duy nhất. Phiên local hiện dùng quyền chỉ đọc và OpenAI trực tiếp."}
            </p>
            <div className="mt-8 grid max-w-xl gap-3 sm:grid-cols-3">
              {["ERP chỉ đọc", "Công việc được lưu", "AI có bằng chứng"].map((item) => (
                <div key={item} className="rounded-2xl border border-black/10 bg-white p-4 text-sm font-medium shadow-sm dark:border-white/10 dark:bg-white/5">
                  {item}
                </div>
              ))}
            </div>
          </section>

          <form onSubmit={signIn} className="rounded-[28px] border border-black/10 bg-white p-7 shadow-xl shadow-slate-900/5 dark:border-white/10 dark:bg-[#11182a]">
            <p className="text-sm font-medium text-slate-500 dark:text-slate-400">{loginMode === "browser" ? "Đăng nhập doanh nghiệp" : "Đăng nhập local"}</p>
            <h2 className="mt-2 text-2xl font-semibold">{companyName}</h2>
            {loginMode === "browser" ? <p className="mt-7 text-sm leading-6 text-slate-600 dark:text-slate-300">Tiếp tục đến trang đăng nhập của doanh nghiệp để vào không gian làm việc.</p> : <><label className="mt-7 block text-sm font-medium">
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
            </label></>}
            {notice ? <p role="alert" className="mt-4 rounded-xl bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-200">{notice}</p> : null}
            <button
              type="submit"
              disabled={busy}
              className="mt-6 w-full rounded-xl bg-indigo-600 px-4 py-3 font-semibold text-white transition hover:bg-indigo-500 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {busy ? "Đang đăng nhập…" : loginMode === "browser" ? "Đăng nhập doanh nghiệp" : "Vào AI Office"}
            </button>
            <p className="mt-4 text-xs leading-5 text-slate-500 dark:text-slate-400">
              {loginMode === "browser" ? "Quyền truy cập được xác minh lại khi bạn sử dụng nguồn dữ liệu."
                : "Token được giữ trong cookie HttpOnly của web local, không hiển thị cho JavaScript phía trình duyệt."}
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
            {auth?.roles.includes("admin") ? <button type="button" onClick={() => setSurface("members")} className="w-full rounded-xl px-3 py-2.5 text-left text-sm font-medium">Thành viên</button> : null}
            <div className="px-3 py-2.5 text-sm text-slate-400">Công việc · sắp có</div>
            <div className="px-3 py-2.5 text-sm text-slate-400">Nhật ký · sắp có</div>
          </nav>
          <div className="mt-auto rounded-2xl bg-slate-50 p-4 text-xs leading-5 text-slate-500 dark:bg-white/5 dark:text-slate-400">
            <p className="font-semibold text-slate-700 dark:text-slate-200">{companyName}</p>
            <p className="mt-1">Vai trò: {auth?.roles.join(", ") || "member"}</p>
            <p className="mt-1">{loginMode === "browser" ? "Tài khoản doanh nghiệp" : "Local Pilot · Read-only"}</p>
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
                Đã đăng nhập
              </span>
              <button type="button" onClick={() => void signOut()} className="rounded-xl border border-slate-200 px-3 py-2 text-sm font-medium dark:border-white/15">
                Đăng xuất
              </button>
            </div>
          </header>

          <div className="px-4 py-5 sm:px-8 sm:py-7">
            <div className="mb-5 flex gap-2 lg:hidden">
              <button type="button" onClick={() => setSurface("assistant")} className="rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm dark:border-white/10 dark:bg-white/5">Trợ lý AI</button>
              <button type="button" onClick={() => setSurface("data-sources")} className="rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm dark:border-white/10 dark:bg-white/5">Nguồn dữ liệu</button>
              {auth?.roles.includes("admin") ? <button type="button" onClick={() => setSurface("members")} className="rounded-xl border border-slate-200 bg-white px-4 py-2 text-sm dark:border-white/10 dark:bg-white/5">Thành viên</button> : null}
            </div>

            {surface === "members" ? (auth?.roles.includes("admin") ? <CompanyMemberPanel key={`${companyId}:${sessionGeneration.current}`} companyId={companyId} userId={auth.userId} generation={sessionGeneration.current}
              isCurrent={isCurrent} validate={validate} request={request} onUnauthorized={() => reset("signed-out")} /> : null) : surface === "assistant" ? (
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
                    <p className="mt-2 px-1 text-xs text-slate-400">Câu trả lời dựa trên nguồn dữ liệu bạn được cấp quyền.</p>
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
                    <p className="mt-2 text-sm text-slate-500 dark:text-slate-400">Quản lý và kiểm tra các nguồn ERP của công ty.</p>
                  </div>
                  <button type="button" onClick={() => void loadSources()} disabled={sourceLoading || sourceSaving} className="rounded-xl border border-slate-200 px-4 py-2 text-sm font-medium dark:border-white/15">
                    {sourceLoading ? "Đang tải…" : "Làm mới"}
                  </button>
                  {auth?.roles.includes("admin") && !addingSource ? (
                    <button type="button" disabled={sourceLoading || sourceSaving} onClick={() => { setEditingSource(null); setAddingSource(true); }} className="rounded-xl bg-indigo-600 px-4 py-2 text-sm font-medium text-white">Thêm nguồn chỉ đọc</button>
                  ) : null}
                </div>
                {addingSource && auth?.roles.includes("admin") ? (
                  <SourceRegistrationPanel key={`${companyId}:${sessionGeneration.current}`} companyId={companyId} generation={sessionGeneration.current}
                    isCurrent={isCurrent} validate={validate} request={request} onCancel={() => setAddingSource(false)}
                    onUnauthorized={() => reset("signed-out")}
                    onRegistered={fresh => { setSources(fresh); setSelectedSourceId(current => taskSourceSelection(fresh, current)); setAddingSource(false); setNotice("Đã đăng ký nguồn chỉ đọc và xác nhận lại danh sách."); }} />
                ) : null}
                {notice ? <p role="status" className="mt-4 text-sm text-indigo-700 dark:text-indigo-300">{notice}</p> : null}
                {!editingSource && editorError ? <p role="alert" className="mt-4 text-sm text-rose-600 dark:text-rose-300">{editorError}</p> : null}
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
                      {auth?.roles.includes("admin") && editingSource?.id !== source.id ? (
                        <button type="button" disabled={sourceLoading || sourceSaving} onClick={() => void editSource(source)} className="mt-4 rounded-lg border border-slate-200 px-3 py-2 text-sm font-medium disabled:opacity-50 dark:border-white/15">Chỉnh sửa</button>
                      ) : null}
                      {editingSource?.id === source.id ? (
                        <SourceMetadataEditor source={editingSource} saving={sourceSaving} error={editorError} onSave={draft => void saveSource(draft)} onCancel={() => { setEditingSource(null); setEditorError(""); }} />
                      ) : null}
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
