import { useCallback, useEffect, useState } from 'react'
import { EmptyState, ErrorBanner, Field, FormLayout, FormSection, ListRow, NoticeBanner, RowList, FormActions } from '@/components/form'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { ApiError, apiRequest } from '@/lib/api'

type Prerequisite = { courseId: string; code: string; title: string }
type ModuleRule = { moduleId: string; title: string; displayOrder: number; releaseAfterDays: number | null; releaseOnUtc: string | null; requiresModuleId: string | null }
type Rules = { prerequisites: Prerequisite[]; modules: ModuleRule[] }
type CourseOption = { id: string; code: string; title: string }
type Draft = { days: string; on: string; requires: string }

/** A datetime-local value in the viewer's time zone, from an ISO instant. */
export const toLocalInput = (iso: string | null) => {
  if (!iso) return ''
  const date = new Date(iso)
  return new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16)
}

/** The request body for a module's rule; blank fields mean "no such condition". */
export const toAccessBody = (draft: Draft) => ({
  releaseAfterDays: draft.days.trim() === '' ? null : Number(draft.days),
  releaseOnUtc: draft.on ? new Date(draft.on).toISOString() : null,
  requiresModuleId: draft.requires || null,
})

export function describeRule(rule: ModuleRule, modules: ModuleRule[]): string {
  const parts: string[] = []
  if (rule.releaseAfterDays !== null) parts.push(`${rule.releaseAfterDays} day${rule.releaseAfterDays === 1 ? '' : 's'} after enrollment`)
  if (rule.releaseOnUtc) parts.push(`not before ${new Date(rule.releaseOnUtc).toLocaleString()}`)
  if (rule.requiresModuleId) parts.push(`after finishing “${modules.find((item) => item.moduleId === rule.requiresModuleId)?.title ?? 'an earlier module'}”`)
  return parts.length === 0 ? 'Always open' : `Opens ${parts.join(', ')}`
}

