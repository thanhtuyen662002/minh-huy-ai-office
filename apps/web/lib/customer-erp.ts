export type ErpConnection = {
  host: string; port: number; database: string; userName: string; password: string;
  encrypt: boolean; trustServerCertificate: boolean;
};
export type CustomerErpSource = { id: string; logicalName: string; isEnabled: boolean; bindingVersion: string };
export const erpMessages: Record<string, string> = {
  ok: "Kết nối thành công. Tài khoản đã được xác nhận chỉ đọc.",
  unreachable: "Không thể kết nối máy chủ SQL Server. Kiểm tra địa chỉ, cổng và chứng chỉ TLS.",
  login_failed: "Tài khoản hoặc mật khẩu không đúng.",
  database_not_found: "Không tìm thấy database hoặc tài khoản chưa được cấp quyền truy cập.",
  not_read_only: "Tài khoản này có quyền ghi hoặc quản trị nên không dùng trực tiếp được. Hãy tạo tài khoản chỉ đọc theo cách bên dưới.",
  missing_view_definition: "Tài khoản thiếu quyền VIEW ANY DEFINITION hoặc VIEW DEFINITION.",
  conflict: "Nguồn hoặc phiên bản kết nối đã thay đổi. Tải lại danh sách trước khi thử lại.",
  invalid_fields: "Thông tin kết nối không hợp lệ. Kiểm tra lại các trường.",
  unavailable: "Chưa nhận được kết quả từ máy chủ. Bấm lại để thử lần nữa.",
  load_failed: "Không tải được danh sách kết nối ERP. Bấm “Tải lại” để thử lại.",
  rate_limited: "Đã đạt giới hạn kiểm tra kết nối. Vui lòng đợi một phút.",
  not_permitted: "Tài khoản này không có quyền tạo tài khoản mới. Nhập tài khoản quản trị SQL (ví dụ sa) rồi bấm lại.",
};
export function isErpConnection(value: unknown, save = false): value is ErpConnection {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  const row = value as Record<string, unknown>;
  const fields = ["host", "port", "database", "userName", "password", "encrypt", "trustServerCertificate"];
  if (save) fields.push("operationId", "logicalName", ...(Object.hasOwn(row, "bindingVersion") ? ["bindingVersion"] : []));
  return Object.keys(row).every(key => fields.includes(key)) && fields.every(key => Object.hasOwn(row, key))
    && typeof row.host === "string" && row.host.length > 0 && row.host.length <= 253 && !/[;,\\/=\s]/.test(row.host)
    && Number.isInteger(row.port) && Number(row.port) >= 1 && Number(row.port) <= 65535
    && typeof row.database === "string" && row.database.trim().length > 0 && row.database.length <= 128
    && typeof row.userName === "string" && row.userName.trim().length > 0 && row.userName.length <= 128
    && typeof row.password === "string" && row.password.length > 0 && row.password.length <= 1024
    && typeof row.encrypt === "boolean" && typeof row.trustServerCertificate === "boolean"
    && (!save || (typeof row.operationId === "string" && guid.test(row.operationId) && typeof row.logicalName === "string"
      && row.logicalName.trim().length > 0 && row.logicalName.length <= 200
      && (!Object.hasOwn(row, "bindingVersion") || (typeof row.bindingVersion === "string" && /^[1-9][0-9]{0,18}$/.test(row.bindingVersion)))));
}
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export function parseCustomerErpSources(value: unknown): CustomerErpSource[] | null {
  if (!Array.isArray(value) || value.length > 1000) return null;
  const result: CustomerErpSource[] = [];
  for (const row of value) {
    if (!row || typeof row !== "object" || typeof row.id !== "string" || !guid.test(row.id)
      || typeof row.logicalName !== "string" || !row.logicalName.trim() || row.logicalName.length > 200
      || typeof row.isEnabled !== "boolean" || typeof row.bindingVersion !== "string"
      || !/^[1-9][0-9]{0,18}$/.test(row.bindingVersion) || result.some(item => item.id === row.id)) return null;
    result.push({ id: row.id, logicalName: row.logicalName, isEnabled: row.isEnabled, bindingVersion: row.bindingVersion });
  }
  return result;
}

export function readOnlyLoginSql(database: string): string {
  const identifier = `[${database.replaceAll("]", "]]")}]`;
  return `-- Chạy bằng quản trị SQL; thay mật khẩu mẫu trước khi chạy.
USE [master];
CREATE LOGIN [aioffice_reader] WITH PASSWORD = '<NHAP_MAT_KHAU_MANH>';
GRANT VIEW ANY DEFINITION TO [aioffice_reader];
USE ${identifier};
CREATE USER [aioffice_reader] FOR LOGIN [aioffice_reader];
-- Cấp trực tiếp, không dùng db_datareader (vai trò cố định bị từ chối).
GRANT SELECT TO [aioffice_reader];
GRANT VIEW DEFINITION TO [aioffice_reader];
-- Yêu cầu SQL Server 2022 độc lập; tài khoản không có quyền ghi/ủy quyền.
-- Không tự động cấp thêm quyền nếu hồ sơ bảo mật khác chưa được hỗ trợ.`;
}
