import { assistantHideBff } from "../../../../../lib/assistant-bff";
export async function POST(request: Request) { return assistantHideBff(request); }
