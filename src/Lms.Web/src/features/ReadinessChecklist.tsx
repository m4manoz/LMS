import { CircleAlert, CircleCheck } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import type { Check } from './courseAuthoring'

const tabName: Record<string, string> = { outline: 'Outline', details: 'Details' }

/**
 * What is needed before a course can be sent for review, in two plain groups: what is still to do (with a button that goes to where it is done)
 * and what is already done. A line says how many of the required items are finished.
 */
export function ReadinessChecklist({ checks, onGo }: { checks: Check[]; onGo?: (tab: string) => void }) {
  const required = checks.filter((check) => check.required)
  const doneRequired = required.filter((check) => check.ok).length
  const todo = checks.filter((check) => !check.ok)
  const done = checks.filter((check) => check.ok)
  const allRequired = doneRequired === required.length
  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-1.5">
        <p className="text-sm font-medium" role="status">{doneRequired} of {required.length} required items done{allRequired ? ' — ready to submit' : ''}</p>
        <div className="h-2 overflow-hidden rounded-full bg-muted" aria-hidden>
          <div className={`h-2 rounded-full transition-all ${allRequired ? 'bg-emerald-500' : 'bg-primary'}`} style={{ width: `${required.length === 0 ? 100 : Math.round((doneRequired / required.length) * 100)}%` }} />
        </div>
      </div>

      {todo.length > 0 ? (
        <section aria-label="Still to do" className="flex flex-col gap-2">
          <h4 className="text-sm font-semibold text-amber-600 dark:text-amber-400">Still to do ({todo.length})</h4>
          <ul className="flex flex-col gap-2">
            {todo.map((check) => (
              <li key={check.id} className="flex items-start gap-3 rounded-md border border-amber-500/50 bg-amber-500/10 p-3">
                <CircleAlert className="mt-0.5 h-5 w-5 shrink-0 text-amber-500" aria-hidden />
                <div className="min-w-0 flex-1">
                  <p className="flex flex-wrap items-center gap-2 text-sm font-medium">{check.label}<Badge variant={check.required ? 'destructive' : 'outline'}>{check.required ? 'Required' : 'Optional'}</Badge></p>
                  <p className="text-sm text-muted-foreground">{check.hint}</p>
                </div>
                {onGo && check.tab ? <Button type="button" size="sm" variant="secondary" aria-label={`Go to ${tabName[check.tab] ?? check.tab} to finish: ${check.label}`} onClick={() => onGo(check.tab!)}>Go to {tabName[check.tab] ?? check.tab}</Button> : null}
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      {done.length > 0 ? (
        <section aria-label="Done" className="flex flex-col gap-2">
          <h4 className="text-sm font-semibold text-emerald-600 dark:text-emerald-400">Done ({done.length})</h4>
          <ul className="flex flex-col gap-1.5">
            {done.map((check) => (
              <li key={check.id} className="flex items-center gap-3 rounded-md border border-emerald-500/40 bg-emerald-500/10 px-3 py-2">
                <CircleCheck className="h-5 w-5 shrink-0 text-emerald-500" aria-hidden />
                <span className="text-sm">{check.label}{check.required ? null : <small className="ml-2 text-muted-foreground">(optional)</small>}</span>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </div>
  )
}
