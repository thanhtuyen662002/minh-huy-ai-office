import { mutateLocalDataSource } from "../../../../../../lib/local-data-source-mutation";
import { isCanonicalCompanyId } from "../../../../../../lib/company-scope";
import { isErpConnection } from "../../../../../../lib/customer-erp";

export async function PUT(request: Request, context: { params: Promise<{ id: string }> }) {
  const { id } = await context.params;
  if (!isCanonicalCompanyId(id)) return Response.json({ code: "invalid_fields" }, { status: 400, headers: { "Cache-Control": "no-store" } });
  return mutateLocalDataSource(request, "PUT", `/api/data-sources/${id}/credentials`,
    { validate: value => isErpConnection(value, true), maxBodyBytes: 16384, timeoutMs: 20000 });
}
