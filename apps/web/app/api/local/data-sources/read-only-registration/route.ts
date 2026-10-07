import { mutateLocalDataSource } from "../../../../../lib/local-data-source-mutation";
import { isRegistrationInput } from "../../../../../lib/source-registration";

export async function POST(request: Request) {
  return mutateLocalDataSource(request, "POST", "/api/data-sources/read-only-registration",
    { validate: isRegistrationInput, maxBodyBytes: 8192 });
}