export default function CourseRulesPanel({ courseId }: { courseId: string }) {
  const [rules, setRules] = useState<Rules | null>(null)
  const [courses, setCourses] = useState<CourseOption[]>([])
  const [selected, setSelected] = useState<string[]>([])
  const [drafts, setDrafts] = useState<Record<string, Draft>>({})
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const apply = (next: Rules) => {
    setRules(next)
    setSelected(next.prerequisites.map((item) => item.courseId))
    setDrafts(Object.fromEntries(next.modules.map((item) => [item.moduleId, { days: item.releaseAfterDays === null ? '' : String(item.releaseAfterDays), on: toLocalInput(item.releaseOnUtc), requires: item.requiresModuleId ?? '' }])))
  }
  const load = useCallback(async () => {
    try {
      const [loaded, list] = await Promise.all([apiRequest<Rules>(`/api/v1/tenant/courses/${courseId}/access-rules`), apiRequest<CourseOption[]>('/api/v1/tenant/courses')])
      apply(loaded); setCourses(list.filter((item) => item.id !== courseId))
    } catch (exception) { setError(readError(exception, 'Unable to load the course rules.')) }
  }, [courseId])
  useEffect(() => { setMessage(null); setError(null); void load() }, [load])

  async function run(action: () => Promise<Rules>, done: string) {
    setBusy(true); setError(null); setMessage(null)
    try { apply(await action()); setMessage(done) }
    catch (exception) { setError(readError(exception, 'Unable to save.')) }
    finally { setBusy(false) }
  }

  const savePrerequisites = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    return run(() => apiRequest<Rules>(`/api/v1/tenant/courses/${courseId}/prerequisites`, { method: 'PUT', body: JSON.stringify({ courseIds: selected }) }), 'Prerequisites saved.')
  }
  const saveModule = (event: React.FormEvent<HTMLFormElement>, moduleId: string) => {
    event.preventDefault()
    const days = drafts[moduleId].days.trim()
    if (days !== '' && !(Number.isInteger(Number(days)) && Number(days) >= 0 && Number(days) <= 3650)) { setMessage(null); setError('Days after enrollment must be a whole number from 0 to 3650.'); return Promise.resolve() }
    return run(() => apiRequest<Rules>(`/api/v1/tenant/courses/${courseId}/modules/${moduleId}/access`, { method: 'PUT', body: JSON.stringify(toAccessBody(drafts[moduleId])) }), 'Module rule saved.')
  }
  const setDraft = (moduleId: string, patch: Partial<Draft>) => setDrafts({ ...drafts, [moduleId]: { ...drafts[moduleId], ...patch } })
  const toggle = (id: string) => setSelected(selected.includes(id) ? selected.filter((item) => item !== id) : [...selected, id])

  if (!rules) return <p className="text-sm text-muted-foreground">{error ?? 'Loading…'}</p>

  return (
    <div className="flex flex-col gap-6">
      <ErrorBanner message={error} />
      <NoticeBanner message={message} />

      <FormLayout onSubmit={savePrerequisites}>
        <FormSection title="Prerequisite courses" description="A learner must have completed every course ticked here before they can enroll. Being enrolled in them is not enough.">
          {courses.length === 0 ? <EmptyState>There are no other courses to choose from.</EmptyState> : (
            <div className="grid gap-1 sm:grid-cols-2">
              {courses.map((course) => (
                <label key={course.id} className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={selected.includes(course.id)} onChange={() => toggle(course.id)} />
                  {course.title} <small className="text-muted-foreground">{course.code}</small>
                </label>
              ))}
            </div>
          )}
        </FormSection>
        <FormActions busy={busy} submitLabel="Save prerequisites" />
      </FormLayout>

      <FormSection title="When each module opens" description="Closed modules show their lesson titles to learners but not their content. Where you set more than one condition, all of them must be met. Staff are never locked out.">
        {rules.modules.length === 0 ? <EmptyState>Add modules on the Outline tab first.</EmptyState> : (
          <RowList label="Modules">
            {rules.modules.map((module, index) => {
              const draft = drafts[module.moduleId]
              const earlier = rules.modules.filter((item) => item.displayOrder < module.displayOrder)
              return (
                <ListRow key={module.moduleId} columns="md:grid-cols-1">
                  <FormLayout className="gap-3" onSubmit={(event) => void saveModule(event, module.moduleId)}>
                    <div className="flex flex-wrap items-center justify-between gap-2">
                      <strong>{index + 1}. {module.title}</strong>
                      <small className="text-muted-foreground">{describeRule(module, rules.modules)}</small>
                    </div>
                    <div className="grid gap-3 sm:grid-cols-3">
                      <Field id={`days-${module.moduleId}`} label="Days after enrollment"><Input id={`days-${module.moduleId}`} type="number" min="0" max="3650" placeholder="Immediately" value={draft.days} onChange={(e) => setDraft(module.moduleId, { days: e.target.value })} /></Field>
                      <Field id={`on-${module.moduleId}`} label="Not before"><Input id={`on-${module.moduleId}`} type="datetime-local" value={draft.on} onChange={(e) => setDraft(module.moduleId, { on: e.target.value })} /></Field>
                      <Field id={`requires-${module.moduleId}`} label="Finish first">
                        <Select id={`requires-${module.moduleId}`} value={draft.requires} disabled={earlier.length === 0} onChange={(e) => setDraft(module.moduleId, { requires: e.target.value })}>
                          <option value="">Nothing</option>
                          {earlier.map((item) => <option key={item.moduleId} value={item.moduleId}>{item.title}</option>)}
                        </Select>
                      </Field>
                    </div>
                    <div className="flex gap-2">
                      <Button type="submit" variant="secondary" size="sm" disabled={busy}>Save</Button>
                      <Button type="button" variant="outline" size="sm" disabled={busy} onClick={() => void run(() => apiRequest<Rules>(`/api/v1/tenant/courses/${courseId}/modules/${module.moduleId}/access`, { method: 'PUT', body: JSON.stringify({ releaseAfterDays: null, releaseOnUtc: null, requiresModuleId: null }) }), 'Rule removed.')}>Clear</Button>
                    </div>
                  </FormLayout>
                </ListRow>
              )
            })}
          </RowList>
        )}
      </FormSection>
    </div>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
