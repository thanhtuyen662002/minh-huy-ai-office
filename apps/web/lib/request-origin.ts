/** Browser cookie mutations require the authority actually addressed by the browser. */
export function hasSameOrigin(request: Request): boolean {
  try {
    const url = new URL(request.url);
    if (url.protocol !== "http:" && url.protocol !== "https:") return false;
    let expected = url.origin;
    const host = request.headers.get("host");
    if (host) {
      // Reject raw authority delimiters before URL parsing can erase paths,
      // dot segments, backslashes or escaped authority characters.
      if (/[\s\\@?#,%]/.test(host) || host.includes("/")) return false;
      // Next.js can build request.url from the container hostname. Never use
      // caller-supplied forwarded hosts to authorize access to a session cookie.
      const authority = new URL(`${url.protocol}//${host}`);
      if (authority.username || authority.password || authority.pathname !== "/" || authority.search || authority.hash) return false;
      expected = authority.origin;
    }
    return request.headers.get("origin") === expected;
  } catch {
    return false;
  }
}
