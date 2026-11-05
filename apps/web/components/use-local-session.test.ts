import { act, renderHook, waitFor } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import { SESSION_CONFIRMATION_MS, useLocalSession } from "./use-local-session";

const companyId = "22222222-2222-4222-8222-222222222222";
const context = { tenantId: "11111111-1111-4111-8111-111111111111", companyId, userId: "33333333-3333-4333-8333-333333333333", roles: ["member"] };

afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });

async function signedIn() {
  const fetch = vi.fn(async () => Response.json(context));
  vi.stubGlobal("fetch", fetch);
  const clear = () => {};
  const hook = renderHook(() => useLocalSession(companyId, clear));
  await waitFor(() => expect(hook.result.current.phase).toBe("ready"));
  fetch.mockClear();
  return { hook, fetch };
}

it("answers read-only checks from a recent confirmation and shares one request after it expires", async () => {
  const { hook, fetch } = await signedIn();
  const generation = hook.result.current.generation.current;
  await act(async () => {
    expect(await Promise.all([1, 2, 3].map(() => hook.result.current.validateRecent(generation)))).toEqual([true, true, true]);
  });
  expect(fetch).not.toHaveBeenCalled();
  vi.spyOn(Date, "now").mockReturnValue(Date.now() + SESSION_CONFIRMATION_MS + 1);
  await act(async () => {
    expect(await Promise.all([1, 2, 3].map(() => hook.result.current.validateRecent(generation)))).toEqual([true, true, true]);
  });
  expect(fetch).toHaveBeenCalledTimes(1);
});

it("keeps validate authoritative and never reuses a confirmation across a session change", async () => {
  const { hook, fetch } = await signedIn();
  await act(async () => { await hook.result.current.validate(hook.result.current.generation.current); });
  expect(fetch).toHaveBeenCalledTimes(1);
  const old = hook.result.current.generation.current;
  act(() => { hook.result.current.reset("signed-out"); });
  await act(async () => { expect(await hook.result.current.validateRecent(old)).toBe(false); });
  expect(fetch).toHaveBeenCalledTimes(1);
});

it("keeps the sign-in when the session check is busy or unreachable and signs out only when refused", async () => {
  const { hook, fetch } = await signedIn();
  const generation = hook.result.current.generation.current;
  fetch.mockResolvedValueOnce(Response.json({}, { status: 503 }));
  await act(async () => { expect(await hook.result.current.validate(generation)).toBe(false); });
  fetch.mockRejectedValueOnce(new Error("offline"));
  await act(async () => { expect(await hook.result.current.validate(generation)).toBe(false); });
  expect(hook.result.current.phase).toBe("ready");
  expect(hook.result.current.generation.current).toBe(generation);
  await act(async () => { expect(await hook.result.current.validate(generation)).toBe(true); });
  fetch.mockResolvedValueOnce(Response.json({}, { status: 401 }));
  await act(async () => { expect(await hook.result.current.validate(generation)).toBe(false); });
  expect(hook.result.current.phase).toBe("signed-out");
});
