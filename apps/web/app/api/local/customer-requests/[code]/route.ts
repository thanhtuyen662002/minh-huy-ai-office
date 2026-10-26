import { customerRequestBff } from "../../../../../lib/customer-request-bff";
type Context = { params: Promise<{ code: string }> };
export async function GET(request: Request, context: Context) { return customerRequestBff(request, (await context.params).code); }
export async function PATCH(request: Request, context: Context) { return customerRequestBff(request, (await context.params).code); }
