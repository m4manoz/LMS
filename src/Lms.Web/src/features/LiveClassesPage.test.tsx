import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import LiveClassesPage from './LiveClassesPage'

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions } }) }))

const live = { id: 's1', courseId: null, courseTitle: null, title: 'Algebra revision', description: 'Weekly', provider: 'local', joinUrl: '', hostUrl: '', startAtUtc: '2030-01-01T10:00:00Z', endAtUtc: '2030-01-01T11:00:00Z', status: 'Scheduled' }

describe('LiveClassesPage', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (path.endsWith('/live-classes/sessions') && !options?.method) return Promise.resolve([live])
      if (path === '/api/v1/tenant/courses') return Promise.resolve([{ id: 'c1', code: 'MATH-1', title: 'Algebra', status: 'Published' }, { id: 'c2', code: 'DRAFT-1', title: 'Unfinished', status: 'Draft' }])
      if (path.endsWith('/announcements') || path.endsWith('/chat') || path.endsWith('/polls') || path.endsWith('/attendance')) return Promise.resolve([])
      if (path.endsWith('/recording')) return Promise.resolve(null)
      return Promise.resolve(undefined)
    })
  })

  it('lists classes as rows and opens nothing by default', async () => {
    permissions = ['liveclass.manage']
    render(<LiveClassesPage />)
    const list = await screen.findByRole('list', { name: 'Live classes' })
    expect(within(list).getByText('Algebra revision')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('shows a different page for each classroom menu, each opening only its own part of a class', async () => {
    permissions = ['attendance.read', 'collaboration.read', 'liveclass.read']
    const cases = [
      ['recording', 'Class recordings', 'Open recording of Algebra revision', 'Open recording', /Give recording consent/],
      ['attendance', 'Class attendance', 'View attendance for Algebra revision', 'View attendance', /No attendance recorded yet/],
      ['chat', 'Class chat', 'Open chat of Algebra revision', 'Open chat', /No messages yet/],
    ] as const
    for (const [tab, heading, buttonName, buttonText, inside] of cases) {
      const { unmount } = render(<LiveClassesPage initialTab={tab} />)
      expect(await screen.findByRole('heading', { name: heading })).toBeInTheDocument()
      expect(screen.queryByRole('heading', { name: 'Live classes' })).toBeNull()
      expect(screen.queryByRole('button', { name: /Schedule class/ })).toBeNull()
      const button = await screen.findByRole('button', { name: buttonName })
      expect(button).toHaveTextContent(buttonText)
      await userEvent.click(button)
      const panel = await screen.findByRole('dialog', { name: 'Algebra revision' })
      expect(within(panel).queryByRole('tab')).toBeNull()                                   // no other tabs: this menu is about one thing
      expect(await within(panel).findByText(inside)).toBeInTheDocument()
      unmount()
    }
  })

  it('keeps the full set of tabs on the Live classes page', async () => {
    permissions = ['attendance.read']
    render(<LiveClassesPage />)
    expect(await screen.findByRole('heading', { name: 'Live classes' })).toBeInTheDocument()
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Algebra revision' }))
    const panel = await screen.findByRole('dialog', { name: 'Algebra revision' })
    expect(within(panel).getAllByRole('tab').map((tab) => tab.textContent)).toEqual(['Overview', 'Announcements', 'Polls', 'Chat', 'Attendance', 'Recording'])
  })

  it('opens the schedule form from the button, marks required fields and blocks an empty title', async () => {
    permissions = ['liveclass.manage']
    render(<LiveClassesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Schedule class/ }))
    const panel = await screen.findByRole('dialog', { name: 'Schedule a class' })
    expect(within(panel).getByText('Title')).toHaveClass('after:text-red-500')
    await userEvent.clear(within(panel).getByLabelText('Title'))
    await userEvent.click(within(panel).getByRole('button', { name: 'Schedule class' }))
    expect(await within(panel).findByRole('alert')).toHaveTextContent('Enter a title for the class.')
    expect(request).not.toHaveBeenCalledWith('/api/v1/tenant/live-classes/sessions', expect.objectContaining({ method: 'POST' }))
  })

  it('opens the schedule panel straight away for the schedule view and posts the same body', async () => {
    permissions = ['liveclass.manage']
    render(<LiveClassesPage initialView="schedule" />)
    const panel = await screen.findByRole('dialog', { name: 'Schedule a class' })
    await userEvent.click(within(panel).getByRole('button', { name: 'Schedule class' }))
    const call = request.mock.calls.find(([path, options]) => path === '/api/v1/tenant/live-classes/sessions' && options?.method === 'POST')
    expect(call).toBeTruthy()
    const body = JSON.parse(call![1].body)
    expect(Object.keys(body)).toEqual(['title', 'description', 'courseId', 'startAtUtc', 'endAtUtc', 'requireApproval'])
    expect(body.requireApproval).toBe(true)                       // learners wait to be let in unless the teacher turns it off
    expect(body.title).toBe('Weekly live class')
    expect(body.courseId).toBeNull()      // no course chosen: a class only staff can see
  })

  it('lets the class be attached to a published course, so the people enrolled in it can see it', async () => {
    permissions = ['liveclass.manage']
    render(<LiveClassesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Schedule class/ }))
    const panel = await screen.findByRole('dialog', { name: 'Schedule a class' })
    const course = await within(panel).findByLabelText('Course')
    expect(within(panel).getByRole('option', { name: 'MATH-1 · Algebra' })).toBeInTheDocument()
    expect(within(panel).queryByRole('option', { name: /Unfinished/ })).toBeNull()   // drafts have no learners to invite
    expect(within(panel).getByRole('option', { name: 'No course (staff only)' })).toBeInTheDocument()
    await userEvent.selectOptions(course, 'c1')
    await userEvent.click(within(panel).getByRole('button', { name: 'Schedule class' }))
    const call = request.mock.calls.find(([path, options]) => path === '/api/v1/tenant/live-classes/sessions' && options?.method === 'POST')
    expect(JSON.parse(call![1].body).courseId).toBe('c1')
  })

  it('starts each new class with no course chosen, even after a previous one', async () => {
    permissions = ['liveclass.manage']
    render(<LiveClassesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Schedule class/ }))
    await userEvent.selectOptions(await screen.findByLabelText('Course'), 'c1')
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    await userEvent.click(screen.getByRole('button', { name: /Schedule class/ }))
    expect(await screen.findByLabelText('Course')).toHaveValue('')
  })

  it('does not ask people who cannot schedule for the course list', async () => {
    permissions = ['liveclass.read']
    render(<LiveClassesPage />)
    await screen.findByRole('list', { name: 'Live classes' })
    expect(request).not.toHaveBeenCalledWith('/api/v1/tenant/courses')
  })

  describe('closing a class', () => {
    const open = async (status: string, perms: string[]) => {
      permissions = perms
      let current = status          // the server's view: closing really changes it
      request.mockImplementation((path: string, options?: { method?: string }) => {
        if (path.endsWith('/live-classes/sessions') && !options?.method) return Promise.resolve([{ ...live, status: current }])
        if (path.endsWith('/close')) { current = 'Completed'; return Promise.resolve({ ...live, status: 'Completed' }) }
        if (path.endsWith('/announcements') || path.endsWith('/chat') || path.endsWith('/polls') || path.endsWith('/attendance')) return Promise.resolve([])
        if (path.endsWith('/recording')) return Promise.resolve(null)
        return Promise.resolve(undefined)
      })
      render(<LiveClassesPage />)
      await userEvent.click(await screen.findByRole('button', { name: 'View details for Algebra revision' }))
      return await screen.findByRole('dialog', { name: 'Algebra revision' })
    }

    it('lets staff close a class after confirming, then shows it as ended with nothing to join', async () => {
      const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
      const panel = await open('Live', ['liveclass.manage'])
      await userEvent.click(within(panel).getByRole('button', { name: 'Close class' }))
      expect(confirm).toHaveBeenCalledWith(expect.stringContaining('Algebra revision'))
      expect(request.mock.calls.filter(([path, options]) => String(path).endsWith('/close') && options?.method === 'POST')).toHaveLength(1)
      expect(await within(panel).findByText('Class closed.')).toBeInTheDocument()
      expect(within(panel).getByText('Completed')).toBeInTheDocument()
      expect(within(panel).queryByRole('button', { name: 'Join class' })).toBeNull()
      expect(within(panel).queryByRole('button', { name: 'Close class' })).toBeNull()
      expect(within(panel).getByText(/can no longer be joined/)).toBeInTheDocument()
      confirm.mockRestore()
    })

    it('does nothing when the confirmation is declined', async () => {
      const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
      const panel = await open('Scheduled', ['liveclass.manage'])
      await userEvent.click(within(panel).getByRole('button', { name: 'Close class' }))
      expect(request.mock.calls.some(([path]) => String(path).endsWith('/close'))).toBe(false)
      expect(within(panel).getByRole('button', { name: 'Join class' })).toBeInTheDocument()
      confirm.mockRestore()
    })

    it('does not offer closing to people who only attend', async () => {
      const panel = await open('Live', ['liveclass.read'])
      expect(within(panel).getByRole('button', { name: 'Join class' })).toBeInTheDocument()
      expect(within(panel).queryByRole('button', { name: 'Close class' })).toBeNull()
    })

    it('offers no joining for a class that has already ended', async () => {
      const panel = await open('Completed', ['liveclass.manage'])
      expect(within(panel).queryByRole('button', { name: 'Join class' })).toBeNull()
      expect(within(panel).queryByRole('button', { name: 'Leave' })).toBeNull()
      expect(within(panel).queryByRole('button', { name: 'Close class' })).toBeNull()
    })

    it('shows the server message when closing fails', async () => {
      const { ApiError } = await import('@/lib/api')
      const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
      const panel = await open('Live', ['liveclass.manage'])
      request.mockImplementation((path: string, options?: { method?: string }) => String(path).endsWith('/close') && options?.method === 'POST'
        ? Promise.resolve().then(() => { throw new ApiError('You cannot close this class.', 403) }) : Promise.resolve([]))
      await userEvent.click(within(panel).getByRole('button', { name: 'Close class' }))
      expect(await within(panel).findByRole('alert')).toHaveTextContent('You cannot close this class.')
      expect(within(panel).getByRole('button', { name: 'Join class' })).toBeInTheDocument()   // still open
      confirm.mockRestore()
    })
  })

  describe('opening a class from its link', () => {
    beforeEach(() => { permissions = ['liveclass.read'] })

    it('opens that class straight away, on the requested tab', async () => {
      render(<LiveClassesPage initialSessionId="s1" initialTab="recording" />)
      const panel = await screen.findByRole('dialog', { name: 'Algebra revision' })
      expect(await within(panel).findByText(/Give recording consent/)).toBeInTheDocument()   // the Recordings page shows just the recording part
    })

    it('says so when the class is not available to the person, and opens nothing', async () => {
      render(<LiveClassesPage initialSessionId="someone-elses-class" />)
      expect(await screen.findByRole('alert')).toHaveTextContent('not available to you')
      expect(screen.queryByRole('dialog')).toBeNull()
    })

    it('opens the linked class only once, so closing it is not undone by a refresh', async () => {
      render(<LiveClassesPage initialSessionId="s1" />)
      await screen.findByRole('dialog', { name: 'Algebra revision' })
      await userEvent.click(screen.getByRole('button', { name: 'Close panel' }))
      await userEvent.click(screen.getByRole('button', { name: 'Refresh' }))
      await screen.findByRole('list', { name: 'Live classes' })
      expect(screen.queryByRole('dialog')).toBeNull()
    })
  })
})
