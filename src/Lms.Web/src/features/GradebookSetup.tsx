import { useCallback, useEffect, useState } from 'react'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, RowList } from '@/components/form'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { ApiError, apiRequest } from '@/lib/api'
import { cn } from '@/lib/utils'

type Category = { id?: string; name: string; weightPercent: number }
type SettingsItem = { itemId: string; kind: string; title: string; categoryId: string | null }
type Settings = { gradeScaleId: string | null; passPercent: number; categories: Category[]; items: SettingsItem[] }
type Scale = { id: string | null; name: string; isDefault: boolean; builtIn: boolean }

/** Weights are valid when there are no categories at all, or when they add up to 100. */
export const categoryTotal = (categories: Category[]) => Math.round(categories.reduce((sum, item) => sum + (Number(item.weightPercent) || 0), 0) * 100) / 100
export function categoriesError(categories: Category[]): string | null {
  if (categories.length === 0) return null
  if (categories.some((item) => !item.name.trim())) return 'Give every category a name.'
  if (new Set(categories.map((item) => item.name.trim().toLowerCase())).size !== categories.length) return 'Category names must be different.'
  if (categories.some((item) => !(Number(item.weightPercent) > 0))) return 'Every weight must be more than 0.'
  const total = categoryTotal(categories)
  return Math.abs(total - 100) > 0.01 ? `The weights add up to ${total}%. They must add up to 100%.` : null
}

