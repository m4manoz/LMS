import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import MyTasksPage, { kindsPresent } from './MyTasksPage'
import NotificationInbox, { INBOX_LIMIT, type InboxItem } from './NotificationInbox'
import NotificationPreferences, { isEnabled, notificationTypes } from './NotificationPreferences'
import NotificationsPage from './NotificationsPage'
import { NOTIFICATIONS_CHANGED, notificationTarget, timeAgo } from '@/lib/notifications'

const request = vi.fn()
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})

const calls = (fragment: string, method?: string) => request.mock.calls.filter((call) => String(call[0]).includes(fragment) && (method === undefined || call[1]?.method === method))

describe('notification helpers', () => {
  it('sends each kind of notification to the screen that explains it', () => {
    expect(notificationTarget('ANNOUNCEMENT')).toBe('announcements')
    expect(notificationTarget('ASSIGNMENT_GRADED')).toBe('assignments')
    expect(notificationTarget('COURSE_UPDATED')).toBe('learning')
    expect(notificationTarget('COURSE_INVITATION')).toBe('invitations')
    expect(notificationTarget('CERTIFICATE_ISSUED')).toBe('certificates')
    expect(notificationTarget('SOMETHING_NEW')).toBeNull()
  })

  it('says how long ago in plain words, then falls back to the date', () => {
    const now = new Date('2026-10-10T12:00:00Z')
    const ago = (seconds: number) => new Date(now.getTime() - seconds * 1000).toISOString()
    expect(timeAgo(ago(20), now)).toBe('just now')
    expect(timeAgo(ago(60), now)).toBe('1 minute ago')
    expect(timeAgo(ago(5 * 60), now)).toBe('5 minutes ago')
    expect(timeAgo(ago(3 * 3600), now)).toBe('3 hours ago')
    expect(timeAgo(ago(2 * 86400), now)).toBe('2 days ago')
    expect(timeAgo(ago(30 * 86400), now)).toMatch(/2026/)
    expect(timeAgo(new Date(now.getTime() + 5000).toISOString(), now)).toBe('just now') // clock skew never reads "in the future"
  })
})

// ---------- preferences ----------
describe('NotificationPreferences', () => {
  beforeEach(() => request.mockReset())

  it('offers every kind of notification the system can send', () => {
    const offered = notificationTypes.map((type) => type.code).sort()
    const sent = ['ANNOUNCEMENT', 'ASSESSMENT_GRADED', 'ASSIGNMENT_GRADED', 'ASSIGNMENT_PUBLISHED', 'CERTIFICATE_ISSUED', 'COURSE_COMPLETED', 'COURSE_INVITATION', 'COURSE_UPDATED',
      'DEADLINE_REMINDER', 'ENROLLMENT_CREATED', 'ENROLLMENT_PROMOTED', 'ENROLLMENT_WAITLISTED'].sort()   // the default templates on the server
    expect(offered).toEqual(sent)
  })

  it('treats a type as on until it has been switched off, per channel', () => {
    const prefs = [{ templateCode: 'COURSE_UPDATED', channel: 'Email' as const, enabled: false }]
    expect(isEnabled(prefs, 'COURSE_UPDATED', 'Email')).toBe(false)
    expect(isEnabled(prefs, 'COURSE_UPDATED', 'InApp')).toBe(true)
    expect(isEnabled(prefs, 'ANNOUNCEMENT', 'Email')).toBe(true)
  })

  it('saves a switch for the new course-update notice', async () => {
    request.mockImplementation((_path: string, options?: { method?: string }) => Promise.resolve(options?.method ? null : []))
    render(<NotificationPreferences />)
    await userEvent.click(await screen.findByRole('checkbox', { name: 'Course updates — email' }))
    await waitFor(() => expect(calls('notification-preferences', 'PUT')).toHaveLength(1))
    expect(JSON.parse(calls('notification-preferences', 'PUT')[0][1].body)).toEqual({ templateCode: 'COURSE_UPDATED', channel: 'Email', enabled: false })
  })
})

