// Existing browser proofs now finish ordinary identity-first login explicitly;
// selected-company switching remains its original v1 Code/S256 route.
export async function finishCompanyDiscovery(page, app, target, response) {
  const refused = () => new Error("Owned company discovery proof failed.");
  if (response.status() !== 303) throw refused();
  const location = response.headers().location;
  if (location === app + "/sign-in/company") {
    await page.getByRole("heading", { name: "Chọn công ty để tiếp tục", exact: true }).waitFor();
    if (await page.locator('textarea[name="question"]').count()
      || await page.getByRole("combobox", { name: "Nguồn dữ liệu", exact: true }).count()) throw refused();
    const directory = await page.evaluate(async () => {
      const response = await fetch("/api/local/session/oidc/company", { cache: "no-store" });
      return { status: response.status, cache: response.headers.get("cache-control"), value: await response.json() };
    });
    if (directory.status !== 200 || directory.cache !== "no-store" || !Array.isArray(directory.value?.items)) throw refused();
    const selected = directory.value.items.filter((item) => item.companyId === target);
    if (selected.length !== 1 || typeof selected[0].companyName !== "string") throw refused();
    const completion = page.waitForResponse((reply) => new URL(reply.url()).pathname === "/api/local/session/oidc/company" && reply.request().method() === "POST");
    completion.catch(() => {});
    await page.getByRole("button", { name: selected[0].companyName, exact: true }).click();
    if ((await completion).status() !== 200) throw refused();
  } else if (location !== app + "/") throw refused();
  await page.waitForURL((url) => url.origin === app && url.pathname === "/");
}
