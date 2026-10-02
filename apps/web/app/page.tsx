import { LocalAiWorkspace } from "../components/local-ai-workspace";

const DEFAULT_LOCAL_COMPANY_ID = "22222222-2222-2222-2222-222222222222";

export default function Home() {
  const companyId = process.env.AIOFFICE_LOCAL_COMPANY_ID ?? DEFAULT_LOCAL_COMPANY_ID;
  const companyName = process.env.AIOFFICE_LOCAL_COMPANY_NAME ?? "Minh Huy";
  return <LocalAiWorkspace companyId={companyId} companyName={companyName} />;
}
