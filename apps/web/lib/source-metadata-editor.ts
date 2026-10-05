import { LocalDataSource } from "./local-ai-workspace";

export type SourceMetadataDraft = {
  logicalName: string;
  purpose: string;
  maxConcurrency: string;
  isEnabled: boolean;
};

export function sourceMetadataUpdate(draft: SourceMetadataDraft) {
  const logicalName = draft.logicalName.trim();
  const purpose = draft.purpose.trim();
  const maxConcurrency = Number(draft.maxConcurrency);
  if (!logicalName || logicalName.length > 200 || !purpose || purpose.length > 200
    || !draft.maxConcurrency.trim() || !Number.isSafeInteger(maxConcurrency)
    || maxConcurrency < 1 || maxConcurrency > 1024) {
    throw new Error("Nhập tên và mục đích tối đa 200 ký tự; số tác vụ đồng thời từ 1 đến 1.024.");
  }
  return {
    logicalName, purpose, maxConcurrency, isEnabled: draft.isEnabled,
  };
}

export function sameSourceMetadata(a: LocalDataSource, b: LocalDataSource) {
  return a.id === b.id && a.logicalName === b.logicalName && a.purpose === b.purpose
    && a.maxConcurrency === b.maxConcurrency && a.isEnabled === b.isEnabled;
}

export function taskSourceSelection(sources: readonly LocalDataSource[], current: string) {
  const eligible = sources.filter(source => source.isEnabled && source.allowRead && !source.allowWrite);
  return eligible.find(source => source.id === current)?.id ?? eligible[0]?.id ?? "";
}
