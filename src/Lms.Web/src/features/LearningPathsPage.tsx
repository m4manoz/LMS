import { useEffect, useState } from 'react'
import { Plus } from 'lucide-react'
import SidePanel from '@/components/SidePanel'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, PageHeader, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, apiRequest } from '@/lib/api'
import { useAuth } from '@/lib/auth'

type PathCourse = { id: string; code: string; title: string }
type LearningPath = { id: string; title: string; description?: string | null; courses: PathCourse[] }
type Course = { id: string; code: string; title: string }

export default function LearningPathsPage() {
  const { session } = useAuth()
  const canManage = session?.permissions.includes('course.manage') ?? false
  const [paths, setPaths] = useState<LearningPath[]>([])
  const [courses, setCourses] = useState<Course[]>([])
  const [search, setSearch] = useState('')
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [selected, setSelected] = useState<string[]>([])
  const [creating, setCreating] = useState(false)
  const [openId, setOpenId] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function refresh() {
    try {
      setPaths(await apiRequest<LearningPath[]>('/api/v1/tenant/catalog/paths'))
      if (canManage) setCourses(await apiRequest<Course[]>('/api/v1/tenant/courses'))
    } catch (exception) { setError(readError(exception, 'Unable to load learning paths.')) }
  }
  useEffect(() => { void refresh() }, [])

  // The order in which courses are ticked is the order of the path.
  function toggle(id: string) { setSelected((current) => current.includes(id) ? current.filter((item) => item !== id) : [...current, id]) }

  const openCreate = () => { setTitle(''); setDescription(''); setSelected([]); setProblem(null); setNotice(null); setOpenId(null); setCreating(true) }
  const closeCreate = () => setCreating(false)
  const closeDetails = () => setOpenId(null)

  async function create(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!title.trim()) { setProblem('Enter a title for the learning path.'); return }
    if (selected.length === 0) { setProblem('Tick at least one course for the path.'); return }
    setBusy(true); setError(null); setProblem(null)
    try {
      await apiRequest('/api/v1/tenant/catalog/paths', { method: 'POST', body: JSON.stringify({ title, description, courseIds: selected }) })
      setCreating(false); setNotice(`Learning path “${title.trim()}” created.`); await refresh()
    } catch (exception) { setProblem(readError(exception, 'Unable to create the learning path.')) }
    finally { setBusy(false) }
  }

  async function remove(path: LearningPath) {
    if (!window.confirm(`Delete the learning path "${path.title}"?`)) return
    setBusy(true); setError(null); setNotice(null)
    try { await apiRequest(`/api/v1/tenant/catalog/paths/${path.id}`, { method: 'DELETE' }); setOpenId(null); await refresh(); setNotice(`Learning path “${path.title}” deleted.`) }
    catch (exception) { setError(readError(exception, 'Unable to delete the learning path.')) }
    finally { setBusy(false) }
  }

  const needle = search.trim().toLowerCase()
  const shown = paths.filter((item) => !needle || item.title.toLowerCase().includes(needle))
  const opened = paths.find((item) => item.id === openId) ?? null

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Learning paths" description="Ordered sequences of courses that lead to a goal."
        actions={<><span className="text-sm text-muted-foreground">{paths.length} path{paths.length === 1 ? '' : 's'}</span>{canManage ? <Button onClick={openCreate}><Plus className="mr-1 h-4 w-4" aria-hidden />New path</Button> : null}</>} />
      <ErrorBanner message={opened ? null : error} />
      <NoticeBanner message={notice} />

      <Input className="max-w-sm" type="search" placeholder="Search learning paths" aria-label="Search learning paths" value={search} onChange={(event) => setSearch(event.target.value)} />

      {paths.length === 0 ? <EmptyState>No learning paths yet.</EmptyState> : shown.length === 0 ? <EmptyState>No learning paths match.</EmptyState> : (
        <RowList label="Learning paths">
          {shown.map((path) => (
            <ListRow key={path.id} selected={openId === path.id} columns="md:grid-cols-[minmax(0,1fr)_110px_auto]">
              <div className="min-w-0">
                <strong className="block truncate">{path.title}</strong>
                <small className="text-muted-foreground">{path.description || 'No description'}</small>
              </div>
              <div className="hidden md:block"><Badge variant="secondary">{path.courses.length} course{path.courses.length === 1 ? '' : 's'}</Badge></div>
              <div className="flex justify-end">
                <Button variant="soft" size="sm" aria-label={`View details for ${path.title}`} onClick={() => { setCreating(false); setOpenId(path.id) }}>View details</Button>
              </div>
            </ListRow>
          ))}
        </RowList>
      )}

      <SidePanel open={opened !== null} label="Learning path details" onClose={closeDetails}>
        {opened ? (
          <div className="flex max-w-3xl flex-col gap-5">
            <div>
              <h3 className="text-xl font-semibold">{opened.title}</h3>
              {opened.description ? <p className="text-sm text-muted-foreground">{opened.description}</p> : null}
            </div>
            <ErrorBanner message={error} />
            <ol className="flex flex-col gap-1 text-sm" aria-label="Courses in this path">
              {opened.courses.map((course, index) => (
                <li key={course.id} className="flex items-center gap-3 rounded-md border border-border px-3 py-2">
                  <span className="flex h-6 w-6 items-center justify-center rounded-full bg-muted text-xs">{index + 1}</span>
                  <span><strong>{course.title}</strong> <small className="text-muted-foreground">{course.code}</small></span>
                </li>
              ))}
            </ol>
            <div className="flex flex-wrap items-center gap-2 border-t border-border pt-5">
              {canManage ? <Button variant="softDestructive" disabled={busy} aria-label={`Delete learning path ${opened.title}`} onClick={() => void remove(opened)}>Delete</Button> : null}
              <Button type="button" variant="outline" onClick={closeDetails}>Close</Button>
            </div>
          </div>
        ) : null}
      </SidePanel>

      <SidePanel open={creating} label="New learning path" onClose={closeCreate}>
        <FormLayout onSubmit={create}>
          <ErrorBanner message={problem} />
          <FormSection title="About the path">
            <Field id="path-title" label="Title" required><Input id="path-title" value={title} onChange={(e) => setTitle(e.target.value)} maxLength={250} /></Field>
            <Field id="path-description" label="Description"><Textarea id="path-description" value={description} onChange={(e) => setDescription(e.target.value)} rows={2} /></Field>
          </FormSection>
          <FormSection title="Courses" description="Tick courses in the order learners should take them.">
            {courses.length === 0 ? <p className="text-sm text-muted-foreground">Create a course first.</p> : courses.map((course) => (
              <label key={course.id} className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={selected.includes(course.id)} onChange={() => toggle(course.id)} />
                {selected.includes(course.id) ? <strong className="w-5 text-primary">{selected.indexOf(course.id) + 1}</strong> : <span className="w-5" />}
                {course.title} <small className="text-muted-foreground">{course.code}</small>
              </label>
            ))}
          </FormSection>
          <FormActions busy={busy} submitLabel="Create path" onCancel={closeCreate} />
        </FormLayout>
      </SidePanel>
    </section>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
