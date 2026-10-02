export type LocalAuthContext = Readonly<{
  tenantId: string;
  companyId: string;
  userId: string;
  roles: readonly string[];
}>;

export type LocalDataSource = Readonly<{
  id: string;
  logicalName: string;
  kind: string;
  environment: string;
  purpose: string;
  allowRead: boolean;
  allowWrite: boolean;
  maxConcurrency: number;
  isEnabled: boolean;
}>;

export type LocalErpTableEvidence = Readonly<{
  schema: string;
  table: string;
  approximateRowCount: number;
}>;

export type LocalAiCheckpoint = Readonly<{
  answer: string;
  provider: string;
  model: string;
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
  evidence: string;
  erpDatabase: string | null;
  erpTableCount: number | null;
  sampledTableCount: number | null;
  topTables: readonly LocalErpTableEvidence[];
}>;

export type LocalTaskSnapshot = Readonly<{
  taskId: string;
  taskStatus: number;
  stepStatus: number;
  dispatchState: number;
  attempt: number;
  resultPayloadJson: string | null;
  failureReason: string | null;
}>;

function own(value: object, key: PropertyKey): unknown {
  const descriptor = Object.getOwnPropertyDescriptor(value, key);
  return descriptor && "value" in descriptor ? descriptor.value : undefined;
}

function text(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 && value.trim() === value ? value : null;
}

function integer(value: unknown): number | null {
  return typeof value === "number" && Number.isSafeInteger(value) ? value : null;
}

function boolean(value: unknown): boolean | null {
  return typeof value === "boolean" ? value : null;
}

export function parseAuthContext(value: unknown): LocalAuthContext | null {
  if (value === null || typeof value !== "object") return null;
  const tenantId = text(own(value, "tenantId"));
  const companyId = text(own(value, "companyId"));
  const userId = text(own(value, "userId"));
  const rolesValue = own(value, "roles");
  if (!tenantId || !companyId || !userId || !Array.isArray(rolesValue)) return null;

  const roles: string[] = [];
  for (const item of rolesValue) {
    const role = text(item);
    if (!role || roles.includes(role)) return null;
    roles.push(role);
  }

  return Object.freeze({ tenantId, companyId, userId, roles: Object.freeze(roles) });
}

export function parseDataSources(value: unknown): readonly LocalDataSource[] | null {
  if (!Array.isArray(value) || value.length > 256) return null;
  const sources: LocalDataSource[] = [];

  for (const item of value) {
    if (item === null || typeof item !== "object") return null;
    const id = text(own(item, "id"));
    const logicalName = text(own(item, "logicalName"));
    const kind = text(own(item, "kind"));
    const environment = text(own(item, "environment"));
    const purpose = text(own(item, "purpose"));
    const allowRead = boolean(own(item, "allowRead"));
    const allowWrite = boolean(own(item, "allowWrite"));
    const maxConcurrency = integer(own(item, "maxConcurrency"));
    const isEnabled = boolean(own(item, "isEnabled"));

    if (
      !id || !logicalName || !kind || !environment || !purpose
      || allowRead === null || allowWrite === null || maxConcurrency === null || maxConcurrency < 1
      || isEnabled === null
    ) return null;

    sources.push(Object.freeze({
      id,
      logicalName,
      kind,
      environment,
      purpose,
      allowRead,
      allowWrite,
      maxConcurrency,
      isEnabled,
    }));
  }

  return Object.freeze(sources);
}

export function parseAcceptedTask(value: unknown): { taskId: string } | null {
  if (value === null || typeof value !== "object") return null;
  const taskId = text(own(value, "taskId"));
  return taskId ? { taskId } : null;
}

export function parseTaskSnapshot(value: unknown): LocalTaskSnapshot | null {
  if (value === null || typeof value !== "object") return null;
  const taskId = text(own(value, "taskId"));
  const taskStatus = integer(own(value, "taskStatus"));
  const stepStatus = integer(own(value, "stepStatus"));
  const dispatchState = integer(own(value, "dispatchState"));
  const attempt = integer(own(value, "attempt"));
  const result = own(value, "resultPayloadJson");
  const failure = own(value, "failureReason");

  if (
    !taskId || taskStatus === null || stepStatus === null
    || dispatchState === null || attempt === null
    || !(result === null || typeof result === "string")
    || !(failure === null || typeof failure === "string")
  ) return null;

  return Object.freeze({
    taskId,
    taskStatus,
    stepStatus,
    dispatchState,
    attempt,
    resultPayloadJson: result,
    failureReason: failure,
  });
}

export function parseAiCheckpoint(payload: string): LocalAiCheckpoint | null {
  let value: unknown;
  try {
    value = JSON.parse(payload);
  } catch {
    return null;
  }
  if (value === null || typeof value !== "object") return null;

  const answer = text(own(value, "answer"));
  const provider = text(own(value, "provider"));
  const model = text(own(value, "model"));
  const evidence = text(own(value, "evidence"));
  const usage = own(value, "usage");
  if (!answer || !provider || !model || !evidence || usage === null || typeof usage !== "object") return null;

  const inputTokens = integer(own(usage, "inputTokens"));
  const outputTokens = integer(own(usage, "outputTokens"));
  const totalTokens = integer(own(usage, "totalTokens"));
  if (inputTokens === null || outputTokens === null || totalTokens === null) return null;

  let erpDatabase: string | null = null;
  let erpTableCount: number | null = null;
  let sampledTableCount: number | null = null;
  let topTables: LocalErpTableEvidence[] = [];

  const erpEvidence = own(value, "erpEvidence");
  if (erpEvidence !== undefined) {
    if (erpEvidence === null || typeof erpEvidence !== "object") return null;
    erpDatabase = text(own(erpEvidence, "databaseName"));
    erpTableCount = integer(own(erpEvidence, "tableCount"));
    sampledTableCount = integer(own(erpEvidence, "sampledTableCount"));
    const tablesValue = own(erpEvidence, "topTables");
    if (!erpDatabase || erpTableCount === null || sampledTableCount === null || !Array.isArray(tablesValue) || tablesValue.length > 20) return null;

    topTables = [];
    for (const tableValue of tablesValue) {
      if (tableValue === null || typeof tableValue !== "object") return null;
      const schema = text(own(tableValue, "schema"));
      const table = text(own(tableValue, "table"));
      const approximateRowCount = integer(own(tableValue, "approximateRowCount"));
      if (!schema || !table || approximateRowCount === null || approximateRowCount < 0) return null;
      topTables.push(Object.freeze({ schema, table, approximateRowCount }));
    }
  }

  return Object.freeze({
    answer,
    provider,
    model,
    inputTokens,
    outputTokens,
    totalTokens,
    evidence,
    erpDatabase,
    erpTableCount,
    sampledTableCount,
    topTables: Object.freeze(topTables),
  });
}
