import { mutateLocalDataSource } from "../../../../../lib/local-data-source-mutation";
import { isErpConnection } from "../../../../../lib/customer-erp";

export async function POST(request: Request) {
  return mutateLocalDataSource(request, "POST", "/api/data-sources/connection-test",
    { validate: isErpConnection, maxBodyBytes: 16384, timeoutMs: 20000 });
}
