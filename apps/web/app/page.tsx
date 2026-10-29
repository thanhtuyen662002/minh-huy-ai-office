import { renderWorkspace } from "./workspace-entry";

// Setup supplies company settings when the container starts, after the image is built.
export const dynamic = "force-dynamic";

export default async function Home() {
  return renderWorkspace("tong-quan");
}
