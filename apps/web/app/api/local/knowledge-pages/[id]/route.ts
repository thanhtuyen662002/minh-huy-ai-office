import { knowledgeBff } from "../../../../../lib/knowledge-bff";
type Context = { params: Promise<{ id: string }> };
export async function GET(request: Request, context: Context) { return knowledgeBff(request, (await context.params).id); }
export async function PUT(request: Request, context: Context) { return knowledgeBff(request, (await context.params).id); }
export async function DELETE(request: Request, context: Context) { return knowledgeBff(request, (await context.params).id); }
