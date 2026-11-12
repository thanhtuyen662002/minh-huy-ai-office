import { mutateLocalDataSource } from "../../../../../../lib/local-data-source-mutation";
import { isCanonicalCompanyId } from "../../../../../../lib/company-scope";
import { isErpServerSave } from "../../../../../../lib/customer-erp";

// Changing a server re-checks every database on it, so the proxy waits longer than a single test.
export async function PUT(request: Request, context: { params: Promise<{ serverId: string }> }) {
  const { serverId } = await context.params;
  if (!isCanonicalCompanyId(serverId)) return Response.json({ code: "invalid_fields" }, { status: 400, headers: { "Cache-Control": "no-store" } });
  return mutateLocalDataSource(request, "PUT", `/api/data-sources/erp-servers/${serverId}`,
    { validate: value => isErpServerSave(value) && Object.hasOwn(value as object, "version"), maxBodyBytes: 16384, timeoutMs: 55000 });
}
