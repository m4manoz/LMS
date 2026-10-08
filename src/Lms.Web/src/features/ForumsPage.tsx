import { useEffect, useState } from 'react'
import { Paperclip, Plus } from 'lucide-react'
import SidePanel from '@/components/SidePanel'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, PageHeader, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, apiRequest, downloadFile } from '@/lib/api'
import { useAuth } from '@/lib/auth'

type Thread = { id: string; courseId: string | null; courseTitle: string | null; title: string; authorName: string; isPinned: boolean; isLocked: boolean; replyCount: number; createdAtUtc: string; lastActivityAtUtc: string }
type Attachment = { id: string; fileName: string; sizeBytes: number; contentType: string }
type Reply = { id: string; authorUserId: string; authorName: string; body: string; createdAtUtc: string; editedAtUtc?: string | null; attachments?: Attachment[] | null }
type ThreadDetail = { id: string; courseId: string | null; title: string; body: string; authorUserId: string; authorName: string; isPinned: boolean; isLocked: boolean; createdAtUtc: string; replies: Reply[]; editedAtUtc?: string | null; attachments?: Attachment[] | null }
type Edit = { id: string; editedByName: string; editedAtUtc: string; previousTitle: string | null; previousBody: string }
type Editing = { kind: 'thread' } | { kind: 'reply'; id: string }

const MAX_ATTACHMENTS = 5
const MAX_FILE_BYTES = 25 * 1024 * 1024
const formatBytes = (bytes: number) => (bytes < 1024 ? `${bytes} B` : bytes < 1024 * 1024 ? `${Math.round(bytes / 1024)} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`)

