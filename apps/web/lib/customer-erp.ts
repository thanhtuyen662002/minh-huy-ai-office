export type ErpConnection = {
  host: string; port: number; database: string; userName: string; password: string;
  encrypt: boolean; trustServerCertificate: boolean;
};
export type CustomerErpSource = { id: string; logicalName: string; isEnabled: boolean; bindingVersion: string };
export const erpMessages: Record<string, string> = {
  ok: "Kết nối thành công. Tài khoản đã được xác nhận chỉ đọc.",
  check_timeout: "Đã kết nối được, nhưng bước kiểm tra quyền trên database này chạy quá lâu. Thử lại sau ít phút; nếu vẫn lỗi, báo đội kỹ thuật.",
  unreachable: "Không thể kết nối máy chủ SQL Server. Kiểm tra địa chỉ, cổng và chứng chỉ TLS.",
  login_failed: "Tài khoản hoặc mật khẩu không đúng.",
  database_not_found: "Không tìm thấy database hoặc tài khoản chưa được cấp quyền truy cập.",
  not_read_only: "Tài khoản này có quyền ghi hoặc quản trị nên không dùng trực tiếp được. Hãy tạo tài khoản chỉ đọc theo cách bên dưới.",
  unsupported_database: "Tài khoản đúng là chỉ đọc, nhưng database này có thành phần AI Office chưa hỗ trợ đọc an toàn (xem chi tiết bên dưới).",
  missing_view_definition: "Tài khoản thiếu quyền VIEW ANY DEFINITION hoặc VIEW DEFINITION.",
  conflict: "Nguồn hoặc phiên bản kết nối đã thay đổi. Tải lại danh sách trước khi thử lại.",
  invalid_fields: "Thông tin kết nối không hợp lệ. Kiểm tra lại các trường.",
  unavailable: "Chưa nhận được kết quả từ máy chủ. Bấm lại để thử lần nữa.",
  load_failed: "Không tải được danh sách kết nối ERP. Bấm “Tải lại” để thử lại.",
  rate_limited: "Đã đạt giới hạn kiểm tra kết nối. Vui lòng đợi một phút.",
  not_permitted: "Tài khoản này không có quyền tạo tài khoản mới. Nhập tài khoản quản trị SQL (ví dụ sa) rồi bấm lại.",
};
export type ErpFinding = { code: string; writeRisk: boolean; detail: string | null };
// Vietnamese labels for the server's named read-only checks. Unknown codes are shown as-is.
export const erpFindingLabels: Record<string, string> = {
  sql_version: "Phiên bản SQL Server chưa hỗ trợ (cần SQL Server 2019 trở lên)",
  availability_group: "Máy chủ đang bật Always On (Availability Group)",
  system_database: "Đang trỏ vào database hệ thống (master, msdb…)",
  dbo_user: "Tài khoản đang là chủ database (dbo)",
  sysadmin: "Tài khoản thuộc vai trò sysadmin",
  db_owner: "Tài khoản thuộc vai trò db_owner",
  fixed_server_role: "Tài khoản thuộc vai trò cố định cấp máy chủ",
  fixed_database_role: "Tài khoản thuộc vai trò cố định của database (ví dụ db_datareader, db_datawriter)",
  missing_view_definition: "Thiếu quyền VIEW ANY DEFINITION / VIEW DEFINITION / CONNECT",
  metadata_deny: "Có lệnh DENY VIEW DEFINITION đang chặn tài khoản",
  grant_option: "Tài khoản được cấp quyền kèm WITH GRANT OPTION",
  database_grant_scope: "Có quyền cấp trên đối tượng đặc biệt trong database",
  server_grant_scope: "Có quyền cấp trên đối tượng đặc biệt của máy chủ",
  ownership: "Tài khoản đang sở hữu một đối tượng (sở hữu = toàn quyền)",
  server_permission: "Có quyền cấp máy chủ ngoài quyền đọc",
  database_permission: "Có quyền cấp database ngoài quyền đọc",
  impersonate: "Có quyền IMPERSONATE (đóng vai tài khoản khác)",
  role_admin: "Có quyền quản lý vai trò (role)",
  schema_hidden: "Một số schema bị ẩn với tài khoản",
  schema_permission: "Có quyền trên schema ngoài quyền đọc",
  object_hidden: "Một số bảng/view/thủ tục bị ẩn với tài khoản",
  object_permission: "Có quyền trên bảng/view ngoài quyền đọc",
  column_update: "Có quyền UPDATE trên cột",
  executable_procedure: "Có quyền chạy stored procedure (procedure có thể ghi dữ liệu). Thường do database cấp EXECUTE cho public; chạy DENY EXECUTE TO [tên tài khoản] trong database để chặn",
  clr_function: "Database có hàm CLR (.NET)",
  extended_procedure: "Database có extended stored procedure",
  synonym: "Database có synonym",
  external_dependency: "Có view/hàm đọc sang database hoặc máy chủ khác, hoặc tham chiếu đối tượng không tồn tại",
  cross_database_chaining: "Database bật TRUSTWORTHY hoặc cross-database ownership chaining",
  view_external_rowset: "Có view/hàm dùng OPENQUERY/OPENROWSET hoặc bị mã hoá",
  external_table: "Database có external table",
  clr_assembly: "Database có assembly CLR",
  linked_server: "Máy chủ có linked server",
  check_failed: "Không chạy được bước kiểm tra quyền",
  connection_tls: "Lỗi bắt tay mã hoá TLS (thường do SQL Server cũ chỉ hỗ trợ TLS 1.0/1.1 hoặc chứng chỉ không hợp lệ)",
  connection_dns: "Không tìm thấy tên máy chủ (DNS)",
  connection_refused: "Máy chủ từ chối kết nối ở cổng này",
  connection_timeout: "Hết thời gian chờ máy chủ phản hồi",
  connection_failed: "Lỗi kết nối",
};
export function parseErpFindings(value: unknown): ErpFinding[] {
  if (!Array.isArray(value)) return [];
  return value.slice(0, 40).flatMap(row => row && typeof row === "object" && typeof row.code === "string" && /^[a-z_]{1,40}$/.test(row.code)
    ? [{ code: row.code, writeRisk: row.writeRisk === true, detail: typeof row.detail === "string" ? row.detail.slice(0, 300) : null }] : []);
}
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
-- Chặn chạy stored procedure kể cả khi database đã cấp EXECUTE cho public.
DENY EXECUTE TO [aioffice_reader];
-- Yêu cầu SQL Server 2019 trở lên, không bật Always On; tài khoản không có quyền ghi/ủy quyền.
-- Không tự động cấp thêm quyền nếu hồ sơ bảo mật khác chưa được hỗ trợ.`;
}
