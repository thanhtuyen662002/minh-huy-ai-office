import { botChannelBff } from "../../../../../lib/bot-channel-bff";
type Context = { params: Promise<{ segments: string[] }> };
export async function GET(request: Request, context: Context) { return botChannelBff(request, (await context.params).segments); }
export async function PUT(request: Request, context: Context) { return botChannelBff(request, (await context.params).segments); }
export async function POST(request: Request, context: Context) { return botChannelBff(request, (await context.params).segments); }
