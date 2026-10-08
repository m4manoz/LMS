import { useCallback, useEffect, useRef, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { Paperclip, Plus, X } from 'lucide-react'
import SidePanel from '@/components/SidePanel'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, PageHeader, RowList } from '@/components/form'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, apiRequest, downloadFile } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import { subscribeMessages } from '@/lib/liveMessages'
import { cn } from '@/lib/utils'

type Conversation = { id: string; kind: 'Direct' | 'Course'; title: string; courseId: string | null; lastMessagePreview: string | null; lastActivityAtUtc: string; unreadCount: number }
type Attachment = { fileName: string; sizeBytes: number; contentType: string }
type Message = { id: string; senderUserId: string; senderName: string; body: string; createdAtUtc: string; isMine: boolean; editedAtUtc?: string | null; isDeleted?: boolean; attachment?: Attachment | null }
type Contact = { userId: string; name: string; email: string; isStaff: boolean }
type CourseOption = { courseId: string; title: string }

/** How often to look for news when the live connection is down, and as a safety net when it is up. */
const POLL_MS = 10000
const POLL_WHEN_LIVE_MS = 60000
const MAX_FILE_BYTES = 25 * 1024 * 1024

function formatBytes(bytes: number) { return bytes < 1024 ? `${bytes} B` : bytes < 1024 * 1024 ? `${Math.round(bytes / 1024)} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB` }

/** "14:05" for today, otherwise "3 Oct". */
export function formatMessageTime(value: string, now = new Date()): string {
  const date = new Date(value)
  const sameDay = date.getFullYear() === now.getFullYear() && date.getMonth() === now.getMonth() && date.getDate() === now.getDate()
  return sameDay ? date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : date.toLocaleDateString([], { day: 'numeric', month: 'short' })
}

