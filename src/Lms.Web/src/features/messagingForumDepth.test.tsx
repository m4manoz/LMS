import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { act } from 'react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ForumsPage, { filesProblem } from './ForumsPage'
import MessagesPage from './MessagesPage'
import { parseSignals, type MessageSignal } from '@/lib/liveMessages'

const request = vi.fn()
const download = vi.fn()
let permissions: string[] = []
let emit: ((signal: MessageSignal) => void) | null = null
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args), downloadFile: (...args: unknown[]) => download(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions, accessToken: 't', user: { id: 'me' } } }) }))
vi.mock('@/lib/liveMessages', async () => {
  const actual = await vi.importActual<typeof import('@/lib/liveMessages')>('@/lib/liveMessages')
  return { ...actual, subscribeMessages: (listener: (signal: MessageSignal) => void, onStatus?: (connected: boolean) => void) => { emit = listener; onStatus?.(true); return () => { emit = null } } }
})

const calls = (method: string, fragment = '') => request.mock.calls.filter((call) => (call[1]?.method ?? 'GET') === method && String(call[0]).includes(fragment))
const bodyOf = (call: unknown[]) => JSON.parse((call[1] as { body: string }).body)
const now = new Date().toISOString()

beforeEach(() => { request.mockReset(); download.mockReset(); download.mockResolvedValue(undefined); request.mockResolvedValue(null); emit = null; vi.restoreAllMocks() })

describe('parseSignals', () => {
  it('turns complete events into signals and keeps a partial one for later', () => {
    const got: MessageSignal[] = []
    let rest = parseSignals(': connected\n\nevent: message\ndata: {"type":"message","conversationId":"c1","messageId":"m1"}\n\nevent: message\ndata: {"type":"ed', (signal) => got.push(signal))
    expect(got).toEqual([{ type: 'message', conversationId: 'c1', messageId: 'm1' }])
    rest = parseSignals(rest + 'ited","conversationId":"c1","messageId":"m1"}\n\n', (signal) => got.push(signal))
    expect(got.map((signal) => signal.type)).toEqual(['message', 'edited'])
    expect(rest).toBe('')
  })

  it('ignores pings and damaged events', () => {
    const got: MessageSignal[] = []
    parseSignals(': ping\n\nevent: message\ndata: not json\n\n', (signal) => got.push(signal))
    expect(got).toEqual([])
  })
})

