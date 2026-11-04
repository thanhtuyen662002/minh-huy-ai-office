using MinhHuy.AIOffice.Platform.Persistence;

namespace MinhHuy.AIOffice.Agent.Worker;

/// <summary>
/// Fixed, nonsecret Vietnamese explanations for a refused or failed ERP read, shown with the failed task.
/// They name the check that refused the read; they never include connection strings, data or server text.
/// </summary>
public static class ErpAccessFailure
{
    public const string SourceUnavailable =
        "Nguồn dữ liệu đang tắt, không cho đọc, đang cho phép ghi, hoặc chưa gán thông tin kết nối.";
    public const string AuthorityUnavailable =
        "Không xác minh được quyền dùng nguồn dữ liệu này cho người hỏi (nguồn đã đổi cấu hình, thông tin kết nối đã đổi, hoặc người hỏi không còn quyền).";
    public const string CheckTimeout =
        "Kiểm tra quyền chỉ đọc của tài khoản ERP chạy quá lâu (quá 30 giây) nên chưa đọc dữ liệu. Thử lại sau ít phút.";

    public static string For(UnauthorizedAccessException error) => error switch
    {
        ErpReadOnlyCredentialsException { Detail: { Length: > 0 } detail } =>
            $"Tài khoản ERP chưa đạt chuẩn chỉ đọc: {detail}.",
        ErpReadOnlyCredentialsException => "Tài khoản ERP chưa đạt chuẩn chỉ đọc (không đọc được lý do cụ thể).",
        _ => AuthorityUnavailable,
    };
}
