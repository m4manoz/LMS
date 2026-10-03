import { useEffect, useState } from 'react'
import { Plus } from 'lucide-react'
import SidePanel from '@/components/SidePanel'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, PageHeader, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, apiRequest } from '@/lib/api'
import { useAuth } from '@/lib/auth'

type Thread = { id: string; courseId: string | null; courseTitle: string | null; title: string; authorName: string; isPinned: boolean; isLocked: boolean; replyCount: number; createdAtUtc: string; lastActivityAtUtc: string }
type Reply = { id: string; authorUserId: string; authorName: string; body: string; createdAtUtc: string }
type ThreadDetail = { id: string; courseId: string | null; title: string; body: string; authorUserId: string; authorName: string; isPinned: boolean; isLocked: boolean; createdAtUtc: string; replies: Reply[] }
type Course = { id: string; code: string; title: string }

export default function ForumsPage() {
  const { session } = useAuth()
  const canPost = session?.permissions.includes('collaboration.manage') ?? false
  const canModerate = session?.permissions.includes('forum.moderate') ?? false
  const [threads, setThreads] = useState<Thread[]>([])
  const [courses, setCourses] = useState<Course[]>([])
  const [courseFilter, setCourseFilter] = useState('')
  const [search, setSearch] = useState('')
  const [detail, setDetail] = useState<ThreadDetail | null>(null)
  const [creating, setCreating] = useState(false)
  const [form, setForm] = useState({ courseId: '', title: '', body: '' })
  const [reply, setReply] = useState('')
  const [problem, setProblem] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function loadThreads(filter = courseFilter) {
    try { setThreads(await apiRequest<Thread[]>(`/api/v1/tenant/community/threads${filter ? `?courseId=${filter}` : ''}`)) }
    catch (exception) { setError(readError(exception, 'Unable to load discussions.')) }
  }

  useEffect(() => {
    void loadThreads()
    apiRequest<Course[]>('/api/v1/tenant/courses').then(setCourses).catch(() => setCourses([]))
  }, [])

  async function open(id: string) {
    setError(null)
    try { setDetail(await apiRequest<ThreadDetail>(`/api/v1/tenant/community/threads/${id}`)) }
    catch (exception) { setError(readError(exception, 'Unable to open the discussion.')) }
  }

  async function run(action: () => Promise<unknown>, failure: string, after?: () => Promise<void>) {
    setBusy(true); setError(null)
    try { await action(); if (after) await after() }
    catch (exception) { setError(readError(exception, failure)) }
    finally { setBusy(false) }
  }

  const openCreate = () => { setForm({ courseId: '', title: '', body: '' }); setProblem(null); setError(null); setDetail(null); setCreating(true) }
  const closeCreate = () => setCreating(false)
  const closeDetail = () => { setDetail(null); setReply('') }

  async function createThread(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!form.title.trim()) { setProblem('Enter a title for the discussion.'); return }
    if (!form.body.trim()) { setProblem('Enter a message for the discussion.'); return }
    setBusy(true); setError(null); setProblem(null)
    try {
      const created = await apiRequest<{ id: string }>('/api/v1/tenant/community/threads', { method: 'POST', body: JSON.stringify({ courseId: form.courseId || null, title: form.title, body: form.body }) })
      await loadThreads()
      setCreating(false)
      await open(created.id)
    } catch (exception) { setProblem(readError(exception, 'Unable to start the discussion.')) }
    finally { setBusy(false) }
  }

  const sendReply = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!detail) return Promise.resolve()
    if (!reply.trim()) { setError('Write a reply before posting.'); return Promise.resolve() }
    return run(() => apiRequest(`/api/v1/tenant/community/threads/${detail.id}/replies`, { method: 'POST', body: JSON.stringify({ body: reply }) }),
      'Unable to post the reply.', async () => { setReply(''); await open(detail.id); await loadThreads() })
  }

  const setFlag = (kind: 'pin' | 'lock', value: boolean) => detail && run(
    () => apiRequest(`/api/v1/tenant/community/threads/${detail.id}/${kind}`, { method: 'POST', body: JSON.stringify({ value }) }),
    `Unable to ${kind} the discussion.`, async () => { await open(detail.id); await loadThreads() })

  const removeThread = () => {
    if (!detail || !window.confirm(`Delete the discussion "${detail.title}"?`)) return
    return run(
      () => apiRequest(`/api/v1/tenant/community/threads/${detail.id}`, { method: 'DELETE' }),
      'Unable to delete the discussion.', async () => { setDetail(null); await loadThreads() })
  }

  const removeReply = (id: string) => {
    if (!detail || !window.confirm('Delete this reply?')) return
    return run(
      () => apiRequest(`/api/v1/tenant/community/replies/${id}`, { method: 'DELETE' }),
      'Unable to delete the reply.', async () => { await open(detail.id) })
  }

  const canDeleteThread = detail && (canModerate || detail.authorUserId === session?.user.id)
  const needle = search.trim().toLowerCase()
  const shown = threads.filter((item) => !needle || item.title.toLowerCase().includes(needle))

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Forums" description="Ask questions and discuss course topics with your class."
        actions={<><span className="text-sm text-muted-foreground">{threads.length} discussion{threads.length === 1 ? '' : 's'}</span>{canPost ? <Button onClick={openCreate}><Plus className="mr-1 h-4 w-4" aria-hidden />Start a discussion</Button> : null}</>} />
      <ErrorBanner message={detail || creating ? null : error} />

      <div className="flex flex-wrap items-center gap-3">
        <Input className="max-w-sm" type="search" placeholder="Search discussions" aria-label="Search discussions" value={search} onChange={(event) => setSearch(event.target.value)} />
        <div className="w-64">
          <Select id="forum-course-filter" aria-label="Course" value={courseFilter} onChange={(e) => { setCourseFilter(e.target.value); void loadThreads(e.target.value) }}>
            <option value="">All discussions</option>
            {courses.map((course) => <option key={course.id} value={course.id}>{course.code} · {course.title}</option>)}
          </Select>
        </div>
      </div>

      {threads.length === 0 ? <EmptyState>No discussions yet.</EmptyState> : shown.length === 0 ? <EmptyState>No discussions match.</EmptyState> : (
        <RowList label="Discussions">
          {shown.map((item) => (
            <ListRow key={item.id} selected={detail?.id === item.id} columns="md:grid-cols-[minmax(0,2fr)_auto_140px_auto]">
              <div className="min-w-0">
                <strong className="block truncate">{item.title}</strong>
                <small className="text-muted-foreground">{item.authorName}{item.courseTitle ? ` · ${item.courseTitle}` : ' · General'}</small>
              </div>
              <div className="flex flex-wrap gap-2">
                {item.isPinned ? <Badge>Pinned</Badge> : null}
                {item.isLocked ? <Badge variant="outline">Locked</Badge> : null}
                <Badge variant="secondary">{item.replyCount} repl{item.replyCount === 1 ? 'y' : 'ies'}</Badge>
              </div>
              <div className="hidden text-muted-foreground md:block">Active {new Date(item.lastActivityAtUtc).toLocaleDateString()}</div>
              <div className="flex justify-end">
                <Button variant="soft" size="sm" aria-label={`View details for ${item.title}`} onClick={() => { setCreating(false); void open(item.id) }}>View details</Button>
              </div>
            </ListRow>
          ))}
        </RowList>
      )}

      <SidePanel open={detail !== null} label="Discussion details" onClose={closeDetail}>
        {detail ? (
          <div className="flex max-w-3xl flex-col gap-5">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div>
                <div className="flex gap-2">
                  {detail.isPinned ? <Badge>Pinned</Badge> : null}
                  {detail.isLocked ? <Badge variant="outline">Locked</Badge> : null}
                </div>
                <h3 className="mt-2 text-xl font-semibold">{detail.title}</h3>
                <p className="text-sm text-muted-foreground">{detail.authorName} · {new Date(detail.createdAtUtc).toLocaleString()}</p>
              </div>
              <div className="flex flex-wrap gap-2">
                {canModerate ? <Button variant="soft" size="sm" disabled={busy} onClick={() => void setFlag('pin', !detail.isPinned)}>{detail.isPinned ? 'Unpin' : 'Pin'}</Button> : null}
                {canModerate ? <Button variant="soft" size="sm" disabled={busy} onClick={() => void setFlag('lock', !detail.isLocked)}>{detail.isLocked ? 'Unlock' : 'Lock'}</Button> : null}
                {canDeleteThread ? <Button variant="softDestructive" size="sm" disabled={busy} onClick={() => void removeThread()}>Delete</Button> : null}
              </div>
            </div>
            <ErrorBanner message={error} />
            <p className="whitespace-pre-wrap text-sm">{detail.body}</p>

            <div className="flex flex-col gap-3 border-t border-border pt-5">
              <h4 className="text-sm font-semibold">Replies ({detail.replies.length})</h4>
              {detail.replies.length === 0 ? <p className="text-sm text-muted-foreground">No replies yet.</p> : detail.replies.map((item) => (
                <div key={item.id} className="rounded-md bg-muted px-3 py-2 text-sm">
                  <div className="flex items-start justify-between gap-2">
                    <strong>{item.authorName}</strong>
                    {canModerate || item.authorUserId === session?.user.id ? <Button variant="softDestructive" size="sm" disabled={busy} aria-label={`Delete reply by ${item.authorName}`} onClick={() => void removeReply(item.id)}>Delete</Button> : null}
                  </div>
                  <p className="whitespace-pre-wrap">{item.body}</p>
                  <small className="text-muted-foreground">{new Date(item.createdAtUtc).toLocaleString()}</small>
                </div>
              ))}
              {detail.isLocked ? <p className="text-sm text-muted-foreground">This discussion is locked. New replies are disabled.</p> : null}
            </div>

            {canPost && !detail.isLocked ? (
              <FormLayout onSubmit={sendReply}>
                <FormSection title="Reply">
                  <Field id="forum-reply" label="Your reply" required><Textarea id="forum-reply" value={reply} onChange={(e) => setReply(e.target.value)} rows={3} maxLength={10000} /></Field>
                </FormSection>
                <FormActions busy={busy} submitLabel="Post reply" busyLabel="Posting…" onCancel={closeDetail} />
              </FormLayout>
            ) : null}
          </div>
        ) : null}
      </SidePanel>

      <SidePanel open={creating} label="Start a discussion" onClose={closeCreate}>
        <FormLayout onSubmit={createThread}>
          <ErrorBanner message={problem} />
          <FormSection title="About the discussion" description="Choose a course, or leave it as General for everyone.">
            <Field id="thread-course" label="Course">
              <Select id="thread-course" value={form.courseId} onChange={(e) => setForm({ ...form, courseId: e.target.value })}>
                <option value="">General</option>
                {courses.map((course) => <option key={course.id} value={course.id}>{course.code} · {course.title}</option>)}
              </Select>
            </Field>
            <Field id="thread-title" label="Title" required><Input id="thread-title" value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} maxLength={200} /></Field>
            <Field id="thread-body" label="Message" required><Textarea id="thread-body" value={form.body} onChange={(e) => setForm({ ...form, body: e.target.value })} rows={5} maxLength={10000} /></Field>
          </FormSection>
          <FormActions busy={busy} busyLabel="Posting…" submitLabel="Post discussion" onCancel={closeCreate} />
        </FormLayout>
      </SidePanel>
    </section>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
