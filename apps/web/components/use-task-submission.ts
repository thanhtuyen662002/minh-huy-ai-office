"use client";
import { useCallback, useEffect, useRef, useState, type RefObject } from "react";
import { readBoundedRequestJson } from "../lib/bounded-request-json";
import { parseSubmissionInput, parseSubmissionIntent, parseSubmissionJson, parseSubmissionReceipt, receiptMatches, submissionFingerprint,
  verifiedSubmissionIntent, type SubmissionInput, type SubmissionIntent, type SubmissionReceipt } from "../lib/task-submission-intent";

export type SubmissionGuards = { companyId: string; generation: RefObject<number>; isCurrent: (generation: number) => boolean;
  ready: () => boolean; validate: (generation: number) => Promise<boolean>;
  request: (generation: number, url: string, init?: RequestInit) => Promise<Response>; onUnauthorized: () => void };
export type PendingSubmission = Readonly<{ input: SubmissionInput; fingerprint: string | null; prepared: boolean;
  phase: "preparing" | "prepared" | "unknown" | "denied" | "accepted" | "expired" | "unavailable";
  receipt: SubmissionReceipt | null; notice: string }>;
type Attempt = Readonly<{ generation: number; serial: number; input: SubmissionInput; controller: AbortController }>;
const unknownNotice = "Chưa xác nhận hệ thống đã nhận yêu cầu. Kiểm tra trạng thái hoặc thử lại đúng yêu cầu này.";

export async function readSubmissionPayload(response: Response, signal: AbortSignal, maximum = 65536): Promise<unknown> {
  const received = await readBoundedRequestJson(new Request("http://bounded-response.invalid", { method: "POST", body: response.body,
    signal, headers: { "Content-Type": response.headers.get("Content-Type") ?? "" }, duplex: "half" } as RequestInit), maximum, parseSubmissionJson);
  if (!received.ok) throw new Error("Submission response could not be verified");
  return received.value;
}

