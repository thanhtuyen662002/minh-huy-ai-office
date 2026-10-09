export const MUTATION_BODY_TIMEOUT_MS = 10_000;

type JsonResult = { ok: true; value: unknown } | { ok: false; status: 400 | 408 | 413 };

/** Bound the actual stream, including stalled reads and cleanup. Content-Length is not authority. */
export async function readBoundedRequestJson(request: Pick<Request, "headers" | "body" | "signal">, maxBytes: number, parseJson: (source: string) => unknown = JSON.parse): Promise<JsonResult> {
  if (request.headers.get("content-type")?.split(";")[0].trim().toLowerCase() !== "application/json"
    || request.signal.aborted) return { ok: false, status: 400 };
  let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
  try { reader = request.body?.getReader(); } catch { return { ok: false, status: 400 }; }
  if (!reader) return { ok: false, status: 400 };
  let timer: ReturnType<typeof setTimeout> | undefined;
  let abort: (() => void) | undefined;
  const cancel = () => { void reader!.cancel().catch(() => {}); };
  async function consume(): Promise<JsonResult> {
    const chunks: Uint8Array[] = [];
    let length = 0;
    while (true) {
      const chunk = await reader!.read();
      if (chunk.done) break;
      length += chunk.value.byteLength;
      if (length > maxBytes) { cancel(); return { ok: false, status: 413 }; }
      chunks.push(chunk.value);
    }
    const bytes = new Uint8Array(length);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    return { ok: true, value: parseJson(new TextDecoder("utf-8", { fatal: true }).decode(bytes)) };
  }
  try {
    const stopped = new Promise<JsonResult>(resolve => {
      abort = () => { resolve({ ok: false, status: 400 }); cancel(); };
      request.signal.addEventListener("abort", abort, { once: true });
      timer = setTimeout(() => { resolve({ ok: false, status: 408 }); cancel(); }, MUTATION_BODY_TIMEOUT_MS);
      if (request.signal.aborted) abort();
    });
    return await Promise.race([consume(), stopped]);
  } catch { return { ok: false, status: 400 }; }
  finally {
    clearTimeout(timer);
    if (abort) request.signal.removeEventListener("abort", abort);
    // A hostile stream's cancel promise must not hold the response or its reader lock open.
    reader.releaseLock();
  }
}