export default function MessagesPage() {
  const { session } = useAuth()
  const canSend = session?.permissions.includes('collaboration.manage') ?? false
  const canManageCourses = session?.permissions.includes('course.manage') ?? false
  const canModerate = session?.permissions.includes('forum.moderate') ?? false
  const [conversations, setConversations] = useState<Conversation[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [messages, setMessages] = useState<Message[]>([])
  const [draft, setDraft] = useState('')
  const [contacts, setContacts] = useState<Contact[]>([])
  const [search, setSearch] = useState('')
  const [courses, setCourses] = useState<CourseOption[]>([])
  const [newOpen, setNewOpen] = useState(false)
  const [coursesOpen, setCoursesOpen] = useState(false)
  const [contactId, setContactId] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [file, setFile] = useState<File | null>(null)
  const [editingId, setEditingId] = useState<string | null>(null)
  const [editText, setEditText] = useState('')
  const [live, setLive] = useState(false)
  const fileRef = useRef<HTMLInputElement>(null)
  const endRef = useRef<HTMLDivElement>(null)

  const selected = conversations.find((item) => item.id === selectedId) ?? null

  const loadConversations = useCallback(async () => {
    try { setConversations(await apiRequest<Conversation[]>('/api/v1/tenant/messages/conversations')) }
    catch (exception) { setError(readError(exception, 'Unable to load your conversations.')) }
  }, [])

  const loadThread = useCallback(async (id: string) => {
    try {
      setMessages(await apiRequest<Message[]>(`/api/v1/tenant/messages/conversations/${id}/messages`))
      await apiRequest(`/api/v1/tenant/messages/conversations/${id}/read`, { method: 'POST' })
      setConversations((current) => current.map((item) => item.id === id ? { ...item, unreadCount: 0 } : item))
    } catch (exception) { setError(readError(exception, 'Unable to open the conversation.')) }
  }, [])

  useEffect(() => { void loadConversations() }, [loadConversations])

  // News arrives the moment it happens over the live connection; polling keeps going as a safety net, faster when the connection is down.
  useEffect(() => subscribeMessages((signal) => {
    void loadConversations()
    if (selectedId && signal.conversationId === selectedId) void loadThread(selectedId)
  }, setLive), [selectedId, loadConversations, loadThread])
  useEffect(() => {
    const timer = window.setInterval(() => {
      void loadConversations()
      if (selectedId) void loadThread(selectedId)
    }, live ? POLL_WHEN_LIVE_MS : POLL_MS)
    return () => window.clearInterval(timer)
  }, [selectedId, live, loadConversations, loadThread])

  useEffect(() => { endRef.current?.scrollIntoView?.({ block: 'end' }) }, [messages.length, selectedId])

  useEffect(() => {
    if (!newOpen) return
    const timer = window.setTimeout(() => {
      apiRequest<Contact[]>(`/api/v1/tenant/messages/contacts${search.trim() ? `?q=${encodeURIComponent(search.trim())}` : ''}`)
        .then(setContacts).catch((exception) => setError(readError(exception, 'Unable to load contacts.')))
    }, 250)
    return () => window.clearTimeout(timer)
  }, [newOpen, search])

  useEffect(() => {
    if (!coursesOpen) return
    if (canManageCourses) {
      apiRequest<{ id: string; title: string; status: string }[]>('/api/v1/tenant/courses')
        .then((items) => setCourses(items.filter((item) => item.status === 'Published').map((item) => ({ courseId: item.id, title: item.title }))))
        .catch((exception) => setError(readError(exception, 'Unable to load courses.')))
    } else {
      apiRequest<{ courseId: string; courseTitle: string; status: string }[]>('/api/v1/tenant/enrollments')
        .then((items) => setCourses(items.filter((item) => item.status === 'Active' || item.status === 'Completed').map((item) => ({ courseId: item.courseId, title: item.courseTitle }))))
        .catch((exception) => setError(readError(exception, 'Unable to load your courses.')))
    }
  }, [coursesOpen, canManageCourses])

  function openNew() { setError(null); setSearch(''); setContactId(''); setCoursesOpen(false); setNewOpen(true) }
  function openCourses() { setError(null); setNewOpen(false); setCoursesOpen(true) }

  async function open(conversationId: string) {
    setSelectedId(conversationId)
    setNewOpen(false)
    setCoursesOpen(false)
    setMessages([])
    clearFile(); setEditingId(null)
    await loadConversations()
    await loadThread(conversationId)
  }

  async function run(action: () => Promise<void>, failure: string) {
    setBusy(true); setError(null)
    try { await action() } catch (exception) { setError(readError(exception, failure)) } finally { setBusy(false) }
  }

  const startDirect = (userId: string) => run(async () => {
    const { id } = await apiRequest<{ id: string }>('/api/v1/tenant/messages/conversations/direct', { method: 'POST', body: JSON.stringify({ userId }) })
    await open(id)
  }, 'Unable to start the conversation.')

  const openCourse = (courseId: string) => run(async () => {
    const { id } = await apiRequest<{ id: string }>('/api/v1/tenant/messages/conversations/course', { method: 'POST', body: JSON.stringify({ courseId }) })
    await open(id)
  }, 'Unable to open the course chat.')

  function clearFile() { setFile(null); if (fileRef.current) fileRef.current.value = '' }

  function chooseFile(chosen: File | undefined) {
    if (!chosen) return
    if (chosen.size > MAX_FILE_BYTES) { setError('Files must be 25 MB or smaller.'); clearFile(); return }
    setError(null); setFile(chosen)
  }

  const send = () => {
    const body = draft.trim()
    if (!selectedId || (!body && !file)) return Promise.resolve()
    return run(async () => {
      if (file) {
        const form = new FormData()
        form.append('file', file)
        if (body) form.append('body', body)
        await apiRequest(`/api/v1/tenant/messages/conversations/${selectedId}/messages/upload`, { method: 'POST', body: form })
      } else {
        await apiRequest(`/api/v1/tenant/messages/conversations/${selectedId}/messages`, { method: 'POST', body: JSON.stringify({ body }) })
      }
      setDraft(''); clearFile()
      await loadThread(selectedId)
      await loadConversations()
    }, 'Unable to send the message.')
  }

  const startEdit = (message: Message) => { setEditingId(message.id); setEditText(message.body); setError(null) }
  const saveEdit = (message: Message) => {
    if (!selectedId) return Promise.resolve()
    const body = editText.trim()
    if (!body && !message.attachment) { setError('A message cannot be empty.'); return Promise.resolve() }
    return run(async () => {
      await apiRequest(`/api/v1/tenant/messages/conversations/${selectedId}/messages/${message.id}`, { method: 'PUT', body: JSON.stringify({ body }) })
      setEditingId(null)
      await loadThread(selectedId)
    }, 'Unable to save the change.')
  }
  const removeMessage = (message: Message) => {
    if (!selectedId || !window.confirm('Delete this message? It will be removed for everyone in the conversation.')) return Promise.resolve()
    return run(async () => {
      await apiRequest(`/api/v1/tenant/messages/conversations/${selectedId}/messages/${message.id}`, { method: 'DELETE' })
      await loadThread(selectedId)
      await loadConversations()
    }, 'Unable to delete the message.')
  }
  const downloadAttachment = (message: Message) => run(
    () => downloadFile(`/api/v1/tenant/messages/conversations/${selectedId}/messages/${message.id}/attachment`, message.attachment?.fileName ?? 'attachment'),
    'Unable to download the file.')

  const totalUnread = conversations.reduce((sum, item) => sum + item.unreadCount, 0)

  function startSelected(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!contactId || !contacts.some((contact) => contact.userId === contactId)) { setError('Choose a person to message.'); return }
    void startDirect(contactId)
  }

  const panelOpen = newOpen || coursesOpen

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Messages" description="Talk to your teachers and chat with your course."
        actions={<><Button variant="outline" onClick={openCourses}>Course chats</Button>{canSend ? <Button onClick={openNew}><Plus className="mr-1 h-4 w-4" aria-hidden />New message</Button> : null}</>} />
      {!panelOpen ? <ErrorBanner message={error} /> : null}

      <div className="grid gap-4 lg:grid-cols-[320px_minmax(0,1fr)]">
        <Card className="self-start">
          <CardHeader><CardTitle>Inbox ({conversations.length}){totalUnread > 0 ? ` · ${totalUnread} unread` : ''}</CardTitle></CardHeader>
          <CardContent className="flex flex-col gap-2">
            {conversations.length === 0 ? <p className="text-sm text-muted-foreground">No conversations yet. Start one with “New message” or open a course chat.</p> : conversations.map((item) => (
              <button key={item.id} type="button" onClick={() => void open(item.id)}
                className={cn('flex flex-col gap-1 rounded-md border border-border px-3 py-2 text-left text-sm hover:bg-muted', selectedId === item.id && 'border-l-4 border-l-primary bg-muted')}>
                <span className="flex items-center justify-between gap-2">
                  <strong className="truncate">{item.title}</strong>
                  <small className="shrink-0 text-muted-foreground">{formatMessageTime(item.lastActivityAtUtc)}</small>
                </span>
                <span className="flex items-center justify-between gap-2">
                  <small className="truncate text-muted-foreground">{item.lastMessagePreview ?? 'No messages yet'}</small>
                  {item.unreadCount > 0 ? <Badge variant="default">{item.unreadCount} new</Badge> : null}
                </span>
              </button>
            ))}
          </CardContent>
        </Card>

        {!selected ? (
          <Card className="self-start">
            <CardHeader><CardTitle>Select a conversation</CardTitle><CardDescription>Choose one on the left to read and reply.</CardDescription></CardHeader>
          </Card>
        ) : (
          <Card className="flex min-h-[28rem] flex-col">
            <CardHeader>
              <CardTitle>{selected.title}</CardTitle>
              <CardDescription>{selected.kind === 'Course' ? 'Everyone enrolled in this course and its teachers can read this chat.' : 'Only the two of you can see this conversation.'}</CardDescription>
            </CardHeader>
            <CardContent className="flex min-h-0 flex-1 flex-col gap-3">
              <div className="flex max-h-[24rem] min-h-48 flex-1 flex-col gap-2 overflow-y-auto rounded-md border border-border p-3" aria-live="polite">
                {messages.length === 0 ? <p className="text-sm text-muted-foreground">No messages yet. Say hello.</p> : messages.map((message) => (
                  <div key={message.id} className={cn('flex flex-col gap-0.5', message.isMine ? 'items-end' : 'items-start')}>
                    <small className="text-muted-foreground">{message.isMine ? 'You' : message.senderName} · {formatMessageTime(message.createdAtUtc)}{message.editedAtUtc && !message.isDeleted ? ' · edited' : ''}</small>
                    {message.isDeleted ? (
                      <p className="max-w-[85%] rounded-lg border border-dashed border-border px-3 py-2 text-sm italic text-muted-foreground">This message was deleted.</p>
                    ) : editingId === message.id ? (
                      <div className="flex w-full max-w-[85%] flex-col gap-2">
                        <Textarea aria-label="Edit message" rows={2} maxLength={5000} value={editText} onChange={(e) => setEditText(e.target.value)} />
                        <div className="flex gap-2">
                          <Button size="sm" disabled={busy} onClick={() => void saveEdit(message)}>Save</Button>
                          <Button size="sm" variant="outline" disabled={busy} onClick={() => setEditingId(null)}>Cancel</Button>
                        </div>
                      </div>
                    ) : (
                      <div className={cn('flex max-w-[85%] flex-col gap-1 rounded-lg px-3 py-2 text-sm', message.isMine ? 'bg-primary/15 text-foreground' : 'bg-muted')}>
                        {message.body ? <p className="whitespace-pre-wrap">{message.body}</p> : null}
                        {message.attachment ? (
                          <button type="button" className="flex items-center gap-1.5 self-start text-left underline" aria-label={`Download ${message.attachment.fileName}`} onClick={() => void downloadAttachment(message)}>
                            <Paperclip className="h-3.5 w-3.5 shrink-0" aria-hidden />{message.attachment.fileName} <span className="text-xs text-muted-foreground no-underline">({formatBytes(message.attachment.sizeBytes)})</span>
                          </button>
                        ) : null}
                      </div>
                    )}
                    {!message.isDeleted && editingId !== message.id && canSend && (message.isMine || (canModerate && selected.kind === 'Course')) ? (
                      <span className="flex gap-3 text-xs text-muted-foreground">
                        {message.isMine ? <button type="button" className="hover:underline" aria-label={`Edit message: ${message.body.slice(0, 30) || message.attachment?.fileName || ''}`} onClick={() => startEdit(message)}>Edit</button> : null}
                        <button type="button" className="hover:underline" aria-label={`Delete message: ${message.body.slice(0, 30) || message.attachment?.fileName || ''}`} onClick={() => void removeMessage(message)}>Delete</button>
                      </span>
                    ) : null}
                  </div>
                ))}
                <div ref={endRef} />
              </div>
              {file ? (
                <div className="flex items-center gap-2 rounded-md border border-border px-3 py-1.5 text-sm" role="status">
                  <Paperclip className="h-4 w-4 shrink-0" aria-hidden /><span className="truncate">{file.name} ({formatBytes(file.size)})</span>
                  <Button type="button" variant="outline" size="icon" className="ml-auto h-6 w-6" aria-label="Remove the attached file" onClick={clearFile}><X className="h-3.5 w-3.5" aria-hidden /></Button>
                </div>
              ) : null}
              {canSend ? (
                <form className="flex items-end gap-2" onSubmit={(event) => { event.preventDefault(); void send() }}>
                  <div className="flex flex-1 flex-col gap-1.5">
                    <Label htmlFor="message-draft" className="sr-only">Message</Label>
                    <Textarea
                      id="message-draft" rows={2} maxLength={5000} placeholder="Write a message… (Enter to send, Shift+Enter for a new line)"
                      value={draft} onChange={(e) => setDraft(e.target.value)}
                      onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); void send() } }}
                    />
                  </div>
                  <div className="flex shrink-0 flex-col items-stretch gap-2">
                    <input ref={fileRef} type="file" className="sr-only" aria-label="Attach a file" onChange={(e) => chooseFile(e.target.files?.[0])} />
                    <Button type="button" variant="outline" size="sm" disabled={busy} onClick={() => fileRef.current?.click()}><Paperclip className="mr-1 h-4 w-4" aria-hidden />Attach</Button>
                    <Button type="submit" disabled={busy || (!draft.trim() && !file)}>Send</Button>
                  </div>
                </form>
              ) : <p className="text-sm text-muted-foreground">You can read messages but not send them.</p>}
            </CardContent>
          </Card>
        )}
      </div>

      <SidePanel open={coursesOpen} label="Course chats" onClose={() => setCoursesOpen(false)}>
        <div className="flex flex-col gap-4">
          <ErrorBanner message={error} />
          <p className="text-sm text-muted-foreground">One shared chat per course for its learners and teachers.</p>
          {courses.length === 0 ? <EmptyState>No courses available.</EmptyState> : (
            <RowList label="Course chats">
              {courses.map((course) => (
                <ListRow key={course.courseId} columns="sm:grid-cols-[minmax(0,1fr)_auto]">
                  <strong className="truncate">{course.title}</strong>
                  <div className="flex justify-end">
                    {canSend || conversations.some((c) => c.courseId === course.courseId)
                      ? <Button variant="soft" size="sm" disabled={busy} aria-label={`Open chat for ${course.title}`} onClick={() => void openCourse(course.courseId)}>Open chat</Button> : null}
                  </div>
                </ListRow>
              ))}
            </RowList>
          )}
        </div>
      </SidePanel>

      <SidePanel open={newOpen && canSend} label="New message" onClose={() => setNewOpen(false)}>
        <FormLayout onSubmit={startSelected}>
          <ErrorBanner message={error} />
          <FormSection title="Start a conversation" description={canManageCourses ? 'Search for anyone in your organization.' : 'You can message your teachers and staff.'}>
            <Field id="contact-search" label="Search by name or email"><Input id="contact-search" value={search} onChange={(e) => setSearch(e.target.value)} /></Field>
            <Field id="contact-person" label="Person" required hint={contacts.length === 0 ? 'No matching people.' : undefined}>
              <Select id="contact-person" value={contactId} onChange={(e) => setContactId(e.target.value)}>
                <option value="">Choose a person</option>
                {contacts.map((contact) => <option key={contact.userId} value={contact.userId}>{contact.name} ({contact.email}){contact.isStaff ? ' · Staff' : ''}</option>)}
              </Select>
            </Field>
          </FormSection>
          <FormActions busy={busy} submitLabel="Start conversation" busyLabel="Starting…" onCancel={() => setNewOpen(false)} />
        </FormLayout>
      </SidePanel>
    </section>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
