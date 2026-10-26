import { customerRequestBff } from "../../../../lib/customer-request-bff";
export async function GET(request: Request) { return customerRequestBff(request); }
