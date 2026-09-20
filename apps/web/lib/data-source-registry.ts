export type DataSourceAccess = "read" | "read-write";
export type ConnectionStatus = "healthy" | "warning" | "unknown";

export type DataSourceView = {
  id: string;
  companyId: string;
  logicalName: string;
  kind: "sql-server" | "erp" | "api";
  environment: "production" | "staging" | "development";
  purpose: string;
  access: DataSourceAccess;
  maxConcurrency: number;
  enabled: boolean;
  connectionStatus: ConnectionStatus;
  connectionMessage: string;
  secretConfigured: boolean;
};

export type DataSourceMetadataEdit = Omit<DataSourceView, "id" | "companyId" | "connectionStatus" | "connectionMessage" | "secretConfigured">;
export type SecretRotationIntent = { dataSourceId: string; replacementSecret: string };

export const demoDataSources: DataSourceView[] = [
  {
    id: "erp-production",
    companyId: "internal",
    logicalName: "company.erp.production",
    kind: "erp",
    environment: "production",
    purpose: "ERP kế toán và bán hàng",
    access: "read-write",
    maxConcurrency: 4,
    enabled: true,
    connectionStatus: "healthy",
    connectionMessage: "Kết nối gần nhất thành công.",
    secretConfigured: true,
  },
  {
    id: "reporting",
    companyId: "internal",
    logicalName: "company.reporting.readonly",
    kind: "sql-server",
    environment: "production",
    purpose: "Báo cáo và đối chiếu",
    access: "read",
    maxConcurrency: 8,
    enabled: true,
    connectionStatus: "warning",
    connectionMessage: "Chưa thể kiểm tra kết nối. Hãy thử lại hoặc liên hệ quản trị hệ thống.",
    secretConfigured: true,
  },
];

export function dataSourcesForCompany(companyId: string, sources: DataSourceView[] = demoDataSources) {
  if (!companyId.trim()) return [];
  return sources.filter((source) => source.companyId === companyId);
}

export function applyMetadataEdit(current: DataSourceView, edit: DataSourceMetadataEdit): DataSourceView {
  return { ...current, ...edit, secretConfigured: current.secretConfigured };
}

export function createSecretRotationIntent(dataSourceId: string, replacementSecret: string): SecretRotationIntent | null {
  if (!dataSourceId.trim() || !replacementSecret.trim()) return null;
  return { dataSourceId, replacementSecret };
}
