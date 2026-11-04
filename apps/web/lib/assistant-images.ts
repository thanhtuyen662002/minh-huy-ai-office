/**
 * Pictures attached to a Trợ lý question. They are uploaded first, then travel inside the question text as
 * "[img:<id>]" tokens (the server parses the same tokens), so the existing submission rules cover them.
 */
export const maxAssistantImages = 6;
export const maxAssistantImageBytes = 5 * 1024 * 1024;
export const assistantImageTypes = ["image/jpeg", "image/png", "image/webp", "image/gif"] as const;
const tokenPattern = /\[img:([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\]/g;

export const imageToken = (id: string) => `[img:${id.toLowerCase()}]`;

/** The words of a question and the picture ids it carries, in order and without repeats. */
export function splitQuestion(question: string): { text: string; imageIds: string[] } {
  const ids: string[] = [];
  for (const match of question.matchAll(tokenPattern)) {
    const id = match[1].toLowerCase();
    if (!ids.includes(id) && ids.length < maxAssistantImages) ids.push(id);
  }
  // A summary cut mid-token leaves "[img:abc…"; drop that tail too.
  const text = question.replace(tokenPattern, " ").replace(/\[img:[0-9a-fA-F-]*…?$/, "").replace(/\s{2,}/g, " ").trim();
  return { text, imageIds: ids };
}

export const questionText = (question: string) => splitQuestion(question).text || "Ảnh đính kèm";

export const assistantImageUrl = (companyId: string, id: string) =>
  `/api/local/assistant-images/${encodeURIComponent(id)}?companyId=${encodeURIComponent(companyId)}`;

/** Longest side the model reads at full detail; bigger photos are scaled down before upload. */
const maxEdge = 1568;

/**
 * Makes a picture ready to upload: small pictures in an accepted format go as they are; large ones are
 * scaled to {@link maxEdge} and re-encoded as JPEG so they stay well under the size limit.
 */
export async function prepareAssistantImage(file: Blob): Promise<Blob> {
  if (!file.type.startsWith("image/")) throw new Error("not-image");
  const accepted = (assistantImageTypes as readonly string[]).includes(file.type);
  if (typeof createImageBitmap !== "function" || typeof document === "undefined") {
    if (!accepted || file.size > maxAssistantImageBytes) throw new Error("unsupported");
    return file;
  }
  const bitmap = await createImageBitmap(file);
  try {
    const scale = Math.min(1, maxEdge / Math.max(bitmap.width, bitmap.height));
    if (accepted && scale === 1 && file.size <= 1.5 * 1024 * 1024) return file;
    const canvas = document.createElement("canvas");
    canvas.width = Math.max(1, Math.round(bitmap.width * scale)); canvas.height = Math.max(1, Math.round(bitmap.height * scale));
    const context = canvas.getContext("2d");
    if (!context) throw new Error("unsupported");
    context.fillStyle = "#fff"; context.fillRect(0, 0, canvas.width, canvas.height);
    context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, "image/jpeg", 0.88));
    if (!blob || blob.size > maxAssistantImageBytes) throw new Error("too-large");
    return blob;
  } finally { bitmap.close(); }
}
