import { CompanyPreselection } from "../../../components/company-preselection";

export const dynamic = "force-dynamic";

// A provider-authenticated identity is not a company session. In particular,
// do not render Home/LocalAiWorkspace from an old SID while choosing.
export default function ChooseCompanyPage() {
  return <CompanyPreselection />;
}
