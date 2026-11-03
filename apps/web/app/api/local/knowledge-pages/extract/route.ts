import { knowledgeExtractBff } from "../../../../../lib/knowledge-bff";
export async function POST(request: Request) { return knowledgeExtractBff(request); }
