import { mutateLocalDataSource } from "../../../../../../lib/local-data-source-mutation";
import { isCanonicalCompanyId } from "../../../../../../lib/company-scope";
import { isErpChatClientUpdate } from "../../../../../../lib/erp-chat";

export async function PUT(request: Request, context: { params: Promise<{ clientId: string }> }) {
  const { clientId } = await context.params;
  if (!isCanonicalCompanyId(clientId)) return Response.json({ code: "invalid_fields" }, { status: 400, headers: { "Cache-Control": "no-store" } });
  return mutateLocalDataSource(request, "PUT", `/api/erp-chat/clients/${clientId}`, { validate: isErpChatClientUpdate, maxBodyBytes: 1024 });
}
