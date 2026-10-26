import { botChannelBff } from "../../../../lib/bot-channel-bff";
export async function GET(request: Request) { return botChannelBff(request); }
