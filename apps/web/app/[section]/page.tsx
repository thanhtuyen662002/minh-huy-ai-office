import { notFound } from "next/navigation";
import { renderWorkspace } from "../workspace-entry";
import { defaultWorkspaceSection, isWorkspaceSection } from "../../lib/workspace-sections";

export const dynamic = "force-dynamic";

export default async function SectionPage({ params }: { params: Promise<{ section: string }> }) {
  const { section } = await params;
  if (!isWorkspaceSection(section) || section === defaultWorkspaceSection) notFound();
  return renderWorkspace(section);
}
