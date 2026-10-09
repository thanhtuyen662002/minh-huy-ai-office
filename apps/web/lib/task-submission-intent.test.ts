// @vitest-environment node
import { expect, it } from "vitest";
import { parseSubmissionInput, parseSubmissionIntent, parseSubmissionJson, parseSubmissionReceipt, receiptMatches,
  submissionFingerprint, verifiedSubmissionIntent, verifiedSubmissionPage } from "./task-submission-intent";

const operationId = "11111111-1111-4111-8111-111111111111", dataSourceId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const companyId = "22222222-2222-4222-8222-222222222222";
const input = { operationId, dataSourceId, question: "Tồn kho 😀 �" };
const inputFingerprint = "6354CD8F9D07F489B9F1B213CE89B4FE5EF4016DFC44329AE740B0C39BDE0656";
const receipt = { companyId, operationId, dataSourceId, inputFingerprint, taskId: "33333333-3333-4333-8333-333333333333",
  stepId: "44444444-4444-4444-8444-444444444444", messageId: "55555555-5555-4555-8555-555555555555", status: 0, dispatchState: 0, createdAtUtc: "2026-10-09T08:00:00Z" };
const intent = { companyId, ...input, inputFingerprint, state: 0, createdAtUtc: "2026-10-09T08:00:00Z", expiresAtUtc: "2026-10-10T08:00:00Z", accepted: null };

