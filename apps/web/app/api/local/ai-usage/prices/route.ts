import { mutateLocalDataSource } from "../../../../../lib/local-data-source-mutation";
import { isAiPriceSave } from "../../../../../lib/ai-usage";

export async function POST(request: Request) {
  return mutateLocalDataSource(request, "POST", "/api/ai-usage/prices", { validate: isAiPriceSave, maxBodyBytes: 4096 });
}
