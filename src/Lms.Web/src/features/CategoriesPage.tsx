import { useEffect, useState } from 'react'
import { Plus } from 'lucide-react'
import SidePanel from '@/components/SidePanel'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, PageHeader, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ApiError, apiRequest } from '@/lib/api'
import { useAuth } from '@/lib/auth'

type Category = { id: string; name: string; slug: string; courseCount: number }

export default function CategoriesPage() {
  const { session } = useAuth()
  const canManage = session?.permissions.includes('course.manage') ?? false
  const [categories, setCategories] = useState<Category[]>([])
  const [search, setSearch] = useState('')
  const [creating, setCreating] = useState(false)
  const [name, setName] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function refresh() {
    try { setCategories(await apiRequest<Category[]>('/api/v1/tenant/catalog/categories')) }
    catch (exception) { setError(readError(exception, 'Unable to load categories.')) }
  }
  useEffect(() => { void refresh() }, [])

  const open = () => { setName(''); setProblem(null); setCreating(true); setNotice(null) }
  const close = () => setCreating(false)

  async function create(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (name.trim().length < 2) { setProblem('Enter a name of at least 2 characters.'); return }
    setBusy(true); setError(null); setProblem(null)
    try {
      await apiRequest('/api/v1/tenant/catalog/categories', { method: 'POST', body: JSON.stringify({ name: name.trim() }) })
      setCreating(false); setNotice(`Category “${name.trim()}” created.`); await refresh()
    } catch (exception) { setProblem(readError(exception, 'Unable to create the category.')) }
    finally { setBusy(false) }
  }

  async function remove(category: Category) {
    if (!window.confirm(`Delete the category "${category.name}"?`)) return
    setBusy(true); setError(null); setNotice(null)
    try { await apiRequest(`/api/v1/tenant/catalog/categories/${category.id}`, { method: 'DELETE' }); await refresh(); setNotice(`Category “${category.name}” deleted.`) }
    catch (exception) { setError(readError(exception, 'Unable to delete the category.')) }
    finally { setBusy(false) }
  }

  const needle = search.trim().toLowerCase()
  const shown = categories.filter((item) => !needle || item.name.toLowerCase().includes(needle))

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Course categories" description="Group courses so learners can find them."
        actions={<><span className="text-sm text-muted-foreground">{categories.length} categor{categories.length === 1 ? 'y' : 'ies'}</span>{canManage ? <Button onClick={open}><Plus className="mr-1 h-4 w-4" aria-hidden />New category</Button> : null}</>} />
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />

      <Input className="max-w-sm" type="search" placeholder="Search categories" aria-label="Search categories" value={search} onChange={(event) => setSearch(event.target.value)} />

      {categories.length === 0 ? <EmptyState>No categories yet.</EmptyState> : shown.length === 0 ? <EmptyState>No categories match.</EmptyState> : (
        <RowList label="Categories">
          {shown.map((item) => (
            <ListRow key={item.id} columns="sm:grid-cols-[minmax(0,1fr)_auto_auto]">
              <div className="min-w-0"><strong className="block truncate">{item.name}</strong><small className="text-muted-foreground">{item.slug}</small></div>
              <div><Badge variant={item.courseCount > 0 ? 'default' : 'secondary'}>{item.courseCount} course{item.courseCount === 1 ? '' : 's'}</Badge></div>
              <div className="flex justify-end">
                {canManage ? <Button variant="softDestructive" size="sm" disabled={busy || item.courseCount > 0} title={item.courseCount > 0 ? 'Move its courses to another category first.' : undefined} aria-label={`Delete category ${item.name}`} onClick={() => void remove(item)}>Delete</Button> : null}
              </div>
            </ListRow>
          ))}
        </RowList>
      )}

      <SidePanel open={creating} label="New category" onClose={close}>
        <FormLayout onSubmit={create}>
          <ErrorBanner message={problem} />
          <FormSection title="About the category" description="Names must be unique within your organization.">
            <Field id="category-name" label="Name" required><Input id="category-name" value={name} onChange={(e) => setName(e.target.value)} maxLength={150} /></Field>
          </FormSection>
          <FormActions busy={busy} submitLabel="Create category" onCancel={close} />
        </FormLayout>
      </SidePanel>
    </section>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
