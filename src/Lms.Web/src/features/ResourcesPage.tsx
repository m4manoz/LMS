import { useEffect, useState } from 'react'
import { Plus } from 'lucide-react'
import SidePanel from '@/components/SidePanel'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, PageHeader, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, apiRequest } from '@/lib/api'
import { useAuth } from '@/lib/auth'

type Resource = { id: string; type: string; title: string; description?: string | null; url?: string | null; uploadedAtUtc: string }
const types = ['Document', 'Video', 'Audio', 'Link', 'Other']
const emptyForm = { type: 'Link', title: '', description: '', url: '' }

export default function ResourcesPage() {
  const { session } = useAuth()
  const canManage = session?.permissions.includes('course.manage') ?? false
  const [resources, setResources] = useState<Resource[]>([])
  const [filter, setFilter] = useState('')
  const [search, setSearch] = useState('')
  const [form, setForm] = useState(emptyForm)
  const [creating, setCreating] = useState(false)
  const [problem, setProblem] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function refresh(nextFilter = filter) {
    try { setResources(await apiRequest<Resource[]>(`/api/v1/tenant/catalog/resources${nextFilter ? `?type=${nextFilter}` : ''}`)) }
    catch (exception) { setError(readError(exception, 'Unable to load resources.')) }
  }
  useEffect(() => { void refresh() }, [])

  const open = () => { setForm(emptyForm); setProblem(null); setNotice(null); setCreating(true) }
  const close = () => setCreating(false)

  async function create(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!form.title.trim()) { setProblem('Enter a title for the resource.'); return }
    if (!form.url.trim()) { setProblem('Enter the URL of the resource.'); return }
    setBusy(true); setError(null); setProblem(null)
    try {
      await apiRequest('/api/v1/tenant/catalog/resources', { method: 'POST', body: JSON.stringify(form) })
      setCreating(false); setNotice(`Resource “${form.title.trim()}” added.`); await refresh()
    } catch (exception) { setProblem(readError(exception, 'Unable to add the resource.')) }
    finally { setBusy(false) }
  }

  async function remove(item: Resource) {
    if (!window.confirm(`Delete the resource "${item.title}"?`)) return
    setBusy(true); setError(null); setNotice(null)
    try { await apiRequest(`/api/v1/tenant/catalog/resources/${item.id}`, { method: 'DELETE' }); await refresh(); setNotice(`Resource “${item.title}” deleted.`) }
    catch (exception) { setError(readError(exception, 'Unable to delete the resource.')) }
    finally { setBusy(false) }
  }

  const needle = search.trim().toLowerCase()
  const shown = resources.filter((item) => !needle || item.title.toLowerCase().includes(needle))

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Learning resources" description="Shared reading, videos and links available to learners."
        actions={<><span className="text-sm text-muted-foreground">{resources.length} resource{resources.length === 1 ? '' : 's'}</span>{canManage ? <Button onClick={open}><Plus className="mr-1 h-4 w-4" aria-hidden />Add resource</Button> : null}</>} />
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />

      <div className="flex flex-wrap items-end gap-3">
        <Input className="max-w-sm" type="search" placeholder="Search resources" aria-label="Search resources" value={search} onChange={(event) => setSearch(event.target.value)} />
        <div className="flex w-48 flex-col gap-1.5">
          <Select id="resource-filter" aria-label="Type" value={filter} onChange={(e) => { setFilter(e.target.value); void refresh(e.target.value) }}>
            <option value="">All types</option>
            {types.map((item) => <option key={item}>{item}</option>)}
          </Select>
        </div>
      </div>

      {resources.length === 0 ? <EmptyState>No resources yet.</EmptyState> : shown.length === 0 ? <EmptyState>No resources match.</EmptyState> : (
        <RowList label="Resources">
          {shown.map((item) => (
            <ListRow key={item.id} columns="md:grid-cols-[minmax(0,1fr)_110px_auto]">
              <div className="min-w-0">
                <strong className="block truncate">{item.title}</strong>
                {item.description ? <small className="block text-muted-foreground">{item.description}</small> : null}
                {item.url ? <a className="block truncate text-primary underline" href={item.url} target="_blank" rel="noreferrer noopener">{item.url}</a> : null}
              </div>
              <div><Badge variant="outline">{item.type}</Badge></div>
              <div className="flex justify-end">
                {canManage ? <Button variant="softDestructive" size="sm" disabled={busy} aria-label={`Delete resource ${item.title}`} onClick={() => void remove(item)}>Delete</Button> : null}
              </div>
            </ListRow>
          ))}
        </RowList>
      )}

      <SidePanel open={creating} label="Add resource" onClose={close}>
        <FormLayout onSubmit={create}>
          <ErrorBanner message={problem} />
          <FormSection title="About the resource" description="Link to material hosted elsewhere. File uploads are attached to courses.">
            <div className="grid gap-3 sm:grid-cols-[160px_minmax(0,1fr)]">
              <Field id="resource-type" label="Type">
                <Select id="resource-type" value={form.type} onChange={(e) => setForm({ ...form, type: e.target.value })}>
                  {types.map((item) => <option key={item}>{item}</option>)}
                </Select>
              </Field>
              <Field id="resource-title" label="Title" required><Input id="resource-title" value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} maxLength={300} /></Field>
            </div>
            <Field id="resource-url" label="URL" required><Input id="resource-url" type="url" value={form.url} onChange={(e) => setForm({ ...form, url: e.target.value })} placeholder="https://" /></Field>
            <Field id="resource-description" label="Description"><Textarea id="resource-description" value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} rows={2} /></Field>
          </FormSection>
          <FormActions busy={busy} submitLabel="Add resource" onCancel={close} />
        </FormLayout>
      </SidePanel>
    </section>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
