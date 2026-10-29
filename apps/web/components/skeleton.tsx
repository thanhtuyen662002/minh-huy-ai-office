import type { ReactNode } from "react";

// Placeholder shapes shown while data loads; the label stays readable for screen readers and tests.
export function Skeleton({ className = "" }: { className?: string }) {
  return <span aria-hidden="true" className={`block animate-pulse rounded-md bg-muted ${className}`} />;
}

export function Loading({ label, className = "", children }: { label: string; className?: string; children: ReactNode }) {
  return <div role="status" aria-live="polite" className={className}><span className="sr-only">{label}</span>{children}</div>;
}

export function SkeletonRows({ label, rows = 4, className = "" }: { label: string; rows?: number; className?: string }) {
  return <Loading label={label} className={`space-y-3 ${className}`}>
    {Array.from({ length: rows }, (_, index) => <div key={index} className="flex items-center gap-3">
      <Skeleton className="size-9 shrink-0 rounded-lg" />
      <div className="min-w-0 flex-1 space-y-2"><Skeleton className={`h-3.5 ${index % 2 ? "w-2/3" : "w-4/5"}`} /><Skeleton className="h-3 w-1/3" /></div>
    </div>)}
  </Loading>;
}

export function BoardSkeleton({ label, columns = 4 }: { label: string; columns?: number }) {
  return <Loading label={label} className="grid auto-cols-[minmax(15rem,1fr)] grid-flow-col gap-3 overflow-hidden pb-2">
    {Array.from({ length: columns }, (_, column) => <div key={column} className="flex min-h-48 flex-col gap-2 rounded-xl bg-muted/70 p-2.5">
      <Skeleton className="mx-1 my-1 h-4 w-24 bg-muted-foreground/15" />
      {Array.from({ length: 3 - (column % 2) }, (_, card) => <div key={card} className="space-y-2.5 rounded-lg border bg-card p-3">
        <Skeleton className="h-3 w-16" /><Skeleton className="h-3.5 w-full" /><Skeleton className="h-3.5 w-3/4" />
        <div className="flex justify-between pt-1"><Skeleton className="h-3 w-20" /><Skeleton className="size-5 rounded-full" /></div>
      </div>)}
    </div>)}
  </Loading>;
}
