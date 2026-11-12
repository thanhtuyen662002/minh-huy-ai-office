import { mutateLocalDataSource } from "../../../../../lib/local-data-source-mutation";
import { isAiAccountSave } from "../../../../../lib/ai-usage";

export async function POST(request: Request) {
  return mutateLocalDataSource(request, "POST", "/api/ai-usage/accounts",
    { validate: value => isAiAccountSave(value) && (value as { version: unknown }).version === null, maxBodyBytes: 65536 });
}
