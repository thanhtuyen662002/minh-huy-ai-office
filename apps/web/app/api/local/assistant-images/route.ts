import { assistantImageUploadBff } from "../../../../lib/assistant-bff";
export async function POST(request: Request) { return assistantImageUploadBff(request); }
