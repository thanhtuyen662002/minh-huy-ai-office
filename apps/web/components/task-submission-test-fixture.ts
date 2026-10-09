import { submissionFingerprint, type SubmissionInput, type SubmissionIntent } from "../lib/task-submission-intent";

/** Synthetic HTTP responses for retained session/privacy tests. No mocked session hook. */
export function ownedSubmissionFixture(companyId: string, taskId: string) {
  const stored = new Map<string, SubmissionIntent>();
  return async (url: string, init?: RequestInit): Promise<Response | undefined> => {
    const path = new URL(url, "http://fixture.invalid").pathname;
    if (!path.startsWith("/api/local/tasks/intents")) return undefined;
    if (path === "/api/local/tasks/intents" && init?.method === "POST") {
      const input: SubmissionInput = JSON.parse(init.body as string);
      if (Object.keys(input).sort().join(",") !== "dataSourceId,operationId,question") throw new Error("Unexpected prepare input");
      const previous = stored.get(input.operationId);
      if (previous) {
        if (previous.question !== input.question || previous.dataSourceId !== input.dataSourceId) throw new Error("Immutable fixture input conflict");
        return Response.json(previous);
      }
      const value: SubmissionIntent = { companyId, ...input, state: 0, inputFingerprint: await submissionFingerprint(input),
        createdAtUtc: "2026-10-09T08:00:00Z", expiresAtUtc: "2026-10-10T08:00:00Z", accepted: null };
      stored.set(input.operationId, value); return Response.json(value);
    }
    if (path === "/api/local/tasks/intents") return Response.json({ companyId, items: [...stored.values()], offset: 0, limit: 25, hasMore: false });
    const operationId = path.split("/")[5], value = stored.get(operationId);
    if (!value) return new Response(null, { status: 404 });
    if (!path.endsWith("/submit")) return Response.json(value);
    const body = JSON.parse(init!.body as string);
    if (init?.method !== "POST" || Object.keys(body).join(",") !== "inputFingerprint" || body.inputFingerprint !== value.inputFingerprint) throw new Error("Unexpected submit input");
    const accepted = { companyId, operationId, dataSourceId: value.dataSourceId!, inputFingerprint: value.inputFingerprint!, taskId,
      stepId: "77777777-7777-4777-8777-777777777777", messageId: "88888888-8888-4888-8888-888888888888", status: 0, dispatchState: 0, createdAtUtc: "2026-10-09T08:00:00Z" };
    stored.set(operationId, { ...value, state: 1, accepted }); return Response.json(accepted, { status: 202 });
  };
}