it.each([
  ["Tồn kho 😀 �", inputFingerprint],
  ["ồ", "653C057F6AFF5A8FF5FEAFA8B8CFAABAE942E33F0605BC03D14876C53B223DE5"],
  ["o\u0302\u0300", "7176B4A97A12F1F815F8133B9E33C7D021885765A6A5D560976A42104F0ED186"],
])("matches independent Python and C# byte-layout vector for %s", async (question, expected) => {
  expect(await submissionFingerprint({ ...input, question })).toBe(expected);
  expect(await submissionFingerprint({ ...input, operationId: crypto.randomUUID(), dataSourceId: dataSourceId.toUpperCase(), question })).toBe(expected);
});
it("does not normalize exact source, case, Unicode composition or internal whitespace", async () => {
  for (const changed of [{ ...input, dataSourceId: companyId }, { ...input, question: "tồn kho 😀 �" }, { ...input, question: "Tồn  kho 😀 �" }])
    expect(await submissionFingerprint(changed)).not.toBe(inputFingerprint);
  expect(await submissionFingerprint({ ...input, question: "ồ" })).not.toBe(await submissionFingerprint({ ...input, question: "o\u0302\u0300" }));
});
it.each(["", " leading", "trailing ", "a\n", "a\u0000", "a\u0085", "\ud800", "a\udfff", "😀\ud800"])("rejects invalid scalar/canonical input before encoding: %j", async question => {
  expect(parseSubmissionInput({ ...input, question })).toBeNull(); await expect(submissionFingerprint({ ...input, question })).rejects.toThrow();
});
it("preserves supported4000 UTF16 units and normalizes only validated GUID spelling", async () => {
  const question = "😀".repeat(2000);
  expect(parseSubmissionInput({ ...input, dataSourceId: dataSourceId.toUpperCase(), question })).toEqual({ ...input, question });
  expect(await submissionFingerprint({ ...input, question })).toMatch(/^[0-9A-F]{64}$/);
  expect(parseSubmissionInput({ ...input, question: question + "x" })).toBeNull();
  for (const malformed of [{ ...input, tenantId: companyId }, { ...input, operationId: "00000000-0000-0000-0000-000000000000" },
    { ...input, dataSourceId: dataSourceId.replaceAll("-", "") }]) expect(parseSubmissionInput(malformed)).toBeNull();
});
it.each([
  '{"operationId":1,"operationId":2}', '{"operationId":1,"\\u006fperationId":2}',
  '{"items":[{"accepted":{"taskId":1,"taskId":2}}]}', '{"a":{"b":{"c":{"d":{"e":{"f":{}}}}}}}',
])("rejects decoded duplicates at any depth and unsupported nesting %s", json => { expect(() => parseSubmissionJson(json)).toThrow(); });
it("allows repeated property names in distinct object scopes and escaped literal question text", () => {
  const value = { items: [intent, intent], question: 'literal "key":1, "key":2 and \\ { }' };
  expect(parseSubmissionJson(JSON.stringify(value))).toEqual(value);
});
it("verifies immutable server input hashes and honest prepared/expired/accepted/unavailable shapes", async () => {
  expect(await verifiedSubmissionIntent(intent)).toEqual(intent);
  expect(await verifiedSubmissionIntent({ ...intent, state: 2 })).toMatchObject({ state: 2 });
  expect(await verifiedSubmissionIntent({ ...intent, state: 1, accepted: receipt })).toMatchObject({ accepted: receipt });
  const unavailable = { companyId, operationId, state: 3, dataSourceId: null, question: null, inputFingerprint: null, createdAtUtc: null, expiresAtUtc: null, accepted: null };
  expect(await verifiedSubmissionIntent(unavailable)).toEqual(unavailable);
  expect(await verifiedSubmissionIntent({ ...intent, question: "tampered exact body" })).toBeNull();
  expect(parseSubmissionIntent({ ...unavailable, question: "PRIVATE" })).toBeNull();
  expect(parseSubmissionIntent({ ...intent, state: 1 })).toBeNull(); expect(parseSubmissionIntent({ ...intent, accepted: receipt })).toBeNull();
  expect(parseSubmissionIntent({ ...intent, state: 1, accepted: { ...receipt, operationId: companyId } })).toBeNull();
});
it.each(["2026-02-30T08:00:00Z", "0000-10-09T08:00:00Z", "2026-10-09T25:00:00Z", "2026-10-09T08:00:00+14:01", "not-a-date"])("rejects noncalendar receipt times %s", createdAtUtc => {
  expect(parseSubmissionReceipt({ ...receipt, createdAtUtc })).toBeNull();
});
it("rejects changed lifetime, unknown states and unsafe receipt fields", () => {
  for (const changed of [{ ...intent, expiresAtUtc: "2026-10-10T08:00:01Z" }, { ...intent, inputFingerprint: inputFingerprint.toLowerCase() },
    { ...intent, state: 4 }, { ...intent, ownerId: companyId }]) expect(parseSubmissionIntent(changed)).toBeNull();
  for (const changed of [{ ...receipt, status: 7 }, { ...receipt, dispatchState: 4 }, { ...receipt, status: "0" }, { ...receipt, taskId: "" },
    { ...receipt, diagnostic: "PRIVATE" }]) expect(parseSubmissionReceipt(changed)).toBeNull();
});
it("binds operation/source/fingerprint/company and stable identities while allowing worker status to advance", () => {
  expect(receiptMatches(receipt, companyId.toUpperCase(), input, inputFingerprint)).toBe(true);
  expect(receiptMatches({ ...receipt, status: 6, dispatchState: 2 }, companyId, input, inputFingerprint, receipt)).toBe(true);
  for (const changed of [{ ...receipt, operationId: companyId }, { ...receipt, companyId: operationId }, { ...receipt, dataSourceId: operationId },
    { ...receipt, inputFingerprint: "A".repeat(64) }, { ...receipt, taskId: companyId }, { ...receipt, stepId: companyId },
    { ...receipt, messageId: companyId }, { ...receipt, createdAtUtc: "2026-10-09T08:00:01Z" }])
    expect(receiptMatches(changed, companyId, input, inputFingerprint, receipt)).toBe(false);
});
it("validates owner pages, GUID-equivalent duplicates, bounded pagination and every input hash", async () => {
  const page = { companyId, items: [intent], offset: 0, limit: 1, hasMore: true };
  expect(await verifiedSubmissionPage(page)).toEqual(page);
  for (const changed of [{ ...page, items: [{ ...intent, companyId: operationId }] }, { ...page, items: [{ ...intent, question: "changed" }] },
    { ...page, items: [intent, { ...intent, operationId: operationId.toUpperCase() }], limit: 2 }, { ...page, limit: 26 },
    { ...page, offset: 10001 }, { ...page, items: [] }, { ...page, ownerId: operationId }]) expect(await verifiedSubmissionPage(changed)).toBeNull();
});
