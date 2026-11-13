# API chat AI Office cho web ERP

Web ERP gọi API này **từ máy chủ của web ERP** (không gọi từ trình duyệt). Khoá ký phải nằm ở máy chủ, không bao giờ gửi xuống trình duyệt.

- Địa chỉ: `https://api.finora.io.vn/api/erp-chat/v1`
- Quản trị viên AI Office tạo web ERP ở **Kết nối → Chi phí AI → Web ERP kết nối** và nhận về **Client ID** và **khoá ký**. Khoá ký chỉ hiện một lần. Nếu lộ khoá, bấm "Tạo khoá mới" thì khoá cũ hết hiệu lực ngay.
- Mỗi web ERP gắn với một **khách hàng tính tiền**. Web ERP chỉ hỏi được các nguồn ERP của khách hàng đó, và chi phí được tính cho khách hàng đó.
- Giai đoạn này **chỉ đọc**: AI chỉ chạy câu `SELECT` trên tài khoản SQL chỉ đọc.

## Ký yêu cầu

Mỗi yêu cầu gửi kèm 4 header:

| Header | Giá trị |
|---|---|
| `X-AIOffice-Client` | Client ID (GUID dạng `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`) |
| `X-AIOffice-Timestamp` | Giờ Unix tính bằng giây. Lệch quá 5 phút so với máy chủ thì bị từ chối |
| `X-AIOffice-Nonce` | Một GUID mới cho **mỗi** yêu cầu. Dùng lại nonce thì bị từ chối (`replayed`) |
| `X-AIOffice-Signature` | HMAC-SHA256 dạng hex của chuỗi ký bên dưới, tính bằng khoá ký (giải base64 ra 32 byte) |

Chuỗi ký gồm 7 dòng, nối bằng `\n` (không có `\n` ở cuối):

```
aioffice-erp-chat-v1
<Client ID>
<METHOD>                 # POST hoặc GET, viết hoa
<path và query>          # ví dụ /api/erp-chat/v1/messages/9b1d…?externalUserId=nv001
<timestamp>
<nonce>
<SHA-256 của body, hex viết hoa>   # body rỗng (GET) dùng SHA-256 của chuỗi rỗng
```

Phần query phải giống hệt từng byte trong URL được gửi đi, kể cả cách mã hoá ký tự.

## Gửi câu hỏi

`POST /api/erp-chat/v1/messages` với body JSON (UTF-8):

```json
{
  "requestId": "web-erp-12345",
  "externalUserId": "nv001",
  "question": "Doanh thu tháng 11 của chi nhánh Cần Thơ?",
  "dataSourceId": null,
  "conversationId": null,
  "scopes": { "branch_id": "CT01" }
}
```