// ---------- inbox ----------
const note = (id: string, subject: string, over: Partial<InboxItem> = {}): InboxItem => ({ id, templateCode: 'ANNOUNCEMENT', subject, body: `${subject} body`, createdAtUtc: new Date().toISOString(), readAtUtc: null, ...over })

describe('NotificationInbox', () => {
  const changed = vi.fn()
  const onChanged = () => changed()
  beforeEach(() => { request.mockReset(); changed.mockClear(); window.addEventListener(NOTIFICATIONS_CHANGED, onChanged) })
  afterEach(() => window.removeEventListener(NOTIFICATIONS_CHANGED, onChanged))

  const list = [note('1', 'Exam moved'), note('2', 'Essay graded', { templateCode: 'ASSIGNMENT_GRADED' }), note('3', 'Welcome', { readAtUtc: new Date().toISOString() })]
  const serve = (items = list) => request.mockImplementation((path: string, options?: { method?: string }) => {
    if (String(path).endsWith('/read-all')) return Promise.resolve({ marked: items.filter((item) => !item.readAtUtc).length })
    return Promise.resolve(options?.method ? null : items)
  })

  it('lists every message with unread ones marked, and counts them', async () => {
    serve(); render(<NotificationInbox onNavigate={vi.fn()} />)
    expect(await screen.findByText('Exam moved')).toBeInTheDocument()
    expect(screen.getByText('Exam moved').closest('strong')).toHaveTextContent('(unread)')
    expect(screen.getByText('Welcome').closest('strong')).not.toHaveTextContent('(unread)')
    expect(screen.getByRole('button', { name: /^All/ })).toHaveTextContent('3')
    expect(screen.getByRole('button', { name: /^Unread/ })).toHaveTextContent('2')
  })

  it('filters to unread only', async () => {
    serve(); render(<NotificationInbox />)
    await screen.findByText('Welcome')
    await userEvent.click(screen.getByRole('button', { name: /^Unread/ }))
    expect(screen.queryByText('Welcome')).toBeNull()
    expect(screen.getByText('Exam moved')).toBeInTheDocument()
  })

  it('marks one as read and tells the bell', async () => {
    serve(); render(<NotificationInbox />)
    await userEvent.click(await screen.findByRole('button', { name: 'Mark as read: Exam moved' }))
    await waitFor(() => expect(calls('/notifications/1/read', 'POST')).toHaveLength(1))
    expect(screen.queryByRole('button', { name: 'Mark as read: Exam moved' })).toBeNull()
    expect(changed).toHaveBeenCalledTimes(1)
  })

  it('marks everything as read in one go, says how many, and tells the bell', async () => {
    serve(); render(<NotificationInbox />)
    await userEvent.click(await screen.findByRole('button', { name: 'Mark all as read' }))
    expect(await screen.findByText('2 notifications marked as read.')).toBeInTheDocument()
    expect(calls('/notifications/read-all', 'POST')).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Mark all as read' })).toBeDisabled()
    expect(screen.queryByRole('button', { name: /Mark as read:/ })).toBeNull()
    expect(changed).toHaveBeenCalledTimes(1)
  })

  it('opens the related screen and reads the message on the way', async () => {
    serve(); const onNavigate = vi.fn()
    render(<NotificationInbox onNavigate={onNavigate} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Open: Essay graded' }))
    expect(onNavigate).toHaveBeenCalledWith('assignments')
    await waitFor(() => expect(calls('/notifications/2/read', 'POST')).toHaveLength(1))
  })

  it('shows friendly empty states', async () => {
    serve([]); render(<NotificationInbox />)
    expect(await screen.findByText('No notifications yet.')).toBeInTheDocument()
  })

  it('says when it is showing only the newest', async () => {
    serve(Array.from({ length: INBOX_LIMIT }, (_, i) => note(String(i), `Message ${i}`)))
    render(<NotificationInbox />)
    expect(await screen.findByText(/Showing your latest 100 notifications/)).toBeInTheDocument()
  })
})

describe('NotificationsPage', () => {
  it('has the inbox and the preferences as its two tabs', async () => {
    request.mockResolvedValue([])
    render(<NotificationsPage />)
    expect(await screen.findByRole('tab', { name: 'Inbox' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('tab', { name: 'Preferences' }))
    expect(await screen.findByText('What you are notified about')).toBeInTheDocument()
  })
})

// ---------- my tasks ----------
const task = (id: string, title: string, over: object = {}) => ({ id, kind: 'assignment', title, courseTitle: null, startAtUtc: null, dueAtUtc: new Date(Date.now() + 86_400_000).toISOString(), status: 'Todo', target: 'assignments', count: null, ...over })

describe('MyTasksPage', () => {
  beforeEach(() => request.mockReset())
  const feed = (...items: object[]) => request.mockResolvedValue({ fromUtc: '', toUtc: '', items })

  it('only offers filters for kinds of work that exist', () => {
    expect(kindsPresent([task('a', 'A'), task('b', 'B', { kind: 'live-class' })] as never)).toEqual(['assignment', 'live-class'])
    expect(kindsPresent([])).toEqual([])
  })

  it('filters by type and keeps the counts honest', async () => {
    feed(task('a', 'Essay'), task('b', 'Quiz one', { kind: 'assessment', target: 'quizzes' }), task('c', 'Done essay', { status: 'Graded' }))
    render(<MyTasksPage onNavigate={vi.fn()} />)
    await screen.findByText('Essay')
    expect(screen.getByRole('tab', { name: 'To do (2)' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Assessment' }))
    expect(screen.queryByText('Essay')).toBeNull()
    expect(screen.getByText('Quiz one')).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'To do (1)' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Completed (0)' })).toBeInTheDocument()
  })

  it('hides the filter when there is only one kind of work', async () => {
    feed(task('a', 'Essay'))
    render(<MyTasksPage onNavigate={vi.fn()} />)
    await screen.findByText('Essay')
    expect(screen.queryByRole('group', { name: 'Filter by type' })).toBeNull()
  })

  it('flags how much is overdue and refreshes on request', async () => {
    feed(task('a', 'Late essay', { status: 'Overdue', dueAtUtc: new Date(Date.now() - 86_400_000).toISOString() }), task('b', 'Also late', { status: 'Overdue', dueAtUtc: new Date(Date.now() - 2 * 86_400_000).toISOString() }))
    render(<MyTasksPage onNavigate={vi.fn()} />)
    expect(await screen.findByText('2 overdue')).toBeInTheDocument()
    expect(screen.getByText('Overdue (2)')).toBeInTheDocument()
    request.mockClear(); feed(task('c', 'Fresh essay'))
    await userEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    expect(await screen.findByText('Fresh essay')).toBeInTheDocument()
    expect(screen.queryByText('2 overdue')).toBeNull()
    expect(request).toHaveBeenCalledTimes(1)
  })
})

describe('ended live classes', () => {
  beforeEach(() => request.mockReset())

  it('never appear as something to do, or in the completed list, on My tasks', async () => {
    request.mockResolvedValue({ fromUtc: '', toUtc: '', items: [
      task('m', 'Missed class', { kind: 'live-class', status: 'Ended', startAtUtc: new Date(Date.now() - 2 * 86_400_000).toISOString(), dueAtUtc: new Date(Date.now() - 2 * 86_400_000 + 3_600_000).toISOString(), target: 'live' }),
      task('n', 'Next class', { kind: 'live-class', status: 'Scheduled', startAtUtc: new Date(Date.now() + 86_400_000).toISOString(), dueAtUtc: new Date(Date.now() + 90_000_000).toISOString(), target: 'live' }),
    ] })
    render(<MyTasksPage onNavigate={vi.fn()} />)
    expect(await screen.findByText('Next class')).toBeInTheDocument()
    expect(screen.queryByText('Missed class')).toBeNull()
    expect(screen.getByRole('tab', { name: 'To do (1)' })).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'Completed (0)' })).toBeInTheDocument()
  })

  it('count as finished work everywhere else (so the calendar shows them muted)', async () => {
    const { isCompleted } = await import('@/lib/tasks')
    expect(isCompleted(task('m', 'x', { status: 'Ended' }) as never)).toBe(true)
    expect(isCompleted(task('m', 'x', { status: 'Scheduled' }) as never)).toBe(false)
  })
})
