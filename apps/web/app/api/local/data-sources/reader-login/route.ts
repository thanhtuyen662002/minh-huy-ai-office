import { mutateLocalDataSource } from "../../../../../lib/local-data-source-mutation";
import { isErpConnection } from "../../../../../lib/customer-erp";

// Administrator credentials pass through once to create a read-only login; nothing here stores them.
export async function POST(request: Request) {
  return mutateLocalDataSource(request, "POST", "/api/data-sources/reader-login",
    { validate: isErpConnection, maxBodyBytes: 16384, timeoutMs: 20000 });
}