- `requestId` (bắt buộc, tối đa 100 ký tự): mã do web ERP tạo cho mỗi lần hỏi. Gửi lại cùng `requestId` và cùng nội dung sẽ nhận về **cùng một** câu hỏi, không tạo câu mới, nên có thể gửi lại an toàn khi mất kết nối.
- `externalUserId` (bắt buộc, tối đa 200 ký tự): mã người dùng trong web ERP. Mỗi người có hội thoại riêng và giới hạn riêng là 20 câu mỗi phút. Chi phí cũng được ghi theo từng người.
- `dataSourceId`: bắt buộc khi khách hàng có nhiều database. Bỏ trống nếu khách hàng chỉ có một database.
- `conversationId`: để hỏi tiếp trong một hội thoại, gửi `conversationId` đã nhận ở câu trước. AI sẽ nhớ các câu trước của **chính người đó**. Bỏ trống để mở hội thoại mới.
- `scopes`: phạm vi dữ liệu của người hỏi, ví dụ chi nhánh. Xem mục [Phạm vi dữ liệu](#phạm-vi-dữ-liệu). Nếu phạm vi khác với các câu trước, AI Office mở hội thoại mới và trả `conversationId` mới.

Trả về `202`:

```json
{ "messageId": "9b1d…", "conversationId": "9b1d…", "status": "pending" }
```

## Lấy câu trả lời

`GET /api/erp-chat/v1/messages/{messageId}?externalUserId=nv001`

Hỏi lại khoảng mỗi 2 giây cho đến khi `status` khác `pending`. Thông thường một câu mất từ 10 đến 60 giây.

```json
{
  "messageId": "9b1d…",
  "conversationId": "9b1d…",
  "status": "answered",
  "answer": "Doanh thu tháng 11 …",
  "failure": null,
  "usage": { "inputTokens": 12000, "outputTokens": 800, "cost": 1350.5, "currency": "VND" }
}
```

`status` có thể là `pending`, `answered` hoặc `failed`. Khi `failed`, trường `failure` cho biết lý do. `usage.cost` là `null` khi model chưa có giá trong bảng giá.

## Lỗi

Lỗi trả về dạng `{ "code": "...", "error": "câu giải thích" }`:

| HTTP | code | Ý nghĩa |
|---|---|---|
| 400 | `invalid_fields`, `data_source_required` | Thiếu trường, hoặc khách hàng có nhiều database mà chưa gửi `dataSourceId` |
| 400 | `invalid_scopes`, `scopes_required` | `scopes` sai quy tắc, hoặc web ERP này bắt buộc gửi phạm vi mà câu hỏi không có |
| 401 | `unauthorized`, `replayed` | Sai chữ ký, lệch giờ, web ERP đã bị tắt, hoặc nonce đã dùng |
| 403 | `data_source_denied` | Nguồn không thuộc khách hàng của web ERP này |
| 404 | `not_found` | Không có câu hỏi này cho người dùng này |
| 409 | `request_conflict` | `requestId` đã dùng cho câu hỏi khác, người khác, hoặc phạm vi khác |
| 429 | `ai-user-rate`, `ai-account-rate`, `ai-budget` | Quá số câu mỗi phút, hoặc khách hàng đã hết hạn mức tháng |
| 429 | (không có body) | Quá 120 lần gọi mỗi phút cho một Client ID |
| 503 | `unavailable` | Lỗi tạm thời, thử lại sau |

## Phạm vi dữ liệu

Phạm vi dữ liệu giới hạn những dòng AI đọc được theo người hỏi, ví dụ nhân viên chỉ xem được chi nhánh của mình. Database ERP tự lọc dữ liệu bằng **Row-Level Security** của SQL Server. AI Office chỉ chuyển phạm vi của người hỏi vào phiên SQL.

**Cách hoạt động**

1. Web ERP gửi `scopes` trong mỗi câu hỏi, ví dụ `{ "branch_id": "CT01", "region": "MN" }`. Phạm vi nằm trong body nên được ký cùng yêu cầu: không ai sửa được trên đường truyền.
2. Trước khi AI chạy truy vấn, AI Office đặt mỗi phạm vi vào `SESSION_CONTEXT` ở chế độ chỉ đọc (`@read_only = 1`). Mã người dùng ERP luôn được đặt thêm dưới khoá dành riêng `aioffice_erp_user`. Câu SQL do AI viết không thể đổi các giá trị này, vì chúng là chỉ đọc và AI không được gọi thủ tục `sp_*`.
3. Chính sách Row-Level Security trong database đọc `SESSION_CONTEXT` và chỉ trả về các dòng hợp lệ.
4. **Chặn khi chưa an toàn:** nếu câu hỏi có phạm vi mà database không có security policy nào đang bật, AI Office từ chối câu hỏi (`status: "failed"`) thay vì trả về toàn bộ dữ liệu.

**Quy tắc cho `scopes`**

- Tối đa 10 khoá.
- Khoá: chữ thường, số và `_`, bắt đầu bằng chữ, tối đa 64 ký tự, không được bắt đầu bằng `aioffice_`.
- Giá trị: chuỗi từ 1 đến 256 ký tự, không có ký tự điều khiển. Số cũng phải gửi dạng chuỗi, ví dụ `"12"`.
- Không gửi `scopes` thì câu hỏi không bị giới hạn theo phạm vi. Muốn bắt buộc mọi câu hỏi phải có phạm vi, bật **Bắt buộc phạm vi** cho web ERP trong AI Office (Kết nối → Chi phí AI → Web ERP kết nối). Khi đó câu hỏi thiếu phạm vi bị trả `400 scopes_required`.

**Ví dụ cho đội ERP: lọc theo chi nhánh**

```sql
CREATE SCHEMA rls;
GO
-- Trả về một dòng khi chi nhánh của dòng dữ liệu trùng với phạm vi của người hỏi.
-- Phiên không có phạm vi (ứng dụng ERP thường) vẫn thấy đủ: chỉ lọc khi có khoá aioffice_erp_user.
CREATE FUNCTION rls.fn_branch(@BranchId nvarchar(50))
RETURNS TABLE WITH SCHEMABINDING
AS RETURN
  SELECT 1 AS allowed
  WHERE SESSION_CONTEXT(N'aioffice_erp_user') IS NULL
     OR @BranchId = CAST(SESSION_CONTEXT(N'branch_id') AS nvarchar(50));
GO
CREATE SECURITY POLICY rls.BranchPolicy
  ADD FILTER PREDICATE rls.fn_branch(BranchId) ON dbo.Invoice,
  ADD FILTER PREDICATE rls.fn_branch(BranchId) ON dbo.Customer
  WITH (STATE = ON);
GO
-- Tài khoản đọc của AI Office cần thấy policy để AI Office xác nhận lọc đang bật:
GRANT VIEW DEFINITION ON OBJECT::rls.BranchPolicy TO [aioffice_reader];
```

Thay `aioffice_reader` bằng tài khoản SQL mà AI Office dùng để đọc database này. Nên chỉ lọc các phiên của AI Office (có `aioffice_erp_user`) như ví dụ, để không ảnh hưởng ứng dụng ERP đang chạy. Nếu người dùng ERP có phạm vi nhưng thiếu khoá `branch_id`, `SESSION_CONTEXT(N'branch_id')` là `NULL`, nên người đó không thấy dòng nào.

**Lưu ý**

- AI Office vẫn đọc danh sách bảng và **số dòng ước tính** của mỗi bảng từ catalog hệ thống để AI biết database có gì. Số dòng này không bị Row-Level Security lọc. Không đặt dữ liệu nhạy cảm vào tên bảng hay tên cột.
- Phạm vi được lưu cùng câu hỏi. Gửi lại cùng `requestId` nhưng khác phạm vi sẽ bị trả `409 request_conflict`.

## Ví dụ Node.js (18+)

```js
import crypto from "node:crypto";

const BASE = "https://api.finora.io.vn";
const CLIENT_ID = process.env.AIOFFICE_CLIENT_ID;
const KEY = Buffer.from(process.env.AIOFFICE_SIGNING_KEY, "base64");

async function call(method, pathAndQuery, bodyObject) {
  const body = bodyObject ? Buffer.from(JSON.stringify(bodyObject), "utf8") : Buffer.alloc(0);
  const timestamp = Math.floor(Date.now() / 1000).toString();
  const nonce = crypto.randomUUID();
  const bodyHash = crypto.createHash("sha256").update(body).digest("hex").toUpperCase();
  const toSign = ["aioffice-erp-chat-v1", CLIENT_ID, method, pathAndQuery, timestamp, nonce, bodyHash].join("\n");
  const signature = crypto.createHmac("sha256", KEY).update(toSign, "ascii").digest("hex");
  const response = await fetch(BASE + pathAndQuery, {
    method,
    headers: { "Content-Type": "application/json", "X-AIOffice-Client": CLIENT_ID, "X-AIOffice-Timestamp": timestamp,
      "X-AIOffice-Nonce": nonce, "X-AIOffice-Signature": signature },
    body: bodyObject ? body : undefined,
  });
  return { status: response.status, json: await response.json().catch(() => null) };
}

const asked = await call("POST", "/api/erp-chat/v1/messages",
  { requestId: crypto.randomUUID(), externalUserId: "nv001", question: "Doanh thu tháng này?" });
const path = `/api/erp-chat/v1/messages/${asked.json.messageId}?externalUserId=${encodeURIComponent("nv001")}`;
let result;
do { await new Promise(r => setTimeout(r, 2000)); result = await call("GET", path); } while (result.json?.status === "pending");
console.log(result.json.answer ?? result.json.failure);
```

## Ví dụ C# (.NET 8+)

```csharp
using System.Security.Cryptography;
using System.Text;

static HttpRequestMessage Signed(HttpMethod method, string pathAndQuery, string? json, Guid clientId, byte[] key)
{
    var body = json is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json);
    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
    var nonce = Guid.NewGuid().ToString("D");
    var toSign = string.Join('\n', "aioffice-erp-chat-v1", clientId.ToString("D"), method.Method, pathAndQuery, timestamp, nonce,
        Convert.ToHexString(SHA256.HashData(body)));
    var signature = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(toSign)));
    var request = new HttpRequestMessage(method, "https://api.finora.io.vn" + pathAndQuery);
    if (json is not null) request.Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } };
    request.Headers.Add("X-AIOffice-Client", clientId.ToString("D"));
    request.Headers.Add("X-AIOffice-Timestamp", timestamp);
    request.Headers.Add("X-AIOffice-Nonce", nonce);
    request.Headers.Add("X-AIOffice-Signature", signature);
    return request;
}
```

## Sắp có

- Trả lời từng chữ (streaming), thay cho việc hỏi lại mỗi 2 giây.
- Ghi, xoá dữ liệu: AI chỉ đề xuất, người dùng xác nhận rồi mới chạy, và chỉ qua các thủ tục đã được duyệt sẵn.
