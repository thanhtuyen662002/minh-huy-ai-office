import { mutateLocalDataSource } from "../../../../../../lib/local-data-source-mutation";
import { isCanonicalCompanyId } from "../../../../../../lib/company-scope";
import { isAiAccountSave } from "../../../../../../lib/ai-usage";

export async function PUT(request: Request, context: { params: Promise<{ accountId: string }> }) {
  const { accountId } = await context.params;
  if (!isCanonicalCompanyId(accountId)) return Response.json({ code: "invalid_fields" }, { status: 400, headers: { "Cache-Control": "no-store" } });
  return mutateLocalDataSource(request, "PUT", `/api/ai-usage/accounts/${accountId}`,
    { validate: value => isAiAccountSave(value) && typeof (value as { version: unknown }).version === "string", maxBodyBytes: 65536 });
}