describe('MessagesPage', () => {
  const conversation = { id: 'c1', kind: 'Direct', title: 'Ms Rai', courseId: null, lastMessagePreview: 'Hi', lastActivityAtUtc: now, unreadCount: 0 }
  const course = { id: 'c2', kind: 'Course', title: 'Art · course chat', courseId: 'k1', lastMessagePreview: 'Hi', lastActivityAtUtc: now, unreadCount: 0 }
  let messages: unknown[]
  let conversations: unknown[]

  function serve() {
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (options?.method) return Promise.resolve({})
      if (path.endsWith('/conversations')) return Promise.resolve(conversations)
      if (path.endsWith('/messages')) return Promise.resolve(messages)
      return Promise.resolve(null)
    })
  }
  const base = { senderUserId: 'u2', senderName: 'Ms Rai', createdAtUtc: now, isMine: false }
  beforeEach(() => {
    permissions = ['collaboration.read', 'collaboration.manage']
    conversations = [conversation]
    messages = [
      { ...base, id: 'm1', body: 'From her' },
      { ...base, id: 'm2', senderUserId: 'me', senderName: 'Me', isMine: true, body: 'Mine, fixed', editedAtUtc: now },
      { ...base, id: 'm3', isMine: false, body: '', isDeleted: true },
      { ...base, id: 'm4', isMine: true, senderUserId: 'me', body: 'See the file', attachment: { fileName: 'plan.pdf', sizeBytes: 2048, contentType: 'application/pdf' } },
    ]
    serve()
  })

  it('shows edited marks, deleted markers and file attachments', async () => {
    render(<MessagesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Ms Rai/ }))
    expect(await screen.findByText('From her')).toBeInTheDocument()
    expect(screen.getByText(/· edited/)).toBeInTheDocument()
    expect(screen.getByText('This message was deleted.')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Download plan.pdf' }))
    expect(download).toHaveBeenCalledWith('/api/v1/tenant/messages/conversations/c1/messages/m4/attachment', 'plan.pdf')
  })

  it('lets you edit and delete only your own messages', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<MessagesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Ms Rai/ }))
    await screen.findByText('From her')
    expect(screen.queryByRole('button', { name: /Edit message: From her/ })).toBeNull()
    expect(screen.queryByRole('button', { name: /Delete message: From her/ })).toBeNull()

    await userEvent.click(screen.getByRole('button', { name: /Edit message: Mine, fixed/ }))
    const box = screen.getByLabelText('Edit message')
    await userEvent.clear(box)
    await userEvent.type(box, 'Mine, fixed again')
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(calls('PUT', '/messages/m2')).toHaveLength(1))
    expect(bodyOf(calls('PUT', '/messages/m2')[0])).toEqual({ body: 'Mine, fixed again' })

    await userEvent.click(screen.getByRole('button', { name: /Delete message: Mine, fixed/ }))
    await waitFor(() => expect(calls('DELETE', '/messages/m2')).toHaveLength(1))
  })

  it('does not delete when the confirmation is declined and refuses to empty a message', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(false)
    render(<MessagesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Ms Rai/ }))
    await screen.findByText('From her')
    await userEvent.click(screen.getByRole('button', { name: /Delete message: Mine, fixed/ }))
    expect(calls('DELETE')).toHaveLength(0)
    await userEvent.click(screen.getByRole('button', { name: /Edit message: Mine, fixed/ }))
    await userEvent.clear(screen.getByLabelText('Edit message'))
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('A message cannot be empty.')
    expect(calls('PUT')).toHaveLength(0)
  })

  it('sends a file with the words as a form, and lets you take the file back out', async () => {
    render(<MessagesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Ms Rai/ }))
    await screen.findByText('From her')
    await userEvent.upload(screen.getByLabelText('Attach a file'), new File(['x'], 'notes.txt', { type: 'text/plain' }))
    expect(screen.getByRole('status')).toHaveTextContent('notes.txt')
    expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled()   // a file alone is enough
    await userEvent.type(screen.getByLabelText('Message'), 'Here you go')
    await userEvent.click(screen.getByRole('button', { name: 'Send' }))
    await waitFor(() => expect(calls('POST', '/messages/upload')).toHaveLength(1))
    const form = (calls('POST', '/messages/upload')[0][1] as { body: FormData }).body
    expect(form.get('body')).toBe('Here you go')
    expect((form.get('file') as File).name).toBe('notes.txt')
    await waitFor(() => expect(screen.queryByRole('status')).toBeNull())

    await userEvent.upload(screen.getByLabelText('Attach a file'), new File(['y'], 'other.txt'))
    await userEvent.click(screen.getByRole('button', { name: 'Remove the attached file' }))
    expect(screen.queryByRole('status')).toBeNull()
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled()
  })

  it('refreshes the open conversation the moment a live signal arrives for it, and only then', async () => {
    render(<MessagesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Ms Rai/ }))
    await screen.findByText('From her')
    const loads = () => calls('GET', '/conversations/c1/messages').length
    const before = loads()
    messages = [...messages, { ...base, id: 'm9', body: 'Brand new' }]
    act(() => emit?.({ type: 'message', conversationId: 'someone-else', messageId: 'x' }))
    expect(loads()).toBe(before)
    act(() => emit?.({ type: 'message', conversationId: 'c1', messageId: 'm9' }))
    expect(await screen.findByText('Brand new')).toBeInTheDocument()
    expect(loads()).toBeGreaterThan(before)
  })

  it('lets a moderator delete anyone’s message in a course chat but not in a direct chat', async () => {
    permissions = ['collaboration.read', 'collaboration.manage', 'forum.moderate']
    conversations = [conversation, course]
    serve()
    render(<MessagesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Ms Rai/ }))
    await screen.findByText('From her')
    expect(screen.queryByRole('button', { name: /Delete message: From her/ })).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: /Art · course chat/ }))
    expect(await screen.findByRole('button', { name: /Delete message: From her/ })).toBeInTheDocument()
  })
})

