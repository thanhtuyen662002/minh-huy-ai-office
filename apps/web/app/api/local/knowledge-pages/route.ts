import { knowledgeBff } from "../../../../lib/knowledge-bff";
export async function GET(request: Request) { return knowledgeBff(request); }
export async function POST(request: Request) { return knowledgeBff(request); }
