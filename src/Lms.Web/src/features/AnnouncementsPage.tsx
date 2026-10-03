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

type Announcement = { id: string; title: string; body: string; isPinned: boolean; courseId: string | null; courseTitle: string | null; authorName: string; createdAtUtc: string; expiresAtUtc: string | null }
type Course = { id: string; code: string; title: string }
const emptyForm = { title: '', body: '', courseId: '', isPinned: false, expiresOn: '' }

export default function AnnouncementsPage() {
  const { session } = useAuth()
  const canManage = session?.permissions.includes('announcement.manage') ?? false
  const [items, setItems] = useState<Announcement[]>([])
  const [courses, setCourses] = useState<Course[]>([])
  const [search, setSearch] = useState('')
  const [form, setForm] = useState(emptyForm)
  const [creating, setCreating] = useState(false)
  const [openId, setOpenId] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function refresh() {
    try { setItems(await apiRequest<Announcement[]>('/api/v1/tenant/community/announcements')) }
    catch (exception) { setError(readError(exception, 'Unable to load announcements.')) }
  }

  useEffect(() => {
    void refresh()
    if (canManage) apiRequest<Course[]>('/api/v1/tenant/courses').then(setCourses).catch(() => setCourses([]))
  }, [])

  const openCreate = () => { setForm(emptyForm); setProblem(null); setNotice(null); setOpenId(null); setCreating(true) }
  const closeCreate = () => setCreating(false)
  const closeDetails = () => setOpenId(null)

  async function publish(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!form.title.trim()) { setProblem('Enter a title for the announcement.'); return }
    if (!form.body.trim()) { setProblem('Enter the message of the announcement.'); return }
    setBusy(true); setError(null); setProblem(null)
    try {
      await apiRequest('/api/v1/tenant/community/announcements', {
        method: 'POST',
        body: JSON.stringify({
          title: form.title, body: form.body, courseId: form.courseId || null, isPinned: form.isPinned,
          // Expire at the end of the chosen day in the viewer's time zone.
          expiresAtUtc: form.expiresOn ? new Date(`${form.expiresOn}T23:59:59`).toISOString() : null,
        }),
      })
      setCreating(false); setNotice(`Announcement “${form.title.trim()}” published.`); await refresh()
    } catch (exception) { setProblem(readError(exception, 'Unable to publish the announcement.')) }
    finally { setBusy(false) }
  }

  async function remove(item: Announcement) {
    if (!window.confirm(`Delete the announcement "${item.title}"?`)) return
    setBusy(true); setError(null); setNotice(null)
    try { await apiRequest(`/api/v1/tenant/community/announcements/${item.id}`, { method: 'DELETE' }); setOpenId(null); await refresh(); setNotice(`Announcement “${item.title}” deleted.`) }
    catch (exception) { setError(readError(exception, 'Unable to delete the announcement.')) }
    finally { setBusy(false) }
  }

  const needle = search.trim().toLowerCase()
  const shown = items.filter((item) => !needle || item.title.toLowerCase().includes(needle))
  const opened = items.find((item) => item.id === openId) ?? null

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Announcements" description="News and notices from your instructors and administrators."
        actions={<><span className="text-sm text-muted-foreground">{items.length} announcement{items.length === 1 ? '' : 's'}</span>{canManage ? <Button onClick={openCreate}><Plus className="mr-1 h-4 w-4" aria-hidden />New announcement</Button> : null}</>} />
      <ErrorBanner message={opened ? null : error} />
      <NoticeBanner message={notice} />

      <Input className="max-w-sm" type="search" placeholder="Search announcements" aria-label="Search announcements" value={search} onChange={(event) => setSearch(event.target.value)} />

      {items.length === 0 ? <EmptyState>No announcements right now.</EmptyState> : shown.length === 0 ? <EmptyState>No announcements match.</EmptyState> : (
        <RowList label="Announcements">
          {shown.map((item) => (
            <ListRow key={item.id} selected={openId === item.id} columns="md:grid-cols-[minmax(0,2fr)_minmax(0,1fr)_auto_auto]">
              <div className="min-w-0">
                <strong className="block truncate">{item.title}</strong>
                <small className="text-muted-foreground">{item.authorName} · {new Date(item.createdAtUtc).toLocaleDateString()}</small>
              </div>
              <div className="hidden text-muted-foreground md:block">{item.expiresAtUtc ? `Until ${new Date(item.expiresAtUtc).toLocaleDateString()}` : ''}</div>
              <div className="flex flex-wrap gap-2">
                {item.isPinned ? <Badge>Pinned</Badge> : null}
                <Badge variant="outline">{item.courseTitle ?? 'Everyone'}</Badge>
              </div>
              <div className="flex justify-end">
                <Button variant="soft" size="sm" aria-label={`View details for ${item.title}`} onClick={() => { setCreating(false); setOpenId(item.id) }}>View details</Button>
              </div>
            </ListRow>
          ))}
        </RowList>
      )}

      <SidePanel open={opened !== null} label="Announcement details" onClose={closeDetails}>
        {opened ? (
          <div className="flex max-w-3xl flex-col gap-5">
            <div>
              <div className="flex flex-wrap gap-2">
                {opened.isPinned ? <Badge>Pinned</Badge> : null}
                <Badge variant="outline">{opened.courseTitle ?? 'Everyone'}</Badge>
              </div>
              <h3 className="mt-2 text-xl font-semibold">{opened.title}</h3>
              <p className="text-sm text-muted-foreground">{opened.authorName} · {new Date(opened.createdAtUtc).toLocaleString()}{opened.expiresAtUtc ? ` · until ${new Date(opened.expiresAtUtc).toLocaleDateString()}` : ''}</p>
            </div>
            <ErrorBanner message={error} />
            <p className="whitespace-pre-wrap text-sm">{opened.body}</p>
            <div className="flex flex-wrap items-center gap-2 border-t border-border pt-5">
              {canManage ? <Button variant="softDestructive" disabled={busy} aria-label={`Delete announcement ${opened.title}`} onClick={() => void remove(opened)}>Delete</Button> : null}
              <Button type="button" variant="outline" onClick={closeDetails}>Close</Button>
            </div>
          </div>
        ) : null}
      </SidePanel>

      <SidePanel open={creating} label="New announcement" onClose={closeCreate}>
        <FormLayout onSubmit={publish}>
          <ErrorBanner message={problem} />
          <FormSection title="Message" description="Everyone in your organization can read it, or choose a single course.">
            <Field id="announcement-title" label="Title" required><Input id="announcement-title" value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} maxLength={200} /></Field>
            <Field id="announcement-body" label="Message" required><Textarea id="announcement-body" value={form.body} onChange={(e) => setForm({ ...form, body: e.target.value })} rows={5} maxLength={10000} /></Field>
          </FormSection>
          <FormSection title="Audience and timing">
            <div className="grid gap-3 sm:grid-cols-2">
              <Field id="announcement-course" label="Audience">
                <Select id="announcement-course" value={form.courseId} onChange={(e) => setForm({ ...form, courseId: e.target.value })}>
                  <option value="">Everyone</option>
                  {courses.map((course) => <option key={course.id} value={course.id}>{course.code} · {course.title}</option>)}
                </Select>
              </Field>
              <Field id="announcement-expires" label="Hide after (optional)"><Input id="announcement-expires" type="date" value={form.expiresOn} onChange={(e) => setForm({ ...form, expiresOn: e.target.value })} /></Field>
            </div>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={form.isPinned} onChange={(e) => setForm({ ...form, isPinned: e.target.checked })} />
              Pin to the top
            </label>
          </FormSection>
          <FormActions busy={busy} busyLabel="Publishing…" submitLabel="Publish" onCancel={closeCreate} />
        </FormLayout>
      </SidePanel>
    </section>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