describe('ForumsPage', () => {
  const thread = { id: 't1', courseId: null, courseTitle: null, title: 'Help with limits', authorName: 'Ada', isPinned: false, isLocked: false, replyCount: 1, createdAtUtc: '2026-01-01T00:00:00Z', lastActivityAtUtc: '2026-01-02T00:00:00Z' }
  const file = (id: string, fileName: string) => ({ id, fileName, sizeBytes: 1536, contentType: 'text/plain' })
  let detail: Record<string, unknown>
  let history: unknown[]

  function serve() {
    request.mockImplementation(async (url: string, init?: { method?: string }) => {
      if (init?.method === 'POST' && url.endsWith('/threads')) return { id: 't1' }
      if (init?.method) return null
      if (url === '/api/v1/tenant/community/threads') return [thread]
      if (url === '/api/v1/tenant/community/threads/t1') return detail
      if (url.endsWith('/history')) return history
      return []
    })
  }

  beforeEach(() => {
    permissions = ['collaboration.read', 'collaboration.manage']
    history = [{ id: 'h2', editedByName: 'Me', editedAtUtc: now, previousTitle: 'Help with limits?', previousBody: 'Second wording' }, { id: 'h1', editedByName: 'Me', editedAtUtc: now, previousTitle: 'Limits', previousBody: 'First wording' }]
    detail = {
      id: 't1', courseId: null, title: 'Help with limits', body: 'How do limits work?', authorUserId: 'me', authorName: 'Me', isPinned: false, isLocked: false, createdAtUtc: now, editedAtUtc: now,
      attachments: [file('a1', 'question.txt')],
      replies: [
        { id: 'r1', authorUserId: 'me', authorName: 'Me', body: 'My reply', createdAtUtc: now, editedAtUtc: now, attachments: [file('a2', 'worked.txt')] },
        { id: 'r2', authorUserId: 'bo', authorName: 'Bo', body: 'Bo reply', createdAtUtc: now, attachments: [] },
      ],
    }
    serve()
  })

  const openThread = async () => {
    render(<ForumsPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Help with limits' }))
    return await screen.findByRole('dialog', { name: 'Discussion details' })
  }

  it('marks edited posts and shows the history only to someone who may edit the post', async () => {
    const panel = await openThread()
    expect(within(panel).getAllByText(/edited/i).length).toBeGreaterThan(1)
    await userEvent.click(within(panel).getByRole('button', { name: 'Edit history' }))
    const list = await within(panel).findByRole('list', { name: 'Edit history' })
    expect(within(list).getAllByRole('listitem').map((item) => item.textContent)).toEqual([expect.stringContaining('Second wording'), expect.stringContaining('First wording')])
    expect(within(list).getByText('Help with limits?')).toBeInTheDocument()
    await userEvent.click(within(panel).getByRole('button', { name: 'Hide history' }))
    expect(within(panel).queryByRole('list', { name: 'Edit history' })).toBeNull()
    expect(within(panel).queryByRole('button', { name: 'Edit history of the reply by Bo' })).toBeNull()   // Bo's reply is not mine and was never edited
  })

  it('edits the discussion with the same title and body rules and sends the change', async () => {
    const panel = await openThread()
    await userEvent.click(within(panel).getByRole('button', { name: 'Edit' }))
    const title = within(panel).getByLabelText('Discussion title')
    await userEvent.clear(title)
    await userEvent.type(title, 'Hi')
    await userEvent.click(within(panel).getByRole('button', { name: 'Save changes' }))
    expect(await within(panel).findByRole('alert')).toHaveTextContent('at least 3 characters')
    expect(calls('PUT')).toHaveLength(0)
    await userEvent.clear(title)
    await userEvent.type(title, 'Help with limits, week 2')
    await userEvent.click(within(panel).getByRole('button', { name: 'Save changes' }))
    await waitFor(() => expect(calls('PUT', '/threads/t1')).toHaveLength(1))
    expect(bodyOf(calls('PUT', '/threads/t1')[0])).toEqual({ title: 'Help with limits, week 2', body: 'How do limits work?' })
  })

  it('edits only my own reply', async () => {
    const panel = await openThread()
    expect(within(panel).queryByRole('button', { name: 'Edit reply by Bo' })).toBeNull()
    await userEvent.click(within(panel).getByRole('button', { name: 'Edit reply by Me' }))
    const box = within(panel).getAllByLabelText('Your reply').find((element) => (element as HTMLTextAreaElement).value === 'My reply')!
    await userEvent.clear(box)
    await userEvent.type(box, 'My better reply')
    await userEvent.click(within(panel).getAllByRole('button', { name: 'Save changes' })[0])
    await waitFor(() => expect(calls('PUT', '/replies/r1')).toHaveLength(1))
    expect(bodyOf(calls('PUT', '/replies/r1')[0])).toEqual({ body: 'My better reply' })
  })

  it('lists and downloads attachments and removes my own after confirming', async () => {
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const panel = await openThread()
    await userEvent.click(within(panel).getByRole('button', { name: 'Download question.txt' }))
    expect(download).toHaveBeenCalledWith('/api/v1/tenant/community/attachments/a1', 'question.txt')
    await userEvent.click(within(panel).getByRole('button', { name: 'Remove worked.txt' }))
    await waitFor(() => expect(calls('DELETE', '/attachments/a2')).toHaveLength(1))
  })

  it('attaches files to an existing discussion', async () => {
    const panel = await openThread()
    await userEvent.upload(within(panel).getByLabelText('Attach files to the discussion'), new File(['x'], 'extra.txt', { type: 'text/plain' }))
    await waitFor(() => expect(calls('POST', '/threads/t1/attachments')).toHaveLength(1))
    expect(((calls('POST', '/threads/t1/attachments')[0][1] as { body: FormData }).body.get('file') as File).name).toBe('extra.txt')
  })

  it('posts a discussion and then attaches the chosen files, telling you if one fails', async () => {
    request.mockImplementation(async (url: string, init?: { method?: string; body?: unknown }) => {
      if (init?.method === 'POST' && url.endsWith('/threads')) return { id: 't1' }
      if (init?.method === 'POST' && url.endsWith('/attachments')) { if (((init.body as FormData).get('file') as File).name === 'bad.exe') throw new Error('refused'); return file('n1', 'ok.txt') }
      if (url === '/api/v1/tenant/community/threads/t1') return detail
      if (url === '/api/v1/tenant/courses') return []
      return [thread]
    })
    render(<ForumsPage />)
    await screen.findByText('Help with limits')
    await userEvent.click(screen.getByRole('button', { name: /Start a discussion/ }))
    await userEvent.type(screen.getByLabelText('Title'), 'New topic')
    await userEvent.type(screen.getByLabelText('Message'), 'Words')
    await userEvent.upload(screen.getByLabelText('Attach files to the discussion'), [new File(['1'], 'ok.txt'), new File(['2'], 'bad.exe')])
    expect(screen.getByRole('list', { name: 'Files to attach' })).toHaveTextContent('ok.txt')
    await userEvent.click(screen.getByRole('button', { name: 'Post discussion' }))
    await waitFor(() => expect(calls('POST', '/attachments')).toHaveLength(2))
    expect(calls('POST', '/attachments').every((call) => String(call[0]).includes('/threads/t1/attachments'))).toBe(true)
    await screen.findByRole('dialog', { name: 'Discussion details' })
  })

  it('attaches files to a reply after posting it', async () => {
    const panel = await openThread()
    request.mockImplementation(async (url: string, init?: { method?: string; body?: unknown }) => {
      if (init?.method === 'POST' && url.endsWith('/replies')) return { threadId: 't1' }
      if (init?.method === 'POST' && url.endsWith('/attachments')) return file('n2', 'r.txt')
      if (url === '/api/v1/tenant/community/threads/t1') return { ...detail, replies: [...(detail.replies as unknown[]), { id: 'r9', authorUserId: 'me', authorName: 'Me', body: 'Fresh', createdAtUtc: now, attachments: [] }] }
      return [thread]
    })
    await userEvent.type(within(panel).getByLabelText('Your reply'), 'Fresh')
    await userEvent.upload(within(panel).getByLabelText('Attach files to your reply'), new File(['1'], 'r.txt'))
    await userEvent.click(within(panel).getByRole('button', { name: 'Post reply' }))
    await waitFor(() => expect(calls('POST', '/attachments')).toHaveLength(1))
    expect(((calls('POST', '/attachments')[0][1] as { body: FormData }).body).get('replyId')).toBe('r9')
  })

  it('hides editing from people who may not edit and from everyone once a thread is locked, except moderators', async () => {
    detail = { ...detail, authorUserId: 'someone-else', authorName: 'Ada', replies: [] }
    serve()
    const panel = await openThread()
    expect(within(panel).queryByRole('button', { name: 'Edit' })).toBeNull()
    expect(within(panel).queryByLabelText('Attach files to the discussion')).toBeNull()
  })

  it('refuses too many or too large files before sending', () => {
    expect(filesProblem([])).toBeNull()
    expect(filesProblem(Array.from({ length: 5 }, () => new File(['x'], 'a.txt')))).toBeNull()
    expect(filesProblem(Array.from({ length: 6 }, () => new File(['x'], 'a.txt')))).toMatch(/at most 5/)
    expect(filesProblem([new File(['x'], 'a.txt')], 5)).toMatch(/at most 5/)
    const big = new File(['x'], 'big.bin'); Object.defineProperty(big, 'size', { value: 26 * 1024 * 1024 })
    expect(filesProblem([big])).toMatch(/big\.bin is larger than 25 MB/)
  })
})
