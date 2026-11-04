import { assistantImageBff } from "../../../../../lib/assistant-bff";
export async function GET(request: Request, { params }: { params: Promise<{ id: string }> }) { return assistantImageBff(request, (await params).id); }
