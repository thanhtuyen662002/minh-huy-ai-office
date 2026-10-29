"use client";

import { FormEvent, useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import {
  LocalAiCheckpoint,
  LocalDataSource,
  parseAiCheckpoint,
  parseDataSources,
  parseTaskSnapshot,
} from "../lib/local-ai-workspace";
import { useLocalSession } from "./use-local-session";
import { SourceMetadataEditor } from "./source-metadata-editor";
import { SourceRegistrationPanel } from "./source-registration-panel";
import { CustomerErpPanel } from "./customer-erp-panel";
import { CompanyMemberPanel } from "./company-member-panel";
import { TaskHistoryPanel } from "./task-history-panel";
import { GroupInboxPanel } from "./group-inbox-panel";
import { CustomerRequestPanel } from "./customer-request-panel";
import { BotChannelPanel, useBotChannelsAvailable } from "./bot-channel-panel";
import { SubmissionRecoveryPanel } from "./submission-recovery-panel";
import { useTaskSubmission } from "./use-task-submission";
import { trimSubmissionQuestion } from "../lib/task-submission-intent";
import { sameSourceMetadata, sourceMetadataUpdate, SourceMetadataDraft, taskSourceSelection } from "../lib/source-metadata-editor";
import { browserLoginDestination, navigateBrowserLogin, type PublicBrowserLogin } from "../lib/browser-login-navigation";
import { parseCompanyChoices, type CompanyChoice } from "../lib/company-choices";
import { workspaceSectionFromPath, workspaceSectionPath, type WorkspaceSection } from "../lib/workspace-sections";
import { SectionHeader, SectionTabs, WorkspaceShell } from "./workspace-shell";
import { WorkspaceOverview } from "./workspace-overview";

type Props = {
  companyId: string;
  companyName: string;
  loginMode?: "local" | "browser";
  browserLogin?: PublicBrowserLogin;
  initialSection?: WorkspaceSection;
  /** Sends a signed-out browser straight to the identity provider instead of showing a second sign-in page. */
  autoStartLogin?: boolean;
};

type ChatMessage = {
  id: string;
  role: "user" | "assistant";
  text: string;
  checkpoint?: LocalAiCheckpoint;
};

type Surface = "overview" | "assistant" | "data-sources" | "members" | "tasks" | "group-inbox" | "customer-requests" | "bot-channels" | "knowledge";

const sectionSurfaces: Record<WorkspaceSection, Surface> = {
  "tong-quan": "overview", "tin-nhan": "group-inbox", "cong-viec": "customer-requests",
  "tro-ly": "assistant", "kien-thuc": "knowledge", "ket-noi": "data-sources",
};
function surfaceSection(surface: Surface): WorkspaceSection {
  if (surface === "tasks") return "cong-viec";
  if (surface === "members" || surface === "bot-channels") return "ket-noi";
  return (Object.keys(sectionSurfaces) as WorkspaceSection[]).find(key => sectionSurfaces[key] === surface) ?? "tong-quan";
}

// Limits automatic redirects so a failing provider callback cannot loop.
const AUTO_LOGIN_KEY = "aioffice-auto-login-at";
function claimAutoLogin() {
  try {
    const last = Number(window.sessionStorage.getItem(AUTO_LOGIN_KEY));
    if (Number.isFinite(last) && Date.now() - last < 60_000) return false;
    window.sessionStorage.setItem(AUTO_LOGIN_KEY, String(Date.now()));
    return true;
  } catch { return false; }
}

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

export function LocalAiWorkspace({ companyId, companyName: initialCompanyName, loginMode = "local", browserLogin,
  initialSection = "tro-ly", autoStartLogin = false }: Props) {
  const initialSurface = useRef(sectionSurfaces[initialSection]);
  const [companies, setCompanies] = useState<readonly CompanyChoice[]>([]);
  const [companyChoiceError, setCompanyChoiceError] = useState("");
  const companyName = companies.find(item => item.companyId.toLowerCase() === companyId.toLowerCase())?.companyName ?? initialCompanyName;
  const [surface, setSurface] = useState<Surface>(initialSurface.current);
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
  const submissionReset = useRef<(() => void) | null>(null), taskRun = useRef<object | null>(null);
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
    submissionReset.current?.(); taskRun.current = null;
    setCompanies([]);
    setCompanyChoiceError("");
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
    setSurface(initialSurface.current);
    setMessages((current) => current.filter((message) => message.id === "welcome"));
  }, []);

  const { phase: sessionState, auth, generation: sessionGeneration, isCurrent, reset, request,
    validate, pause, restore, beginMutation, ready } = useLocalSession(companyId, clearPrivateState);
  const botChannelsAvailable = useBotChannelsAvailable({ companyId, userId: auth?.userId ?? "", generation: sessionGeneration.current,
    isCurrent, validate, request, onUnauthorized: () => reset("signed-out") }, sessionState === "ready" && !!auth?.roles.includes("admin"));
  const submission = useTaskSubmission({ companyId, generation: sessionGeneration, isCurrent, ready, validate, request,
    onUnauthorized: () => reset("signed-out") });
  submissionReset.current = submission.reset;

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

  const loadCompanies = useCallback(async () => {
    if (loginMode !== "browser" || !ready()) return;
    const generation = sessionGeneration.current;
    setCompanies([]); setCompanyChoiceError("");
    try {
      if (!await validate(generation) || !isCurrent(generation)) return;
      const response = await request(generation, `/api/local/companies?companyId=${encodeURIComponent(companyId)}`, { cache: "no-store" });
      const payload = await readJson(response);
      if (!isCurrent(generation) || !await validate(generation) || !isCurrent(generation)) return;
      const items = response.ok ? parseCompanyChoices(payload) : null;
      if (!items || !items.some(item => item.companyId.toLowerCase() === companyId.toLowerCase())) throw new Error();
      setCompanies(items);
    } catch {
      if (isCurrent(generation)) { setCompanies([]); setCompanyChoiceError("Không tải được công ty được cấp quyền. Hãy làm mới để thử lại."); }
    }
  }, [companyId, isCurrent, loginMode, ready, request, sessionGeneration, validate]);

  useEffect(() => { if (sessionState === "ready") void loadCompanies(); }, [sessionState, auth, loadCompanies]);

  async function selectCompany(targetCompanyId: string) {
    if (loginMode !== "browser" || !browserLogin || !ready() || busy || sourceSaving
      || targetCompanyId.toLowerCase() === companyId.toLowerCase()
      || !companies.some(item => item.companyId === targetCompanyId)) return;
    const previous = sessionGeneration.current;
    if (!await validate(previous) || !isCurrent(previous)) return;
    const finish = beginMutation("ready"); if (!finish) return;
    // Clear old chat, source, member and task state before cookie authority
    // changes. Existing OIDC begin/callback fencing rejects stale completions.
    const generation = reset("closing"); let navigating = false;
    try {
      const response = await fetch("/api/local/session/oidc/start", {
        method: "POST", cache: "no-store", redirect: "error", credentials: "same-origin",
        headers: { "Content-Type": "application/json" }, body: JSON.stringify({ companyId: targetCompanyId }),
      });
      const payload = await readJson(response);
      if (!isCurrent(generation)) return;
      const destination = response.ok ? browserLoginDestination(payload, browserLogin) : null;
      if (!destination) throw new Error();
      navigateBrowserLogin(destination); navigating = true;
    } catch { /* Restore verifies whichever issued session is actually current. */ }
    finally {
      finish();
      if (isCurrent(generation) && !navigating) { await restore(); setNotice("Không chuyển được công ty. Hãy thử lại."); }
    }
  }

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

  const signedOutByUser = useRef(false);
  const [autoLoginTried, setAutoLoginTried] = useState(false);
  useEffect(() => {
    if (!autoStartLogin || sessionState !== "signed-out" || !browserLogin || signedOutByUser.current || autoLoginTried) return;
    setAutoLoginTried(true);
    if (claimAutoLogin()) void startBrowserLogin();
    // eslint-disable-next-line react-hooks/exhaustive-deps -- runs once when the first session check finds no session.
  }, [autoStartLogin, autoLoginTried, browserLogin, sessionState]);

  // Keep the address bar on the current section so links and reloads land on the same screen.
  const firstSurface = useRef(true);
  useEffect(() => {
    const path = workspaceSectionPath(surfaceSection(surface));
    if (window.location.pathname !== path) {
      if (firstSurface.current) window.history.replaceState(null, "", path);
      else window.history.pushState(null, "", path);
    }
    firstSurface.current = false;
  }, [surface]);
  useEffect(() => {
    const onPop = () => {
      const section = workspaceSectionFromPath(window.location.pathname);
      if (section) setSurface(current => surfaceSection(current) === section ? current : sectionSurfaces[section]);
    };
    window.addEventListener("popstate", onPop);
    return () => window.removeEventListener("popstate", onPop);
  }, []);

  async function startBrowserLogin() {
    if (sessionState !== "signed-out" || loginMode !== "browser") return;
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
  }

  async function signIn(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (sessionState !== "signed-out") return;
    if (loginMode === "browser") {
      await startBrowserLogin();
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
    signedOutByUser.current = true;
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
    if (busy || taskRun.current || !ready()) return;

    const form = event.currentTarget;
    const data = new FormData(form);
    const question = trimSubmissionQuestion(String(data.get("question") ?? ""));

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

    // Capture and lock before any await. Source/form changes cannot alter retries.
    const attempt = submission.begin(selectedSourceId, question);
    if (!attempt) { setNotice("Kiểm tra yêu cầu đang giữ trước khi tạo yêu cầu khác; câu hỏi cần có nội dung hợp lệ."); return; }

    const userMessage: ChatMessage = {
      id: crypto.randomUUID(),
      role: "user",
      text: question,
    };
    setMessages((current) => [...current, userMessage]);
    form.reset();
    await executeSubmission(attempt);
  }

  async function retrySubmission() {
    if (taskRun.current || !ready()) return;
    const attempt = submission.retry(); if (attempt) await executeSubmission(attempt);
  }

  async function executeSubmission(attempt: NonNullable<ReturnType<typeof submission.begin>>) {
    const generation = attempt.generation, run = {}; taskRun.current = run;
    setBusy(true);
    setNotice("");
    setTaskStage("Đang xác nhận yêu cầu…");

    try {
      const accepted = await submission.send(attempt);
      if (!isCurrent(generation) || taskRun.current !== run) return;
      if (!accepted) { setTaskStage(""); return; }

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
        if (!snapshot || snapshot.taskId.toLowerCase() !== accepted.taskId.toLowerCase()) continue;

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
    } catch {
      if (!isCurrent(generation)) return;
      setNotice("Yêu cầu đã được nhận. Chưa đọc được kết quả tại đây; mở Công việc để xem trạng thái đã lưu.");
      setTaskStage("");
    } finally {
      if (taskRun.current === run) { taskRun.current = null; if (isCurrent(generation)) setBusy(false); }
    }
  }

  if (sessionState === "checking" || sessionState === "closing" || (auth && auth.companyId !== companyId)) {
    return (
      <main className="flex min-h-screen items-center justify-center bg-background px-6 text-foreground">
        <div className="rounded-xl border bg-card p-8 shadow-sm">
          <p className="text-xs font-semibold tracking-[0.2em] text-primary">MINH HUY AI OFFICE</p>
          <h1 className="mt-3 text-xl font-semibold">Đang kiểm tra phiên đăng nhập…</h1>
        </div>
      </main>
    );
  }

  if (sessionState === "signed-out") {
    const redirecting = loginMode === "browser" && (busy || autoStartLogin && !!browserLogin && !signedOutByUser.current && !autoLoginTried);
    return (
      <main className="flex min-h-screen items-center justify-center bg-background px-4 py-10 text-foreground">
        <form onSubmit={signIn} className="w-full max-w-sm rounded-xl border bg-card p-7 shadow-sm">
          <div className="flex items-center gap-3">
            <div className="flex size-10 items-center justify-center rounded-lg bg-primary text-sm font-bold text-primary-foreground">AI</div>
            <div>
              <h1 className="text-lg font-semibold leading-tight">AI Office</h1>
              <p className="text-sm text-muted-foreground">{companyName}</p>
            </div>
            {loginMode === "local" ? <span className="ml-auto rounded-full bg-accent px-2.5 py-1 text-xs font-medium text-accent-foreground">Local Pilot</span> : null}
          </div>
          {loginMode === "browser" ? <p className="mt-6 text-sm leading-6 text-muted-foreground">
            {redirecting ? "Đang chuyển đến trang đăng nhập…" : signedOutByUser.current ? "Bạn đã đăng xuất." : "Đăng nhập bằng tài khoản công ty để tiếp tục."}
          </p> : <>
            <label className="mt-6 block text-sm font-medium">
              Tên đăng nhập
              <input name="username" defaultValue="pilot-admin" autoComplete="username"
                className="mt-1.5 w-full rounded-lg border bg-background px-3 py-2 outline-none focus:border-ring" />
            </label>
            <label className="mt-4 block text-sm font-medium">
              Mật khẩu
              <input name="password" type="password" autoComplete="current-password"
                className="mt-1.5 w-full rounded-lg border bg-background px-3 py-2 outline-none focus:border-ring" />
            </label>
          </>}
          {notice ? <p role="alert" className="mt-4 rounded-lg bg-rose-50 px-3 py-2 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-200">{notice}</p> : null}
          <button type="submit" disabled={busy}
            className="mt-6 w-full rounded-lg bg-primary px-4 py-2.5 text-sm font-semibold text-primary-foreground hover:bg-primary/90 disabled:cursor-not-allowed disabled:opacity-50">
            {busy ? "Đang đăng nhập…" : loginMode === "browser" ? "Đăng nhập doanh nghiệp" : "Vào AI Office"}
          </button>
          {loginMode === "local" ? <p className="mt-4 text-xs leading-5 text-muted-foreground">Phiên local chỉ đọc. Token được giữ trong cookie HttpOnly, không hiển thị cho JavaScript.</p> : null}
        </form>
      </main>
    );
  }

  const isAdmin = !!auth?.roles.includes("admin");
  const onUnauthorized = () => reset("signed-out");
  const section = surfaceSection(surface);
  const scopeKey = `${companyId}:${auth?.userId ?? ""}:${sessionGeneration.current}`;
  const connectionTabs = [
    ...(botChannelsAvailable ? [["bot-channels", "Bot Zalo & WhatsApp"] as const] : []),
    ["data-sources", "Nguồn dữ liệu"] as const,
    ...(isAdmin ? [["members", "Thành viên"] as const] : []),
  ];

  let content: ReactNode = null;
  if (section === "tong-quan") {
    content = <WorkspaceOverview companyName={companyName} onOpen={next => setSurface(sectionSurfaces[next])} />;
  } else if (section === "tin-nhan") {
    content = <>
      <SectionHeader title="Tin nhắn" description="Tin nhắn từ các nhóm khách hàng mà bot đang theo dõi." />
      {auth ? <GroupInboxPanel key={scopeKey} tenantId={auth.tenantId} companyId={companyId} userId={auth.userId} generation={sessionGeneration.current}
        isCurrent={isCurrent} validate={validate} request={request} onUnauthorized={onUnauthorized} /> : null}
    </>;
  } else if (section === "cong-viec") {
    content = <>
      <SectionHeader title="Công việc" description="Yêu cầu AI tổng hợp từ tin nhắn khách và việc đã giao cho trợ lý." />
      <SectionTabs label="Loại công việc" value={surface === "tasks" ? "tasks" : "customer-requests"} onChange={setSurface}
        tabs={[["customer-requests", "Yêu cầu khách hàng"], ["tasks", "Việc giao cho AI"]]} />
      {surface === "tasks" && auth ? <div key={scopeKey} className="space-y-5">
        <SubmissionRecoveryPanel companyId={companyId} userId={auth.userId} generation={sessionGeneration.current} disabled={busy || submission.working}
          isCurrent={isCurrent} validate={validate} request={request} onUnauthorized={onUnauthorized}
          onResume={intent => { if (taskRun.current || !submission.resume(intent)) return false; setNotice(""); setSurface("assistant"); return true; }} />
        <TaskHistoryPanel companyId={companyId} userId={auth.userId} generation={sessionGeneration.current}
          isCurrent={isCurrent} validate={validate} request={request} onUnauthorized={onUnauthorized} />
      </div> : auth ? <CustomerRequestPanel key={scopeKey} tenantId={auth.tenantId} companyId={companyId} userId={auth.userId} generation={sessionGeneration.current}
        isCurrent={isCurrent} validate={validate} request={request} onUnauthorized={onUnauthorized} /> : null}
    </>;
  } else if (section === "kien-thuc") {
    content = <>
      <SectionHeader title="Kiến thức" description="Tài liệu khách hàng, quy trình xử lý và ghi chú gắn với công việc." />
      <div className="rounded-xl border border-dashed bg-card p-8 text-center text-sm text-muted-foreground">Mục này đang được xây dựng.</div>
    </>;
  } else if (section === "ket-noi") {
    const tab = connectionTabs.some(([key]) => key === surface) ? surface : "data-sources";
    content = <>
      <SectionHeader title="Kết nối" description="Bot nhắn tin, nguồn dữ liệu ERP và thành viên được dùng AI Office." />
      <SectionTabs label="Loại kết nối" value={tab} onChange={setSurface} tabs={connectionTabs} />
      {tab === "bot-channels" && botChannelsAvailable && auth ? <BotChannelPanel key={scopeKey}
        companyId={companyId} userId={auth.userId} generation={sessionGeneration.current} isCurrent={isCurrent} validate={validate} request={request}
        onUnauthorized={onUnauthorized} />
      : tab === "members" && isAdmin && auth ? <CompanyMemberPanel key={`${companyId}:${sessionGeneration.current}`} companyId={companyId} userId={auth.userId}
        generation={sessionGeneration.current} isCurrent={isCurrent} validate={validate} request={request} onUnauthorized={onUnauthorized} />
      : (
              <section className="rounded-xl border bg-card p-5 shadow-sm sm:p-7">
                <div className="flex flex-wrap items-center justify-between gap-4">
                  <div>
                    <h2 className="text-lg font-semibold">Nguồn dữ liệu</h2>
                    <p className="mt-2 text-sm text-muted-foreground">Quản lý và kiểm tra các nguồn ERP của công ty.</p>
                  </div>
                  <button type="button" onClick={() => void loadSources()} disabled={sourceLoading || sourceSaving} className="rounded-xl border px-4 py-2 text-sm font-medium">
                    {sourceLoading ? "Đang tải…" : "Làm mới"}
                  </button>
                  {auth?.roles.includes("admin") && !addingSource ? (
                    <button type="button" disabled={sourceLoading || sourceSaving} onClick={() => { setEditingSource(null); setAddingSource(true); }} className="rounded-xl bg-primary px-4 py-2 text-sm font-medium text-primary-foreground">Thêm nguồn chỉ đọc</button>
                  ) : null}
                </div>
                {auth?.roles.includes("admin") ? <CustomerErpPanel key={`customer-erp:${companyId}:${sessionGeneration.current}`} companyId={companyId} generation={sessionGeneration.current}
                  isCurrent={isCurrent} validate={validate} request={request} onUnauthorized={() => reset("signed-out")} onSaved={() => void loadSources()} /> : null}
                {addingSource && auth?.roles.includes("admin") ? (
                  <SourceRegistrationPanel key={`${companyId}:${sessionGeneration.current}`} companyId={companyId} generation={sessionGeneration.current}
                    isCurrent={isCurrent} validate={validate} request={request} onCancel={() => setAddingSource(false)}
                    onUnauthorized={() => reset("signed-out")}
                    onRegistered={fresh => { setSources(fresh); setSelectedSourceId(current => taskSourceSelection(fresh, current)); setAddingSource(false); setNotice("Đã đăng ký nguồn chỉ đọc và xác nhận lại danh sách."); }} />
                ) : null}
                {notice ? <p role="status" className="mt-4 text-sm text-accent-foreground">{notice}</p> : null}
                {!editingSource && editorError ? <p role="alert" className="mt-4 text-sm text-rose-600 dark:text-rose-300">{editorError}</p> : null}
                <div className="mt-6 grid gap-4 xl:grid-cols-2">
                  {sources.map((source) => (
                    <article key={source.id} className="rounded-2xl border p-5">
                      <div className="flex items-start justify-between gap-4">
                        <div>
                          <h3 className="font-semibold">{source.logicalName}</h3>
                          <p className="mt-1 text-sm text-muted-foreground">{source.purpose}</p>
                        </div>
                        <span className={`rounded-full px-2.5 py-1 text-xs font-semibold ${source.allowRead && !source.allowWrite ? "bg-emerald-50 text-emerald-700 dark:bg-emerald-400/10 dark:text-emerald-200" : "bg-amber-50 text-amber-700 dark:bg-amber-400/10 dark:text-amber-200"}`}>
                          {sourceLabel(source)}
                        </span>
                      </div>
                      <dl className="mt-5 grid grid-cols-3 gap-3 text-sm">
                        <div><dt className="text-xs text-muted-foreground">Loại</dt><dd className="mt-1 font-medium">{source.kind}</dd></div>
                        <div><dt className="text-xs text-muted-foreground">Môi trường</dt><dd className="mt-1 font-medium">{source.environment}</dd></div>
                        <div><dt className="text-xs text-muted-foreground">Đồng thời</dt><dd className="mt-1 font-medium">{source.maxConcurrency}</dd></div>
                      </dl>
                      <div className="mt-5 flex items-center justify-between gap-3 rounded-xl bg-muted p-3">
                        <span className="text-sm">{connectionState[source.id] ?? "Chưa kiểm tra trong phiên này"}</span>
                        <button type="button" onClick={() => void testConnection(source)} className="rounded-lg border bg-card px-3 py-2 text-xs font-semibold hover:bg-muted">
                          Kiểm tra
                        </button>
                      </div>
                      {auth?.roles.includes("admin") && editingSource?.id !== source.id ? (
                        <button type="button" disabled={sourceLoading || sourceSaving} onClick={() => void editSource(source)} className="mt-4 rounded-lg border px-3 py-2 text-sm font-medium disabled:opacity-50">Chỉnh sửa</button>
                      ) : null}
                      {editingSource?.id === source.id ? (
                        <SourceMetadataEditor source={editingSource} saving={sourceSaving} error={editorError} onSave={draft => void saveSource(draft)} onCancel={() => { setEditingSource(null); setEditorError(""); }} />
                      ) : null}
                    </article>
                  ))}
                </div>
                {!sourceLoading && sources.length === 0 ? <p className="mt-6 rounded-xl bg-muted p-5 text-sm text-muted-foreground">Chưa có nguồn dữ liệu được cấp quyền.</p> : null}
              </section>
      )}
    </>;
  } else {
    content = (
              <div className="grid gap-5 xl:grid-cols-[minmax(0,1fr)_330px]">
                <section className="flex min-h-[calc(100vh-8rem)] flex-col overflow-hidden rounded-xl border bg-card shadow-sm">
                  <div className="border-b px-5 py-4 sm:px-6">
                    <div className="flex flex-wrap items-center justify-between gap-3">
                      <div>
                        <h2 className="text-lg font-semibold">Trợ lý AI</h2>
                        <p className="mt-1 text-sm text-muted-foreground">
                          {selectedSource ? `Nguồn: ${selectedSource.logicalName}` : "Chưa có nguồn dữ liệu"}
                        </p>
                      </div>
                      <select
                        aria-label="Nguồn dữ liệu"
                        value={selectedSourceId}
                        onChange={(event) => setSelectedSourceId(event.target.value)}
                        className="max-w-xs rounded-xl border bg-card px-3 py-2 text-sm"
                      >
                        <option value="">Chọn nguồn dữ liệu</option>
                        {readOnlySources.map((source) => <option key={source.id} value={source.id}>{source.logicalName}</option>)}
                      </select>
                    </div>
                  </div>

                  <div className="flex-1 space-y-5 overflow-y-auto px-4 py-6 sm:px-6">
                    {messages.map((message) => (
                      <article key={message.id} className={`flex ${message.role === "user" ? "justify-end" : "justify-start"}`}>
                        <div className={`max-w-[88%] rounded-2xl px-4 py-3 sm:max-w-[78%] ${message.role === "user" ? "bg-primary text-primary-foreground" : "bg-muted text-foreground"}`}>
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
                        <div className="rounded-2xl bg-accent px-4 py-3 text-sm text-accent-foreground">{taskStage}</div>
                      </div>
                    ) : null}
                  </div>

                  <form onSubmit={askAi} className="border-t p-4 sm:p-5">
                    {notice ? <p role="status" className="mb-3 text-sm text-rose-600 dark:text-rose-300">{notice}</p> : null}
                    {submission.pending ? <div aria-label="Yêu cầu đang giữ" className="mb-4 rounded-xl border border-ring/40 bg-accent/40 p-3 text-sm">
                      {!messages.some(message => message.role === "user" && message.text === submission.pending!.input.question)
                        ? <p className="whitespace-pre-wrap break-words">{submission.pending.input.question}</p> : null}
                      <p role="status" className="mt-2">{submission.pending.notice}</p>
                      <div className="mt-3 flex flex-wrap gap-2">
                        {["prepared", "unknown", "denied"].includes(submission.pending.phase) ? <>
                          <button type="button" disabled={busy || submission.working} onClick={() => void retrySubmission()} className="rounded-lg border px-3 py-2 disabled:opacity-50">{submission.pending.phase === "prepared" ? "Gửi yêu cầu đã lưu" : "Thử lại đúng yêu cầu"}</button>
                          <button type="button" disabled={busy || submission.working} onClick={() => void submission.reconcile()} className="rounded-lg border px-3 py-2 disabled:opacity-50">Kiểm tra trạng thái yêu cầu</button>
                        </> : null}
                        <button type="button" onClick={() => setSurface("tasks")} className="rounded-lg border px-3 py-2">Mở Công việc và yêu cầu đã lưu</button>
                      </div>
                      {submission.pending.phase !== "accepted" ? <>
                        <p className="mt-3 text-amber-700 dark:text-amber-300">Yêu cầu trước có thể đã được nhận; tạo yêu cầu khác có thể xử lý thêm một lần. Yêu cầu đã lưu vẫn được giữ trên hệ thống.</p>
                        <button type="button" disabled={busy || submission.working} onClick={() => { if (submission.newOperation()) setNotice(""); }} className="mt-2 rounded-lg border px-3 py-2 disabled:opacity-50">Tạo yêu cầu khác</button>
                      </> : null}
                    </div> : null}
                    <div className="flex items-end gap-3 rounded-2xl border bg-muted p-2 focus-within:border-ring">
                      <textarea
                        name="question"
                        rows={2}
                        disabled={busy || submission.working || !!submission.pending && submission.pending.phase !== "accepted"}
                        placeholder="Hỏi AI Office về nguồn dữ liệu đã chọn…"
                        className="min-h-12 flex-1 resize-none bg-transparent px-3 py-2 text-sm outline-none placeholder:text-muted-foreground"
                      />
                      <button type="submit" disabled={busy || submission.working || !selectedSourceId || !!submission.pending && submission.pending.phase !== "accepted"} className="rounded-xl bg-primary px-5 py-3 text-sm font-semibold text-primary-foreground hover:bg-primary/90 disabled:cursor-not-allowed disabled:opacity-40">
                        Gửi
                      </button>
                    </div>
                    <p className="mt-2 px-1 text-xs text-muted-foreground">Câu trả lời dựa trên nguồn dữ liệu bạn được cấp quyền.</p>
                  </form>
                </section>

                <aside className="space-y-5">
                  <section className="rounded-xl border bg-card p-5 shadow-sm">
                    <p className="text-xs font-semibold uppercase tracking-[0.16em] text-muted-foreground">Nguồn đang dùng</p>
                    {selectedSource ? (
                      <>
                        <h3 className="mt-3 font-semibold">{selectedSource.logicalName}</h3>
                        <p className="mt-1 text-sm leading-6 text-muted-foreground">{selectedSource.purpose}</p>
                        <dl className="mt-5 grid grid-cols-2 gap-3 text-sm">
                          <div className="rounded-xl bg-muted p-3"><dt className="text-xs text-muted-foreground">Quyền</dt><dd className="mt-1 font-medium">{sourceLabel(selectedSource)}</dd></div>
                          <div className="rounded-xl bg-muted p-3"><dt className="text-xs text-muted-foreground">Đồng thời</dt><dd className="mt-1 font-medium">{selectedSource.maxConcurrency}</dd></div>
                        </dl>
                      </>
                    ) : <p className="mt-3 text-sm text-muted-foreground">Chưa có nguồn read-only khả dụng.</p>}
                  </section>

                  {messages.slice().reverse().find((message) => message.checkpoint)?.checkpoint ? (() => {
                    const checkpoint = messages.slice().reverse().find((message) => message.checkpoint)!.checkpoint!;
                    return (
                      <section className="rounded-xl border bg-card p-5 shadow-sm">
                        <p className="text-xs font-semibold uppercase tracking-[0.16em] text-muted-foreground">Evidence gần nhất</p>
                        <dl className="mt-4 space-y-3 text-sm">
                          <div><dt className="text-muted-foreground">Database</dt><dd className="mt-1 font-medium">{checkpoint.erpDatabase ?? "—"}</dd></div>
                          <div><dt className="text-muted-foreground">Số bảng</dt><dd className="mt-1 font-medium">{checkpoint.erpTableCount ?? "—"}</dd></div>
                          <div><dt className="text-muted-foreground">Model</dt><dd className="mt-1 font-medium">{checkpoint.model}</dd></div>
                          <div><dt className="text-muted-foreground">Token</dt><dd className="mt-1 font-medium">{checkpoint.inputTokens} in · {checkpoint.outputTokens} out</dd></div>
                        </dl>
                      </section>
                    );
                  })() : null}
                </aside>
              </div>
    );
  }

  return (
    <WorkspaceShell section={section} onSection={next => setSurface(sectionSurfaces[next])} companyId={companyId} companyName={companyName}
      companies={loginMode === "browser" ? companies : []} companyDisabled={busy || sourceSaving} onSelectCompany={id => void selectCompany(id)}
      roleLabel={auth?.roles.join(", ") || "member"} accountLabel={loginMode === "browser" ? "Tài khoản doanh nghiệp" : "Local Pilot · chỉ đọc"}
      onSignOut={() => void signOut()}>
      <div key={`${companyId}:${sessionGeneration.current}`}>
        {companyChoiceError ? <div role="alert" className="mb-4 rounded-lg border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900 dark:border-amber-400/30 dark:bg-amber-400/10 dark:text-amber-100">
          {companyChoiceError} <button type="button" onClick={() => void loadCompanies()} className="ml-2 underline">Làm mới công ty</button>
        </div> : null}
        {content}
      </div>
    </WorkspaceShell>
  );
}
