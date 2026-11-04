import { ImagePlus, X } from "lucide-react";
import { assistantImageUrl, maxAssistantImages, splitQuestion } from "../lib/assistant-images";

export type PendingImage = Readonly<{ key: string; blob: Blob; url: string }>;

/** Pictures waiting to be sent with the next question, each removable. */
export function PendingImages({ images, onRemove, disabled }: { images: readonly PendingImage[]; onRemove: (key: string) => void; disabled?: boolean }) {
  if (!images.length) return null;
  return <ul aria-label="Ảnh đính kèm" className="flex flex-wrap gap-2 px-2 pt-1">
    {images.map((image, index) => <li key={image.key} className="relative">
      <img src={image.url} alt={`Ảnh ${index + 1}`} className="size-16 rounded-lg border object-cover" />
      <button type="button" disabled={disabled} onClick={() => onRemove(image.key)} aria-label={`Bỏ ảnh ${index + 1}`}
        className="absolute -right-1.5 -top-1.5 flex size-5 items-center justify-center rounded-full bg-foreground text-background shadow disabled:opacity-50">
        <X className="size-3" aria-hidden="true" />
      </button>
    </li>)}
  </ul>;
}

export function AttachButton({ count, disabled, onFiles }: { count: number; disabled?: boolean; onFiles: (files: File[]) => void }) {
  return <label title={`Đính kèm ảnh (tối đa ${maxAssistantImages})`}
    className={`flex size-10 shrink-0 cursor-pointer items-center justify-center rounded-xl text-muted-foreground hover:bg-muted hover:text-foreground ${disabled || count >= maxAssistantImages ? "pointer-events-none opacity-40" : ""}`}>
    <ImagePlus className="size-5" aria-hidden="true" />
    <span className="sr-only">Đính kèm ảnh</span>
    <input type="file" accept="image/*" multiple className="sr-only" disabled={disabled || count >= maxAssistantImages}
      onChange={event => { const files = [...(event.currentTarget.files ?? [])]; event.currentTarget.value = ""; if (files.length) onFiles(files); }} />
  </label>;
}

/** A question as the asker sent it: its pictures (opening full size) and its words. */
export function UserQuestion({ text, companyId }: { text: string; companyId: string }) {
  const { text: words, imageIds } = splitQuestion(text);
  return <>
    {imageIds.length ? <div className={`grid gap-1.5 ${imageIds.length === 1 ? "grid-cols-1" : "grid-cols-2 sm:grid-cols-3"} ${words ? "mb-2" : ""}`}>
      {imageIds.map((id, index) => <a key={id} href={assistantImageUrl(companyId, id)} target="_blank" rel="noreferrer" className="block">
        <img src={assistantImageUrl(companyId, id)} alt={`Ảnh đính kèm ${index + 1}`} loading="lazy"
          className={`${imageIds.length === 1 ? "max-h-64 w-auto" : "h-24 w-full"} rounded-lg bg-primary-foreground/10 object-cover`} />
      </a>)}
    </div> : null}
    {words ? <p className="whitespace-pre-wrap text-sm leading-6">{words}</p> : null}
  </>;
}
