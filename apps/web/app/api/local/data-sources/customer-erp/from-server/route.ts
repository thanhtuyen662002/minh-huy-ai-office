import { mutateLocalDataSource } from "../../../../../../lib/local-data-source-mutation";
import { isErpServerSource } from "../../../../../../lib/customer-erp";

export async function POST(request: Request) {
  return mutateLocalDataSource(request, "POST", "/api/data-sources/customer-erp/from-server",
    { validate: value => isErpServerSource(value, true), maxBodyBytes: 16384, timeoutMs: 55000 });
}
