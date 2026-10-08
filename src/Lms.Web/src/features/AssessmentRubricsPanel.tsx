import { useCallback, useEffect, useState } from 'react'
import { Plus, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, RowList } from '@/components/form'
import SidePanel from '@/components/SidePanel'
import { ApiError, apiRequest } from '@/lib/api'
import type { Rubric } from '@/lib/assessments'

type Level = { label: string; points: string }
type Criterion = { id?: string; name: string; description: string; levels: Level[] }
type Form = { name: string; criteria: Criterion[] }

const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)
const blankCriterion = (): Criterion => ({ name: '', description: '', levels: [{ label: 'Full marks', points: '4' }, { label: 'Partly', points: '2' }, { label: 'Not shown', points: '0' }] })
const blankForm = (): Form => ({ name: '', criteria: [blankCriterion()] })

const formTotal = (form: Form) => form.criteria.reduce((sum, criterion) => sum + Math.max(0, ...criterion.levels.map((level) => Number(level.points) || 0)), 0)

/** A course's rubrics: scoring guides for essay and file questions. A rubric that questions use is frozen; copy it to change it. */
export default function AssessmentRubricsPanel({ courseId }: { courseId: string }) {
  const [rubrics, setRubrics] = useState<Rubric[] | null>(null)
  const [open, setOpen] = useState(false)
  const [editing, setEditing] = useState<Rubric | null>(null)
  const [form, setForm] = useState<Form>(blankForm)
  const [problem, setProblem] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try { const list = await apiRequest<Rubric[]>(`/api/v1/tenant/courses/${courseId}/rubrics`); setRubrics(Array.isArray(list) ? list : []) }
    catch (exception) { setError(readError(exception, 'Unable to load the rubrics.')) }
  }, [courseId])
  useEffect(() => { setRubrics(null); setOpen(false); void load() }, [load])

  const toForm = (rubric: Rubric, copy: boolean): Form => ({
    name: copy ? `${rubric.name} (copy)` : rubric.name,
    criteria: rubric.criteria.map((criterion) => ({ id: copy ? undefined : criterion.id, name: criterion.name, description: criterion.description ?? '', levels: criterion.levels.map((level) => ({ label: level.label, points: String(level.points) })) })),
  })
  const startNew = () => { setEditing(null); setForm(blankForm()); setProblem(null); setNotice(null); setOpen(true) }
  const startEdit = (rubric: Rubric, copy = false) => { setEditing(copy ? null : rubric); setForm(toForm(rubric, copy)); setProblem(null); setNotice(null); setOpen(true) }

  const setCriterion = (index: number, change: Partial<Criterion>) => setForm((current) => ({ ...current, criteria: current.criteria.map((item, at) => (at === index ? { ...item, ...change } : item)) }))
  const setLevel = (index: number, level: number, change: Partial<Level>) => setForm((current) => ({ ...current, criteria: current.criteria.map((item, at) => (at === index ? { ...item, levels: item.levels.map((existing, l) => (l === level ? { ...existing, ...change } : existing)) } : item)) }))

  async function save(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!form.name.trim()) return setProblem('Enter a name for the rubric.')
    for (const criterion of form.criteria) {
      if (!criterion.name.trim()) return setProblem('Every criterion needs a name.')
      if (criterion.levels.length < 2) return setProblem(`“${criterion.name}” needs at least two levels.`)
      if (criterion.levels.some((level) => !level.label.trim() || level.points.trim() === '' || !Number.isInteger(Number(level.points)) || Number(level.points) < 0))
        return setProblem(`Every level of “${criterion.name}” needs a label and whole-number points.`)
      if (new Set(criterion.levels.map((level) => Number(level.points))).size !== criterion.levels.length) return setProblem(`The levels of “${criterion.name}” must have different points.`)
    }
    const total = formTotal(form)
    if (total < 1 || total > 100) return setProblem('The criteria together must be worth between 1 and 100 points.')
    setBusy(true); setProblem(null)
    try {
      const body = JSON.stringify({
        name: form.name.trim(),
        criteria: form.criteria.map((criterion) => ({ id: criterion.id, name: criterion.name.trim(), description: criterion.description.trim() || null, levels: criterion.levels.map((level) => ({ label: level.label.trim(), points: Number(level.points) })) })),
      })
      if (editing) await apiRequest(`/api/v1/tenant/rubrics/${editing.id}`, { method: 'PUT', body })
      else await apiRequest(`/api/v1/tenant/courses/${courseId}/rubrics`, { method: 'POST', body })
      setOpen(false); setNotice(`Rubric “${form.name.trim()}” saved.`); await load()
    } catch (exception) { setProblem(readError(exception, 'Unable to save the rubric.')) } finally { setBusy(false) }
  }

  async function remove(rubric: Rubric) {
    if (!window.confirm(`Delete the rubric “${rubric.name}”?`)) return
    setBusy(true); setError(null); setNotice(null)
    try { await apiRequest(`/api/v1/tenant/rubrics/${rubric.id}`, { method: 'DELETE' }); setNotice('Rubric deleted.'); await load() }
    catch (exception) { setError(readError(exception, 'Unable to delete the rubric.')) } finally { setBusy(false) }
  }

  return (
    <section className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-muted-foreground">Rubrics let you mark essays and uploaded work criterion by criterion.</p>
        <Button onClick={startNew}><Plus className="mr-1 h-4 w-4" aria-hidden />New rubric</Button>
      </div>
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />
      {rubrics === null ? <p role="status" className="text-sm text-muted-foreground">Loading…</p>
        : rubrics.length === 0 ? <EmptyState>No rubrics for this course yet.</EmptyState>
          : (
            <RowList label="Rubrics">
              {rubrics.map((rubric) => (
                <ListRow key={rubric.id} columns="md:grid-cols-[minmax(0,2fr)_120px_120px_auto]">
                  <div className="min-w-0">
                    <strong className="block truncate">{rubric.name}</strong>
                    <small className="text-muted-foreground">{rubric.criteria.map((criterion) => criterion.name).join(' · ')}</small>
                  </div>
                  <span className="text-xs text-muted-foreground">{rubric.totalPoints} points</span>
                  <span className="text-xs text-muted-foreground">{rubric.usedByQuestions === 0 ? 'Not used yet' : `Used by ${rubric.usedByQuestions} question${rubric.usedByQuestions === 1 ? '' : 's'}`}</span>
                  <div className="flex gap-2">
                    {rubric.usedByQuestions === 0 ? <Button size="sm" variant="outline" aria-label={`Edit rubric ${rubric.name}`} onClick={() => startEdit(rubric)}>Edit</Button> : null}
                    <Button size="sm" variant="outline" aria-label={`Copy rubric ${rubric.name}`} onClick={() => startEdit(rubric, true)}>Copy</Button>
                    {rubric.usedByQuestions === 0 ? <Button size="sm" variant="outline" disabled={busy} aria-label={`Delete rubric ${rubric.name}`} onClick={() => void remove(rubric)}>Delete</Button> : null}
                  </div>
                </ListRow>
              ))}
            </RowList>
          )}

      <SidePanel open={open} label={editing ? 'Edit rubric' : 'New rubric'} onClose={() => setOpen(false)}>
        <FormLayout onSubmit={(event) => void save(event)} noValidate>
          <ErrorBanner message={problem} />
          <FormSection title="Rubric" divider={false}>
            <Field id="rubric-name" label="Rubric name" required><Input id="rubric-name" maxLength={200} value={form.name} onChange={(event) => setForm({ ...form, name: event.target.value })} /></Field>
          </FormSection>
          {form.criteria.map((criterion, index) => (
            <FormSection key={index} title={`Criterion ${index + 1}`}>
              <Field id={`criterion-${index}-name`} label={`Criterion ${index + 1} name`} required><Input id={`criterion-${index}-name`} maxLength={200} value={criterion.name} onChange={(event) => setCriterion(index, { name: event.target.value })} /></Field>
              <Field id={`criterion-${index}-description`} label={`Criterion ${index + 1} description`}><Input id={`criterion-${index}-description`} value={criterion.description} onChange={(event) => setCriterion(index, { description: event.target.value })} /></Field>
              <div className="flex flex-col gap-2">
                {criterion.levels.map((level, at) => (
                  <div key={at} className="flex items-end gap-2">
                    <Field id={`criterion-${index}-level-${at}-label`} label="Level" className="flex-1"><Input id={`criterion-${index}-level-${at}-label`} aria-label={`Criterion ${index + 1} level ${at + 1} label`} maxLength={100} value={level.label} onChange={(event) => setLevel(index, at, { label: event.target.value })} /></Field>
                    <Field id={`criterion-${index}-level-${at}-points`} label="Points" className="w-24"><Input id={`criterion-${index}-level-${at}-points`} aria-label={`Criterion ${index + 1} level ${at + 1} points`} type="number" min="0" max="100" value={level.points} onChange={(event) => setLevel(index, at, { points: event.target.value })} /></Field>
                    <Button type="button" variant="outline" size="icon" aria-label={`Remove level ${at + 1} of criterion ${index + 1}`} disabled={criterion.levels.length <= 2} onClick={() => setCriterion(index, { levels: criterion.levels.filter((_, l) => l !== at) })}><X className="h-4 w-4" aria-hidden /></Button>
                  </div>
                ))}
                <div className="flex gap-2">
                  <Button type="button" size="sm" variant="outline" disabled={criterion.levels.length >= 8} onClick={() => setCriterion(index, { levels: [...criterion.levels, { label: '', points: '' }] })}>Add level</Button>
                  <Button type="button" size="sm" variant="outline" disabled={form.criteria.length <= 1} onClick={() => setForm({ ...form, criteria: form.criteria.filter((_, at) => at !== index) })}>Remove criterion</Button>
                </div>
              </div>
            </FormSection>
          ))}
          <div className="flex items-center justify-between gap-2">
            <Button type="button" variant="outline" disabled={form.criteria.length >= 20} onClick={() => setForm({ ...form, criteria: [...form.criteria, blankCriterion()] })}>Add criterion</Button>
            <span className="text-sm text-muted-foreground">Total: {formTotal(form)} points</span>
          </div>
          <FormActions busy={busy} submitLabel="Save rubric" onCancel={() => setOpen(false)} />
        </FormLayout>
      </SidePanel>
    </section>
  )
}
