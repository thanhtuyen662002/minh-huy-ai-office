import { isCanonicalCompanyId } from "../../../../../../lib/local-ai-bff";
import { mutateLocalDataSource } from "../../../../../../lib/local-data-source-mutation";

export async function PUT(request: Request, context: { params: Promise<{ dataSourceId: string }> }) {
  const { dataSourceId } = await context.params;
  if (!isCanonicalCompanyId(dataSourceId)) return Response.json({ error: "Invalid source selection." }, {
    status: 400, headers: { "Cache-Control": "no-store" },
  });
  return mutateLocalDataSource(request, "PUT", `/api/data-sources/${encodeURIComponent(dataSourceId)}/metadata`);
}