/** One immutable operation, synchronously acquired before the first await. No browser storage. */
export function useTaskSubmission({ companyId, generation, isCurrent, ready, validate, request, onUnauthorized }: SubmissionGuards) {
  const [pending, setPending] = useState<PendingSubmission | null>(null), [working, setWorking] = useState(false);
  const stored = useRef<PendingSubmission | null>(null), lock = useRef<Attempt | null>(null), serial = useRef(0), mounted = useRef(true);
  const consumed = useRef(new WeakSet<Attempt>());
  const save = useCallback((value: PendingSubmission | null) => { stored.current = value; setPending(value); }, []);
  const reset = useCallback(() => {
    serial.current++; lock.current?.controller.abort(); lock.current = null; stored.current = null;
    if (mounted.current) { setPending(null); setWorking(false); }
  }, []);
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; serial.current++; lock.current?.controller.abort(); lock.current = null; stored.current = null; }; }, []);

  const current = (attempt: Attempt) => mounted.current && lock.current === attempt && attempt.serial === serial.current && isCurrent(attempt.generation);
  function acquire(input: SubmissionInput): Attempt | null {
    if (!mounted.current || lock.current || !ready()) return null;
    const attempt = Object.freeze({ input, generation: generation.current, serial: ++serial.current, controller: new AbortController() });
    lock.current = attempt; setWorking(true); return attempt;
  }
  function begin(sourceId: string, question: string): Attempt | null {
    if (lock.current || !ready() || stored.current && stored.current.phase !== "accepted") return null;
    const input = parseSubmissionInput({ operationId: crypto.randomUUID(), dataSourceId: sourceId, question });
    if (!input) return null;
    const attempt = acquire(input); if (!attempt) return null;
    save({ input, fingerprint: null, prepared: false, phase: "preparing", receipt: null, notice: "Đang lưu yêu cầu; công việc chưa được tạo." });
    return attempt;
  }
  function retry(): Attempt | null {
    const value = stored.current;
    if (!value || ["accepted", "expired", "unavailable"].includes(value.phase)) return null;
    return acquire(value.input);
  }
  function newOperation(): boolean {
    if (lock.current || !ready()) return false;
    serial.current++; save(null); return true;
  }
  function resume(value: SubmissionIntent): boolean {
    if (lock.current || !ready()) return false;
    const intent = parseSubmissionIntent(value);
    if (!intent || intent.state === 3 || intent.companyId.toLowerCase() !== companyId.toLowerCase()) return false;
    if (stored.current && stored.current.phase !== "accepted" && stored.current.input.operationId.toLowerCase() !== intent.operationId.toLowerCase()) return false;
    const input = parseSubmissionInput({ operationId: intent.operationId, dataSourceId: intent.dataSourceId, question: intent.question });
    if (!input) return false;
    save({ input, fingerprint: intent.inputFingerprint, prepared: true, phase: intent.state === 1 ? "accepted" : intent.state === 2 ? "expired" : "prepared",
      receipt: intent.accepted, notice: intent.state === 1 ? "Hệ thống đã nhận yêu cầu. Mở Công việc để xem kết quả đã lưu."
        : intent.state === 2 ? "Yêu cầu chưa gửi đã hết hạn. Bạn có thể chủ động tạo yêu cầu khác." : "Yêu cầu đã lưu, chưa vào hàng đợi. Bấm Gửi yêu cầu đã lưu để tiếp tục." });
    return true;
  }
  async function confirm(attempt: Attempt) { return current(attempt) && await validate(attempt.generation) && current(attempt); }
  async function receive(attempt: Attempt, path: string, init?: RequestInit): Promise<{ response: Response; payload: unknown } | null> {
    const signal = AbortSignal.any([attempt.controller.signal, AbortSignal.timeout(10000)]);
    const response = await request(attempt.generation, path, { ...init, cache: "no-store", signal });
    if (!current(attempt)) return null;
    if (response.status === 401) { onUnauthorized(); return null; }
    const payload = response.status === 404 ? null : await readSubmissionPayload(response, signal);
    if (!await confirm(attempt)) return null;
    return { response, payload };
  }
  function markFailure(attempt: Attempt, status: number, value: unknown) {
    if (!current(attempt) || !stored.current) return;
    const code = value && typeof value === "object" && Object.hasOwn(value, "code") ? (value as { code: unknown }).code : null;
    const phase = status === 403 ? "denied" : code === "intent-expired" ? "expired" : code === "intent-unavailable" || code === "operation-conflict" ? "unavailable" : "unknown";
    const notice = status === 403 ? "Quyền truy cập đã thay đổi. Kiểm tra trạng thái đã lưu; yêu cầu trước có thể đã được nhận."
      : code === "intent-expired" ? "Yêu cầu chưa gửi đã hết hạn. Bạn có thể chủ động tạo yêu cầu khác."
      : code === "intent-limit" ? "Đã có nhiều yêu cầu chưa giải quyết. Kiểm tra các yêu cầu đã lưu trước khi tạo thêm."
      : phase === "unavailable" ? "Yêu cầu đã lưu cần được kiểm tra. Không gửi nội dung thay thế bằng thao tác này." : unknownNotice;
    save({ ...stored.current, phase, notice });
  }

  async function send(attempt: Attempt): Promise<SubmissionReceipt | null> {
    if (!current(attempt) || !stored.current || stored.current.input !== attempt.input || consumed.current.has(attempt)) return null;
    consumed.current.add(attempt);
    try {
      const fingerprint = await submissionFingerprint(attempt.input);
      if (!await confirm(attempt) || !stored.current) return null;
      if (stored.current.fingerprint && stored.current.fingerprint !== fingerprint) throw new Error("Immutable input changed");
      save({ ...stored.current, fingerprint });
      if (!stored.current!.prepared) {
        const prepared = await receive(attempt, `/api/local/tasks/intents?companyId=${encodeURIComponent(companyId)}`, {
          method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(attempt.input) });
        if (!prepared) return null;
        if (!prepared.response.ok) { markFailure(attempt, prepared.response.status, prepared.payload); return null; }
        const intent = await verifiedSubmissionIntent(prepared.payload);
        if (!current(attempt)) return null;
        if (!intent || intent.companyId.toLowerCase() !== companyId.toLowerCase() || intent.operationId.toLowerCase() !== attempt.input.operationId
          || intent.dataSourceId?.toLowerCase() !== attempt.input.dataSourceId || intent.question !== attempt.input.question || intent.inputFingerprint !== fingerprint)
          throw new Error("Invalid prepared receipt");
        if (intent.state === 2 || intent.state === 3) { markFailure(attempt, 409, { code: intent.state === 2 ? "intent-expired" : "intent-unavailable" }); return null; }
        save({ input: attempt.input, fingerprint, prepared: true, phase: "prepared", receipt: intent.accepted, notice: "Yêu cầu đã lưu. Đang xác nhận gửi…" });
      }
      if (!await confirm(attempt)) return null;
      const result = await receive(attempt, `/api/local/tasks/intents/${attempt.input.operationId}/submit?companyId=${encodeURIComponent(companyId)}`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ inputFingerprint: fingerprint }) });
      if (!result) return null;
      if (!result.response.ok) { markFailure(attempt, result.response.status, result.payload); return null; }
      const receipt = parseSubmissionReceipt(result.payload);
      if (result.response.status !== 202 || !receipt || !receiptMatches(receipt, companyId, attempt.input, fingerprint, stored.current?.receipt))
        throw new Error("Invalid accepted receipt");
      if (!current(attempt)) return null;
      save({ input: attempt.input, fingerprint, prepared: true, phase: "accepted", receipt, notice: "Hệ thống đã nhận yêu cầu. Mở Công việc để xem tiến độ và kết quả đã lưu." });
      return receipt;
    } catch {
      if (current(attempt) && stored.current) save({ ...stored.current, phase: "unknown", notice: unknownNotice });
      return null;
    } finally { if (lock.current === attempt) { lock.current = null; if (mounted.current) setWorking(false); } }
  }

  async function reconcile(): Promise<void> {
    const value = stored.current; if (!value) return;
    const attempt = acquire(value.input); if (!attempt) return;
    try {
      if (!await confirm(attempt)) return;
      const received = await receive(attempt, `/api/local/tasks/intents/${attempt.input.operationId}?companyId=${encodeURIComponent(companyId)}`);
      if (!received) return;
      if (!received.response.ok) { markFailure(attempt, received.response.status, received.payload); return; }
      const intent = await verifiedSubmissionIntent(received.payload);
      if (!current(attempt)) return;
      if (!intent || intent.companyId.toLowerCase() !== companyId.toLowerCase() || intent.operationId.toLowerCase() !== attempt.input.operationId
        || intent.state !== 3 && (intent.question !== attempt.input.question || intent.dataSourceId?.toLowerCase() !== attempt.input.dataSourceId
          || intent.inputFingerprint !== await submissionFingerprint(attempt.input))) throw new Error("Invalid recovery");
      if (!await confirm(attempt)) return;
      if (intent.state === 3) { markFailure(attempt, 409, { code: "intent-unavailable" }); return; }
      save({ input: attempt.input, fingerprint: intent.inputFingerprint, prepared: true,
        phase: intent.state === 1 ? "accepted" : intent.state === 2 ? "expired" : "prepared", receipt: intent.accepted,
        notice: intent.state === 1 ? "Hệ thống đã nhận yêu cầu. Mở Công việc để xem kết quả đã lưu."
          : intent.state === 2 ? "Yêu cầu chưa gửi đã hết hạn. Bạn có thể chủ động tạo yêu cầu khác." : "Yêu cầu đã lưu, chưa vào hàng đợi. Bấm Gửi yêu cầu đã lưu để tiếp tục." });
    } catch { if (current(attempt) && stored.current) save({ ...stored.current, phase: "unknown", notice: unknownNotice }); }
    finally { if (lock.current === attempt) { lock.current = null; if (mounted.current) setWorking(false); } }
  }
  return { pending, working, begin, retry, send, resume, reconcile, newOperation, reset };
}
