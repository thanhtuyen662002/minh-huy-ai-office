// @vitest-environment node
import { afterEach, beforeEach, expect, it, vi } from "vitest";
const mocks = vi.hoisted(() => ({ cookies: vi.fn(), runtime: vi.fn(), read: vi.fn(), fetch: vi.fn() }));
vi.mock("next/headers", () => ({ cookies: mocks.cookies }));
vi.mock("./browser-auth-runtime", () => ({ getBrowserAuthRuntime: mocks.runtime }));
import { assistantHideBff, assistantImageBff, assistantImageUploadBff } from "./assistant-bff";
import { botCompany as company } from "./bot-channel-test-fixture";
const session = { companyId: company, accessToken: "PRIVATE-TOKEN" };
const png = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82]);
const id = "6f1c0c8e-1d2a-4b3c-8d4e-5f6a7b8c9d0e";
const upload = (body: BodyInit, type = "image/png", origin = "http://web.test") => new Request(`http://web.test/api/local/assistant-images?companyId=${company}`,
  { method: "POST", headers: { "Content-Type": type, Origin: origin }, body });
const hide = (value: unknown) => new Request(`http://web.test/api/local/assistant-history/hide?companyId=${company}`,
  { method: "POST", headers: { "Content-Type": "application/json", Origin: "http://web.test" }, body: JSON.stringify(value) });
beforeEach(() => {
  vi.clearAllMocks(); vi.stubEnv("AIOFFICE_BROWSER_OIDC_ENABLED", "false"); vi.stubEnv("AIOFFICE_LOCAL_UI_ENABLED", "true");
  mocks.cookies.mockResolvedValue({ get: () => ({ value: "PRIVATE-TOKEN" }) }); mocks.read.mockResolvedValue(session);
  mocks.runtime.mockReturnValue({ coreApiOrigin: "http://core.test", settings: {}, sessions: { read: mocks.read } }); vi.stubGlobal("fetch", mocks.fetch);
});
afterEach(() => { vi.unstubAllGlobals(); vi.unstubAllEnvs(); });

it("uploads a picture whose bytes match its type and returns only its id", async () => {
  mocks.fetch.mockResolvedValue(Response.json({ id: id.toUpperCase(), contentType: "image/png", bytes: png.byteLength, secret: "x" }));
  const response = await assistantImageUploadBff(upload(png));
  expect(response.status).toBe(200);
  expect(await response.json()).toEqual({ id, contentType: "image/png", bytes: png.byteLength });
  expect(mocks.fetch.mock.calls[0][0]).toBe(`http://127.0.0.1:8080/api/companies/${company}/assistant-images`);
});

it.each([[png, "image/jpeg"], [new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]), "image/png"], [png, "text/html"]])
("refuses a body that is not the declared picture type", async (body, type) => {
  expect((await assistantImageUploadBff(upload(body, type))).status).toBe(400);
  expect(mocks.fetch).not.toHaveBeenCalled();
});

it("refuses a cross-site upload", async () => {
  expect((await assistantImageUploadBff(upload(png, "image/png", "http://evil.test"))).status).toBe(403);
});

it("serves a picture only when the bytes match the declared type", async () => {
  mocks.fetch.mockResolvedValueOnce(new Response(png, { headers: { "Content-Type": "image/png" } }));
  const ok = await assistantImageBff(new Request(`http://web.test/api/local/assistant-images/${id}?companyId=${company}`), id);
  expect(ok.status).toBe(200); expect(ok.headers.get("x-content-type-options")).toBe("nosniff");
  mocks.fetch.mockResolvedValueOnce(new Response(png, { headers: { "Content-Type": "image/jpeg" } }));
  expect((await assistantImageBff(new Request(`http://web.test/api/local/assistant-images/${id}?companyId=${company}`), id)).status).toBe(502);
  expect((await assistantImageBff(new Request(`http://web.test/api/local/assistant-images/x?companyId=${company}`), "x")).status).toBe(400);
});

it("hides a validated list of questions", async () => {
  mocks.fetch.mockResolvedValue(Response.json({ hidden: 1 }));
  const response = await assistantHideBff(hide({ taskIds: [id.toUpperCase()] }));
  expect(response.status).toBe(200); expect(await response.json()).toEqual({ hidden: 1 });
  expect(JSON.parse(String(mocks.fetch.mock.calls[0][1].body))).toEqual({ taskIds: [id] });
  for (const bad of [{ taskIds: [] }, { taskIds: ["x"] }, { taskIds: [id], extra: 1 }, []])
    expect((await assistantHideBff(hide(bad))).status).toBe(400);
});
