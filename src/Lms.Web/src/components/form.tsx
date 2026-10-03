import type { ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { cn } from '@/lib/utils'

/**
 * Shared building blocks so every screen looks and behaves like course authoring.
 * See docs/ui-patterns.md for how they fit together.
 */

/** Page title and description on the left, the main action (usually "New …") on the right. */
export function PageHeader({ title, description, actions }: { title: string; description?: string; actions?: ReactNode }) {
  return (
    <div className="flex flex-wrap items-end justify-between gap-3">
      <div>
        <h2 className="text-2xl font-semibold">{title}</h2>
        {description ? <p className="text-sm text-muted-foreground">{description}</p> : null}
      </div>
      {actions ? <div className="flex items-center gap-3">{actions}</div> : null}
    </div>
  )
}

export function ErrorBanner({ message }: { message: string | null | undefined }) {
  return message ? <div role="alert" className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">{message}</div> : null
}

export function NoticeBanner({ message }: { message: string | null | undefined }) {
  return message ? <div role="status" className="rounded-md border border-emerald-500/40 bg-emerald-500/10 px-3 py-2 text-sm text-emerald-300">{message}</div> : null
}

/** A form: sections stacked with space between, width capped for readability. */
/** noValidate: the form checks its own fields and says what is wrong in its own words, instead of the browser's pop-up. */
export function FormLayout({ onSubmit, children, className, noValidate }: { onSubmit: (event: React.FormEvent<HTMLFormElement>) => void; children: ReactNode; className?: string; noValidate?: boolean }) {
  return <form className={cn('flex max-w-3xl flex-col gap-6', className)} onSubmit={onSubmit} noValidate={noValidate}>{children}</form>
}

/** A titled group of related fields. Every section after the first gets a divider above it. */
export function FormSection({ title, description, children, disabled, divider = true }: { title: string; description?: string; children: ReactNode; disabled?: boolean; divider?: boolean }) {
  return (
    <fieldset className={cn('flex flex-col gap-3', divider && 'border-t border-border pt-5 first:border-t-0 first:pt-0')} disabled={disabled}>
      <legend className="mb-1 text-sm font-semibold">{title}</legend>
      {description ? <p className="-mt-1 text-xs text-muted-foreground">{description}</p> : null}
      {children}
    </fieldset>
  )
}

/** A label, its control, an optional hint and an optional error. The control must carry the same `id`. A red asterisk marks required fields. */
export function Field({ id, label, required, hint, error, className, children }: { id: string; label: string; required?: boolean; hint?: string; error?: string | null; className?: string; children: ReactNode }) {
  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <Label htmlFor={id} required={required}>{label}</Label>
      {children}
      {error ? <small role="alert" className="text-destructive">{error}</small> : hint ? <small className="text-muted-foreground">{hint}</small> : null}
    </div>
  )
}

/** The form's footer: the main button first, then Cancel, under a divider. */
export function FormActions({ busy, submitLabel, busyLabel = 'Saving…', disabled, onCancel, children }: { busy?: boolean; submitLabel: string; busyLabel?: string; disabled?: boolean; onCancel?: () => void; children?: ReactNode }) {
  return (
    <div className="flex flex-wrap items-center gap-2 border-t border-border pt-5">
      <Button type="submit" disabled={busy || disabled}>{busy ? busyLabel : submitLabel}</Button>
      {onCancel ? <Button type="button" variant="outline" onClick={onCancel}>Cancel</Button> : null}
      {children}
    </div>
  )
}

/** The container for a list of full-width rows. */
export function RowList({ label, children }: { label: string; children: ReactNode }) {
  return <div className="flex flex-col gap-2" role="list" aria-label={label}>{children}</div>
}

/** One full-width row. Pass `columns` as a Tailwind grid template for wide screens, for example `md:grid-cols-[minmax(0,2fr)_110px_auto]`. A left accent bar marks the row whose details are open. */
export function ListRow({ selected, columns = 'md:grid-cols-[minmax(0,1fr)_auto]', children }: { selected?: boolean; columns?: string; children: ReactNode }) {
  return (
    <div role="listitem" className={cn('grid w-full items-center gap-x-4 gap-y-2 rounded-md border border-border px-4 py-3 text-sm', columns, selected && 'border-l-4 border-l-primary bg-muted')}>
      {children}
    </div>
  )
}

export function EmptyState({ children }: { children: ReactNode }) {
  return <p className="rounded-md border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">{children}</p>
}