/** The files chosen for a new post, checked before anything is sent. Returns a message for the first problem, or null. */
export function filesProblem(files: File[], alreadyThere = 0): string | null {
  if (files.length + alreadyThere > MAX_ATTACHMENTS) return `A post can have at most ${MAX_ATTACHMENTS} attachments.`
  const tooBig = files.find((file) => file.size > MAX_FILE_BYTES)
  return tooBig ? `${tooBig.name} is larger than 25 MB.` : null
}
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
  const [newFiles, setNewFiles] = useState<File[]>([])
  const [replyFiles, setReplyFiles] = useState<File[]>([])
  const [editing, setEditing] = useState<Editing | null>(null)
  const [editForm, setEditForm] = useState({ title: '', body: '' })
  const [history, setHistory] = useState<{ of: string; items: Edit[] } | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
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
    setError(null); setEditing(null); setHistory(null)
    try { setDetail(await apiRequest<ThreadDetail>(`/api/v1/tenant/community/threads/${id}`)) }
    catch (exception) { setError(readError(exception, 'Unable to open the discussion.')) }
  }

  async function run(action: () => Promise<unknown>, failure: string, after?: () => Promise<void>) {
    setBusy(true); setError(null)
    try { await action(); if (after) await after() }
    catch (exception) { setError(readError(exception, failure)) }
    finally { setBusy(false) }
  }

  const openCreate = () => { setForm({ courseId: '', title: '', body: '' }); setNewFiles([]); setProblem(null); setError(null); setDetail(null); setCreating(true) }
  const closeCreate = () => setCreating(false)
  const closeDetail = () => { setDetail(null); setReply(''); setReplyFiles([]); setEditing(null); setHistory(null); setNotice(null) }

  async function createThread(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!form.title.trim()) { setProblem('Enter a title for the discussion.'); return }
    if (!form.body.trim()) { setProblem('Enter a message for the discussion.'); return }
    const tooMany = filesProblem(newFiles)
    if (tooMany) { setProblem(tooMany); return }
    setBusy(true); setError(null); setProblem(null)
    try {
      const created = await apiRequest<{ id: string }>('/api/v1/tenant/community/threads', { method: 'POST', body: JSON.stringify({ courseId: form.courseId || null, title: form.title, body: form.body }) })
      const failed = await uploadFiles(created.id, null, newFiles)
      setNewFiles([])
      if (failed.length > 0) setNotice(`The discussion was posted, but ${failed.join(', ')} could not be attached. Open it to try again.`)
      await loadThreads()
      setCreating(false)
      await open(created.id)
    } catch (exception) { setProblem(readError(exception, 'Unable to start the discussion.')) }
    finally { setBusy(false) }
  }

  /** Attaches each file to a post one by one, so one failure does not lose the others. Returns the names that failed. */
  async function uploadFiles(threadId: string, replyId: string | null, files: File[]): Promise<string[]> {
    const failed: string[] = []
    for (const file of files) {
      try {
        const form = new FormData()
        form.append('file', file)
        if (replyId) form.append('replyId', replyId)
        await apiRequest(`/api/v1/tenant/community/threads/${threadId}/attachments`, { method: 'POST', body: form })
      } catch { failed.push(file.name) }
    }
    return failed
  }

  const sendReply = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!detail) return Promise.resolve()
    if (!reply.trim()) { setError('Write a reply before posting.'); return Promise.resolve() }
    const problem = filesProblem(replyFiles)
    if (problem) { setError(problem); return Promise.resolve() }
    const pending = replyFiles
    return run(async () => {
      await apiRequest(`/api/v1/tenant/community/threads/${detail.id}/replies`, { method: 'POST', body: JSON.stringify({ body: reply }) })
      if (pending.length > 0) {
        const refreshed = await apiRequest<ThreadDetail>(`/api/v1/tenant/community/threads/${detail.id}`)
        const mine = [...refreshed.replies].reverse().find((item) => item.authorUserId === session?.user.id)
        const failed = mine ? await uploadFiles(detail.id, mine.id, pending) : pending.map((file) => file.name)
        if (failed.length > 0) setNotice(`Your reply was posted, but ${failed.join(', ')} could not be attached.`)
      }
    }, 'Unable to post the reply.', async () => { setReply(''); setReplyFiles([]); await open(detail.id); await loadThreads() })
  }

  const startEditThread = () => { if (!detail) return; setEditForm({ title: detail.title, body: detail.body }); setEditing({ kind: 'thread' }); setHistory(null); setError(null) }
  const startEditReply = (item: Reply) => { setEditForm({ title: '', body: item.body }); setEditing({ kind: 'reply', id: item.id }); setHistory(null); setError(null) }

  const saveEdit = (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!detail || !editing) return Promise.resolve()
    if (editing.kind === 'thread' && editForm.title.trim().length < 3) { setError('Enter a title of at least 3 characters.'); return Promise.resolve() }
    if (!editForm.body.trim()) { setError('The message cannot be empty.'); return Promise.resolve() }
    const [url, body] = editing.kind === 'thread'
      ? [`/api/v1/tenant/community/threads/${detail.id}`, { title: editForm.title.trim(), body: editForm.body.trim() }]
      : [`/api/v1/tenant/community/replies/${editing.id}`, { body: editForm.body.trim() }]
    return run(() => apiRequest(url, { method: 'PUT', body: JSON.stringify(body) }), 'Unable to save the change.', async () => { setEditing(null); await open(detail.id); await loadThreads() })
  }

  const showHistory = async (of: string, url: string) => {
    if (history?.of === of) { setHistory(null); return }
    setError(null)
    try { setHistory({ of, items: await apiRequest<Edit[]>(url) }) }
    catch (exception) { setError(readError(exception, 'Unable to load the history.')) }
  }

  const addFiles = (target: 'new' | 'reply', list: FileList | null) => {
    const picked = Array.from(list ?? [])
    if (target === 'new') setNewFiles((current) => [...current, ...picked]); else setReplyFiles((current) => [...current, ...picked])
  }

  const attachToExisting = (replyId: string | null, list: FileList | null) => {
    if (!detail) return
    const picked = Array.from(list ?? [])
    const have = replyId ? (detail.replies.find((item) => item.id === replyId)?.attachments?.length ?? 0) : (detail.attachments?.length ?? 0)
    const problem = filesProblem(picked, have)
    if (problem) { setError(problem); return }
    void run(async () => {
      const failed = await uploadFiles(detail.id, replyId, picked)
      if (failed.length > 0) throw new ApiError(`${failed.join(', ')} could not be attached.`, 400)
    }, 'Unable to attach the file.', async () => { await open(detail.id) })
  }

  const removeAttachment = (attachment: Attachment) => {
    if (!detail || !window.confirm(`Remove ${attachment.fileName}?`)) return
    return run(() => apiRequest(`/api/v1/tenant/community/attachments/${attachment.id}`, { method: 'DELETE' }), 'Unable to remove the file.', async () => { await open(detail.id) })
  }

  const download = (attachment: Attachment) => run(() => downloadFile(`/api/v1/tenant/community/attachments/${attachment.id}`, attachment.fileName), 'Unable to download the file.')

  const fileList = (files: Attachment[] | null | undefined, mayRemove: boolean, label: string) => (files && files.length > 0 ? (
    <ul className="mt-2 flex flex-col gap-1 text-sm" aria-label={label}>
      {files.map((file) => (
        <li key={file.id} className="flex flex-wrap items-center gap-2">
          <Paperclip className="h-3.5 w-3.5 shrink-0" aria-hidden />
          <button type="button" className="underline" aria-label={`Download ${file.fileName}`} onClick={() => void download(file)}>{file.fileName}</button>
          <span className="text-xs text-muted-foreground">{formatBytes(file.sizeBytes)}</span>
          {mayRemove ? <button type="button" className="text-xs text-muted-foreground hover:underline" aria-label={`Remove ${file.fileName}`} onClick={() => void removeAttachment(file)}>Remove</button> : null}
        </li>
      ))}
    </ul>
  ) : null)

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
  const mayEdit = (authorId: string) => canPost && (canModerate || authorId === session?.user.id) && (!detail?.isLocked || canModerate)
  const needle = search.trim().toLowerCase()
  const shown = threads.filter((item) => !needle || item.title.toLowerCase().includes(needle))

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Forums" description="Ask questions and discuss course topics with your class."
        actions={<><span className="text-sm text-muted-foreground">{threads.length} discussion{threads.length === 1 ? '' : 's'}</span>{canPost ? <Button onClick={openCreate}><Plus className="mr-1 h-4 w-4" aria-hidden />Start a discussion</Button> : null}</>} />
      <ErrorBanner message={detail || creating ? null : error} />
      <NoticeBanner message={detail ? null : notice} />

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
                {mayEdit(detail.authorUserId) ? <Button variant="soft" size="sm" disabled={busy} onClick={startEditThread}>Edit</Button> : null}
                {canDeleteThread ? <Button variant="softDestructive" size="sm" disabled={busy} onClick={() => void removeThread()}>Delete</Button> : null}
              </div>
            </div>
            <ErrorBanner message={error} />
            <NoticeBanner message={notice} />
            {editing?.kind === 'thread' ? (
              <FormLayout onSubmit={saveEdit} noValidate>
                <FormSection title="Edit your discussion" description="The earlier wording is kept in the history, which only you and moderators can read." divider={false}>
                  <Field id="edit-thread-title" label="Discussion title" required><Input id="edit-thread-title" value={editForm.title} maxLength={200} onChange={(e) => setEditForm({ ...editForm, title: e.target.value })} /></Field>
                  <Field id="edit-thread-body" label="Discussion message" required><Textarea id="edit-thread-body" rows={5} maxLength={10000} value={editForm.body} onChange={(e) => setEditForm({ ...editForm, body: e.target.value })} /></Field>
                </FormSection>
                <FormActions busy={busy} submitLabel="Save changes" onCancel={() => setEditing(null)} />
              </FormLayout>
            ) : (
              <div>
                <p className="whitespace-pre-wrap text-sm">{detail.body}</p>
                {detail.editedAtUtc ? <small className="text-muted-foreground">Edited {new Date(detail.editedAtUtc).toLocaleString()}</small> : null}
                {detail.editedAtUtc && mayEdit(detail.authorUserId) ? (
                  <button type="button" className="ml-2 text-xs underline" onClick={() => void showHistory('thread', `/api/v1/tenant/community/threads/${detail.id}/history`)}>{history?.of === 'thread' ? 'Hide history' : 'Edit history'}</button>
                ) : null}
              </div>
            )}
            {history?.of === 'thread' ? <HistoryList items={history.items} /> : null}
            {fileList(detail.attachments, mayEdit(detail.authorUserId), 'Files on the discussion')}
            {mayEdit(detail.authorUserId) && (detail.attachments?.length ?? 0) < MAX_ATTACHMENTS ? (
              <label className="inline-flex cursor-pointer items-center gap-1 text-sm underline">
                <Paperclip className="h-3.5 w-3.5" aria-hidden />Attach files to the discussion
                <input type="file" multiple className="sr-only" aria-label="Attach files to the discussion" onChange={(e) => { attachToExisting(null, e.target.files); e.target.value = '' }} />
              </label>
            ) : null}

            <div className="flex flex-col gap-3 border-t border-border pt-5">
              <h4 className="text-sm font-semibold">Replies ({detail.replies.length})</h4>
              {detail.replies.length === 0 ? <p className="text-sm text-muted-foreground">No replies yet.</p> : detail.replies.map((item) => (
                <div key={item.id} className="rounded-md bg-muted px-3 py-2 text-sm">
                  <div className="flex items-start justify-between gap-2">
                    <strong>{item.authorName}</strong>
                    <div className="flex gap-2">
                      {mayEdit(item.authorUserId) ? <Button variant="soft" size="sm" disabled={busy} aria-label={`Edit reply by ${item.authorName}`} onClick={() => startEditReply(item)}>Edit</Button> : null}
                      {canModerate || item.authorUserId === session?.user.id ? <Button variant="softDestructive" size="sm" disabled={busy} aria-label={`Delete reply by ${item.authorName}`} onClick={() => void removeReply(item.id)}>Delete</Button> : null}
                    </div>
                  </div>
                  {editing?.kind === 'reply' && editing.id === item.id ? (
                    <FormLayout onSubmit={saveEdit} noValidate>
                      <Field id={`edit-reply-${item.id}`} label="Your reply" required><Textarea id={`edit-reply-${item.id}`} rows={3} maxLength={10000} value={editForm.body} onChange={(e) => setEditForm({ ...editForm, body: e.target.value })} /></Field>
                      <FormActions busy={busy} submitLabel="Save changes" onCancel={() => setEditing(null)} />
                    </FormLayout>
                  ) : <p className="whitespace-pre-wrap">{item.body}</p>}
                  <small className="text-muted-foreground">{new Date(item.createdAtUtc).toLocaleString()}{item.editedAtUtc ? ` · edited ${new Date(item.editedAtUtc).toLocaleString()}` : ''}</small>
                  {item.editedAtUtc && mayEdit(item.authorUserId) ? (
                    <button type="button" className="ml-2 text-xs underline" aria-label={`Edit history of the reply by ${item.authorName}`} onClick={() => void showHistory(item.id, `/api/v1/tenant/community/replies/${item.id}/history`)}>{history?.of === item.id ? 'Hide history' : 'Edit history'}</button>
                  ) : null}
                  {history?.of === item.id ? <HistoryList items={history.items} /> : null}
                  {fileList(item.attachments, mayEdit(item.authorUserId), `Files on the reply by ${item.authorName}`)}
                  {mayEdit(item.authorUserId) && (item.attachments?.length ?? 0) < MAX_ATTACHMENTS ? (
                    <label className="mt-1 inline-flex cursor-pointer items-center gap-1 text-xs underline">
                      <Paperclip className="h-3 w-3" aria-hidden />Attach
                      <input type="file" multiple className="sr-only" aria-label={`Attach files to the reply by ${item.authorName}`} onChange={(e) => { attachToExisting(item.id, e.target.files); e.target.value = '' }} />
                    </label>
                  ) : null}
                </div>
              ))}
              {detail.isLocked ? <p className="text-sm text-muted-foreground">This discussion is locked. New replies are disabled.</p> : null}
            </div>

            {canPost && !detail.isLocked ? (
              <FormLayout onSubmit={sendReply}>
                <FormSection title="Reply">
                  <Field id="forum-reply" label="Your reply" required><Textarea id="forum-reply" value={reply} onChange={(e) => setReply(e.target.value)} rows={3} maxLength={10000} /></Field>
                  <PickedFiles files={replyFiles} onPick={(list) => addFiles('reply', list)} onRemove={(index) => setReplyFiles((current) => current.filter((_, at) => at !== index))} label="Attach files to your reply" />
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
            <PickedFiles files={newFiles} onPick={(list) => addFiles('new', list)} onRemove={(index) => setNewFiles((current) => current.filter((_, at) => at !== index))} label="Attach files to the discussion" />
          </FormSection>
          <FormActions busy={busy} busyLabel="Posting…" submitLabel="Post discussion" onCancel={closeCreate} />
        </FormLayout>
      </SidePanel>
    </section>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }

/** Files chosen but not yet sent, with a way to add more or take one out. */
function PickedFiles({ files, onPick, onRemove, label }: { files: File[]; onPick: (list: FileList | null) => void; onRemove: (index: number) => void; label: string }) {
  return (
    <div className="flex flex-col gap-1.5">
      <label className="inline-flex w-fit cursor-pointer items-center gap-1 text-sm underline">
        <Paperclip className="h-3.5 w-3.5" aria-hidden />{label}
        <input type="file" multiple className="sr-only" aria-label={label} onChange={(event) => { onPick(event.target.files); event.target.value = '' }} />
      </label>
      {files.length > 0 ? (
        <ul className="flex flex-col gap-1 text-sm" aria-label="Files to attach">
          {files.map((file, index) => (
            <li key={`${file.name}-${index}`} className="flex items-center gap-2">
              <span className="truncate">{file.name}</span><span className="text-xs text-muted-foreground">{formatBytes(file.size)}</span>
              <button type="button" className="text-xs text-muted-foreground hover:underline" aria-label={`Do not attach ${file.name}`} onClick={() => onRemove(index)}>Remove</button>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  )
}

/** The wording a post had before each edit, newest first. */
function HistoryList({ items }: { items: Edit[] }) {
  return (
    <ol className="flex flex-col gap-2 rounded-md border border-border p-3 text-sm" aria-label="Edit history">
      {items.length === 0 ? <li className="text-muted-foreground">No earlier versions.</li> : items.map((item) => (
        <li key={item.id}>
          <small className="text-muted-foreground">Before the edit by {item.editedByName} on {new Date(item.editedAtUtc).toLocaleString()}</small>
          {item.previousTitle ? <p className="font-medium">{item.previousTitle}</p> : null}
          <p className="whitespace-pre-wrap">{item.previousBody}</p>
        </li>
      ))}
    </ol>
  )
}