export default function GradebookSetup({ courseId, onSaved }: { courseId: string; onSaved: () => void }) {
  const [settings, setSettings] = useState<Settings | null>(null)
  const [scales, setScales] = useState<Scale[]>([])
  const [categories, setCategories] = useState<Category[]>([])
  const [scaleId, setScaleId] = useState('')
  const [pass, setPass] = useState('50')
  const [message, setMessage] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      const [loaded, scaleList] = await Promise.all([
        apiRequest<Settings>(`/api/v1/tenant/gradebook/courses/${courseId}/settings`),
        apiRequest<Scale[]>('/api/v1/tenant/gradebook/scales'),
      ])
      setSettings(loaded); setScales(scaleList)
      setCategories(loaded.categories.map((item) => ({ ...item })))
      setScaleId(loaded.gradeScaleId ?? ''); setPass(String(loaded.passPercent))
    } catch (exception) { setError(readError(exception, 'Unable to load the grading setup.')) }
  }, [courseId])
  useEffect(() => { setMessage(null); setError(null); void load() }, [load])

  const validation = categoriesError(categories)
  const setCategory = (index: number, patch: Partial<Category>) => setCategories(categories.map((item, i) => (i === index ? { ...item, ...patch } : item)))

  async function save(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const passValue = Number(pass)
    const invalid = pass.trim() === '' || Number.isNaN(passValue) || passValue < 0 || passValue > 100 ? 'The pass mark must be a number from 0 to 100.' : categoriesError(categories)
    setProblem(invalid)
    if (invalid) return
    setBusy(true); setError(null); setMessage(null)
    try {
      const saved = await apiRequest<Settings>(`/api/v1/tenant/gradebook/courses/${courseId}/settings`, {
        method: 'PUT',
        body: JSON.stringify({
          gradeScaleId: scaleId || null, passPercent: Number(pass),
          categories: categories.map((item) => ({ id: item.id ?? null, name: item.name, weightPercent: Number(item.weightPercent) })),
        }),
      })
      setSettings(saved); setCategories(saved.categories.map((item) => ({ ...item }))); setMessage('Saved.'); onSaved()
    } catch (exception) { setError(readError(exception, 'Unable to save the grading setup.')) }
    finally { setBusy(false) }
  }

  async function assign(item: SettingsItem, categoryId: string) {
    setError(null)
    try {
      await apiRequest(`/api/v1/tenant/gradebook/courses/${courseId}/items/category`, { method: 'PUT', body: JSON.stringify({ itemKind: item.kind, itemId: item.itemId, categoryId: categoryId || null }) })
      setSettings((current) => current && { ...current, items: current.items.map((entry) => (entry.itemId === item.itemId && entry.kind === item.kind ? { ...entry, categoryId: categoryId || null } : entry)) })
      onSaved()
    } catch (exception) { setError(readError(exception, 'Unable to move the item.')) }
  }

  if (!settings) return <p className="text-sm text-muted-foreground">{error ?? 'Loading…'}</p>
  const total = categoryTotal(categories)
  const uncounted = categories.length > 0 ? settings.items.filter((item) => !item.categoryId) : []

  return (
    <div className="flex max-w-3xl flex-col gap-8">
      <ErrorBanner message={error} />
      <FormLayout onSubmit={save}>
        <ErrorBanner message={problem ?? validation} />
        <NoticeBanner message={message} />
        <FormSection title="Grading for this course" description="With no categories, the grade is total points earned ÷ total points possible. With categories, each one counts by its weight.">
          <div className="grid gap-3 sm:grid-cols-2">
            <Field id="setup-scale" label="Grade scale">
              <Select id="setup-scale" value={scaleId} onChange={(e) => setScaleId(e.target.value)}>
                <option value="">Organization default</option>
                {scales.filter((scale) => !scale.builtIn).map((scale) => <option key={scale.id} value={scale.id!}>{scale.name}</option>)}
              </Select>
            </Field>
            <Field id="setup-pass" label="Pass mark (%)" required>
              <Input id="setup-pass" type="number" step="0.5" value={pass} onChange={(e) => { setPass(e.target.value); setProblem(null) }} />
            </Field>
          </div>
        </FormSection>

        <FormSection title="Weighted categories" description="Optional. When you add categories, their weights must add up to 100%.">
          {categories.length === 0 ? <p className="text-sm text-muted-foreground">No categories: every graded item counts by its points.</p> : (
            <div aria-hidden="true" className="hidden gap-2 text-sm font-medium sm:flex">
              <span className="min-w-48 flex-1 after:ml-0.5 after:text-red-500 after:content-['*'/'']">Category</span>
              <span className="w-28 after:ml-0.5 after:text-red-500 after:content-['*'/'']">Weight (%)</span>
              <span className="w-[72px]" />
            </div>
          )}
          {categories.map((category, index) => (
            <div key={category.id ?? `new-${index}`} className="flex flex-wrap items-end gap-2">
              <div className="flex min-w-48 flex-1 flex-col gap-1.5">
                <Label htmlFor={`category-name-${index}`} className="sr-only">Category name {index + 1}</Label>
                <Input id={`category-name-${index}`} value={category.name} maxLength={60} placeholder="e.g. Homework" onChange={(e) => setCategory(index, { name: e.target.value })} />
              </div>
              <div className="flex w-28 flex-col gap-1.5">
                <Label htmlFor={`category-weight-${index}`} className="sr-only">Weight {index + 1} (%)</Label>
                <Input id={`category-weight-${index}`} type="number" min="0" max="100" step="0.01" value={category.weightPercent} onChange={(e) => setCategory(index, { weightPercent: Number(e.target.value) })} />
              </div>
              <Button type="button" variant="softDestructive" size="sm" aria-label={`Remove category ${index + 1}`} onClick={() => setCategories(categories.filter((_, i) => i !== index))}>Remove</Button>
            </div>
          ))}
          <div className="flex flex-wrap items-center justify-between gap-2">
            <Button type="button" variant="secondary" size="sm" disabled={categories.length >= 10} onClick={() => setCategories([...categories, { name: '', weightPercent: categories.length === 0 ? 100 : 0 }])}>Add category</Button>
            {categories.length > 0 ? <span role="status" className={cn('text-sm', Math.abs(total - 100) <= 0.01 ? 'text-emerald-400' : 'text-destructive')}>Total {total}% of 100%</span> : null}
          </div>
        </FormSection>

        <FormActions busy={busy} disabled={validation !== null} submitLabel="Save grading setup" />
      </FormLayout>

      <FormSection title="Which category each item belongs to" description={settings.categories.length === 0 ? 'Save at least one category first.' : 'Items outside every category are not counted in the weighted grade.'}>
        {settings.items.length === 0 ? <EmptyState>This course has no published assignments or assessments yet.</EmptyState> : (
          <RowList label="Graded items">
            {settings.items.map((item) => (
              <ListRow key={`${item.kind}-${item.itemId}`} columns="md:grid-cols-[minmax(0,1fr)_12rem]">
                <span className="min-w-0"><strong className="block truncate">{item.title}</strong><small className="text-muted-foreground">{item.kind === 'assignment' ? 'Assignment' : 'Assessment'}</small></span>
                <Select aria-label={`Category for ${item.title}`} value={item.categoryId ?? ''} disabled={settings.categories.length === 0} onChange={(e) => void assign(item, e.target.value)}>
                  <option value="">Not counted</option>
                  {settings.categories.map((category) => <option key={category.id} value={category.id}>{category.name}</option>)}
                </Select>
              </ListRow>
            ))}
          </RowList>
        )}
        {uncounted.length > 0 ? <p className="text-sm text-amber-400">{uncounted.length} item{uncounted.length === 1 ? ' is' : 's are'} not in a category and will not count.</p> : null}
      </FormSection>
    </div>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
