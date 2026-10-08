import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import LiveClassesPage from './LiveClassesPage'
import LiveClassSettingsPanel, { validateJitsiAddress, validateLiveKitAddress } from './LiveClassSettingsPanel'
import { ApiError } from '@/lib/api'

vi.mock('@/components/LiveClassRoom', () => ({ default: ({ join, onLeave }: { join: { room: string }; onLeave: () => void }) => <section aria-label="Live class room">Room {join.room}<button onClick={onLeave}>Leave class</button></section> }))

const request = vi.fn()
let permissions: string[] = []
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})
vi.mock('@/lib/auth', () => ({ useAuth: () => ({ session: { permissions, user: { id: 'me', displayName: 'Tara Teacher' } } }) }))

const calls = (fragment: string, method?: string) => request.mock.calls.filter((call) => String(call[0]).includes(fragment) && (method === undefined || call[1]?.method === method))

describe('validateJitsiAddress', () => {
  it('accepts blank (the public server) and clean https addresses', () => {
    for (const ok of ['', '   ', 'https://meet.jit.si', 'https://meet.school.org/jitsi']) expect(validateJitsiAddress(ok), ok).toBeNull()
  })
  it('refuses everything else with a reason', () => {
    expect(validateJitsiAddress('http://meet.example.org')).toMatch(/https/)
    expect(validateJitsiAddress('javascript:alert(1)')).toMatch(/https|link/)
    expect(validateJitsiAddress('not a link')).toMatch(/link/)
    expect(validateJitsiAddress('https://user:pw@meet.example.org')).toMatch(/user name/)
    expect(validateJitsiAddress('https://meet.example.org/?x=1')).toMatch(/query/)
  })
})

describe('validateLiveKitAddress', () => {
  it('accepts wss and https addresses', () => {
    for (const ok of ['wss://x.livekit.cloud', 'https://lk.school.org', 'ws://localhost:7880', 'ws://127.0.0.1:7880']) expect(validateLiveKitAddress(ok), ok).toBeNull()
  })
  it('refuses blank, plain ws/http and anything carrying sign-in details or a query', () => {
    expect(validateLiveKitAddress('  ')).toMatch(/Enter/)
    expect(validateLiveKitAddress('ws://x.example.org')).toMatch(/wss/)
    expect(validateLiveKitAddress('ws://192.168.1.5:7880')).toMatch(/wss/)          // only this machine, not the network
    expect(validateLiveKitAddress('wss://u:p@x.example.org')).toMatch(/user name/)
    expect(validateLiveKitAddress('wss://x.example.org/?a=1')).toMatch(/query/)
    expect(validateLiveKitAddress('nope')).toMatch(/link/)
  })
})

describe('LiveClassSettingsPanel LiveKit', () => {
  it('needs address, key and secret, sends them, and never shows a saved secret', async () => {
    request.mockReset()
    request.mockImplementation((_path: string, options?: { method?: string; body?: string }) =>
      Promise.resolve(options?.method === 'PUT' ? { provider: 'LiveKit', liveKitUrl: 'wss://x.livekit.cloud', liveKitApiKey: 'K1', liveKitSecretSet: true } : { provider: 'Local', jitsiBaseUrl: '' }))
    render(<LiveClassSettingsPanel />)
    await userEvent.click(await screen.findByRole('radio', { name: /LiveKit/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    expect(await screen.findByText(/Enter the LiveKit server address/)).toBeInTheDocument()
    expect(calls('integrations/live-classes', 'PUT')).toHaveLength(0)
    await userEvent.type(screen.getByLabelText(/Server address/), 'wss://x.livekit.cloud')
    await userEvent.type(screen.getByLabelText(/API key/), 'K1')
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    expect(await screen.findByText(/Enter the LiveKit API secret/)).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText(/API secret/), 's3cret-value')
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(calls('integrations/live-classes', 'PUT')).toHaveLength(1))
    expect(JSON.parse(calls('integrations/live-classes', 'PUT')[0][1].body)).toMatchObject({ provider: 'LiveKit', liveKitUrl: 'wss://x.livekit.cloud', liveKitApiKey: 'K1', liveKitApiSecret: 's3cret-value' })
    expect(await screen.findByText(/A secret is saved/)).toBeInTheDocument()
    expect((screen.getByLabelText(/API secret/) as HTMLInputElement).value).toBe('')
  })
})

describe('LiveClassSettingsPanel', () => {
  beforeEach(() => {
    request.mockReset()
    request.mockImplementation((_path: string, options?: { method?: string; body?: string }) =>
      Promise.resolve(options?.method === 'PUT' ? { ...JSON.parse(options.body!), jitsiBaseUrl: JSON.parse(options.body!).jitsiBaseUrl ?? 'https://meet.jit.si' } : { provider: 'Local', jitsiBaseUrl: 'https://meet.jit.si' }))
  })

  it('shows the four choices with what each means, and the current one selected', async () => {
    render(<LiveClassSettingsPanel />)
    const group = await screen.findByRole('radiogroup', { name: 'Live class provider' })
    expect(within(group).getAllByRole('radio')).toHaveLength(4)
    expect(within(group).getByRole('radio', { name: /Placeholder/ })).toBeChecked()
    expect(screen.getByText(/Zoom, Google Meet, Microsoft Teams/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Server address')).toBeNull()
  })

  it('asks for the server address only for Jitsi, and saves the choice', async () => {
    render(<LiveClassSettingsPanel />)
    await userEvent.click(await screen.findByRole('radio', { name: /Jitsi Meet/ }))
    await userEvent.clear(screen.getByLabelText('Server address'))   // it starts filled with the public server
    await userEvent.type(screen.getByLabelText('Server address'), 'https://meet.school.org')
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(calls('live-classes', 'PUT')).toHaveLength(1))
    expect(JSON.parse(calls('live-classes', 'PUT')[0][1].body)).toEqual({ provider: 'Jitsi', jitsiBaseUrl: 'https://meet.school.org' })
    expect(await screen.findByText(/Classes already scheduled keep the tool/)).toBeInTheDocument()
  })

  it('does not send an address that is not valid', async () => {
    render(<LiveClassSettingsPanel />)
    await userEvent.click(await screen.findByRole('radio', { name: /Jitsi Meet/ }))
    await userEvent.clear(screen.getByLabelText('Server address'))
    await userEvent.type(screen.getByLabelText('Server address'), 'http://insecure.example.org')
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('https')
    expect(calls('live-classes', 'PUT')).toHaveLength(0)
  })

  it('sends no address for the other choices', async () => {
    render(<LiveClassSettingsPanel />)
    await userEvent.click(await screen.findByRole('radio', { name: /Your own meeting link/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(calls('live-classes', 'PUT')).toHaveLength(1))
    expect(JSON.parse(calls('live-classes', 'PUT')[0][1].body)).toEqual({ provider: 'Manual', jitsiBaseUrl: null })
  })

  it('shows the server message when saving fails', async () => {
    request.mockImplementation((_p: string, options?: { method?: string }) => {
      if (options?.method === 'PUT') return Promise.resolve().then(() => { throw new ApiError('You cannot change this.', 403) })
      return Promise.resolve({ provider: 'Local', jitsiBaseUrl: 'https://meet.jit.si' })
    })
    render(<LiveClassSettingsPanel />)
    await userEvent.click(await screen.findByRole('button', { name: 'Save settings' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('You cannot change this.')
  })
})

// ---------- the class screen follows the setting ----------
const live = (provider: string) => ({ id: 's1', courseId: null, courseTitle: null, title: 'Algebra revision', description: 'Weekly', provider, joinUrl: '', hostUrl: '', startAtUtc: '2030-01-01T10:00:00Z', endAtUtc: '2030-01-01T11:00:00Z', status: 'Scheduled' })

function serve(provider: string, sessionProvider = provider.toLowerCase()) {
  request.mockImplementation((path: string, options?: { method?: string; body?: string }) => {
    if (path.endsWith('/live-classes/provider')) return Promise.resolve({ provider, requiresMeetingLink: provider === 'Manual', canRecord: provider === 'Local' })
    if (path.endsWith('/live-classes/sessions') && !options?.method) return Promise.resolve([live(sessionProvider)])
    if (path === '/api/v1/tenant/courses') return Promise.resolve([])
    if (path.endsWith('/recording') && !options?.method) return Promise.resolve(null)
    if (path.endsWith('/recording/link')) return Promise.resolve({ id: 'r', sessionId: 's1', provider: sessionProvider, providerRecordingId: 'external-link', recordingUrl: JSON.parse(options!.body!).url, status: 'Available', attemptCount: 0, maxAttempts: 3, lastError: null, requestedAtUtc: '', availableAtUtc: '', retainUntilUtc: '2031-01-01T00:00:00Z' })
    if (/announcements|chat|polls|attendance/.test(path)) return Promise.resolve([])
    return Promise.resolve(undefined)
  })
}

describe('scheduling follows the organization setting', () => {
  beforeEach(() => { request.mockReset(); permissions = ['liveclass.manage'] })

  async function openForm() {
    render(<LiveClassesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Schedule class/ }))
    return await screen.findByRole('dialog', { name: 'Schedule a class' })
  }

  it('asks for a meeting link, marked required, when links are pasted by hand', async () => {
    serve('Manual')
    const panel = await openForm()
    expect(await within(panel).findByLabelText('Meeting link')).toBeInTheDocument()
    expect(within(panel).getByText('Meeting link')).toHaveClass('after:text-red-500')
  })

  it('refuses a missing or non-https link and then sends the link with the class', async () => {
    serve('Manual')
    const panel = await openForm()
    const link = await within(panel).findByLabelText('Meeting link')
    await userEvent.click(within(panel).getByRole('button', { name: 'Schedule class' }))
    expect(await within(panel).findByRole('alert')).toHaveTextContent('Paste the meeting link')
    await userEvent.type(link, 'http://insecure.example.org/room')
    await userEvent.click(within(panel).getByRole('button', { name: 'Schedule class' }))
    expect(within(panel).getByRole('alert')).toHaveTextContent('Paste the meeting link')
    expect(calls('/live-classes/sessions', 'POST')).toHaveLength(0)

    await userEvent.clear(link)
    await userEvent.type(link, 'https://us02web.zoom.us/j/123')
    await userEvent.click(within(panel).getByRole('button', { name: 'Schedule class' }))
    await waitFor(() => expect(calls('/live-classes/sessions', 'POST')).toHaveLength(1))
    expect(JSON.parse(calls('/live-classes/sessions', 'POST')[0][1].body).meetingUrl).toBe('https://us02web.zoom.us/j/123')
  })

  it('does not ask for a link with Jitsi or the placeholder, and sends none', async () => {
    for (const provider of ['Jitsi', 'Local']) {
      request.mockReset(); serve(provider)
      const { unmount } = render(<LiveClassesPage />)
      await userEvent.click(await screen.findByRole('button', { name: /Schedule class/ }))
      const panel = await screen.findByRole('dialog', { name: 'Schedule a class' })
      expect(within(panel).queryByLabelText('Meeting link')).toBeNull()
      await userEvent.click(within(panel).getByRole('button', { name: 'Schedule class' }))
      await waitFor(() => expect(calls('/live-classes/sessions', 'POST')).toHaveLength(1))
      expect('meetingUrl' in JSON.parse(calls('/live-classes/sessions', 'POST')[0][1].body)).toBe(false)
      unmount()
    }
  })
})

describe('scheduling a class that records itself', () => {
  beforeEach(() => { request.mockReset(); permissions = ['liveclass.manage'] })

  async function openForm(provider: string) {
    serve(provider)
    const base = request.getMockImplementation()!
    request.mockImplementation((path: string, options?: { method?: string; body?: string }) =>
      path === '/api/v1/tenant/courses' ? Promise.resolve([{ id: 'c1', code: 'ALG', title: 'Algebra', status: 'Published' }]) : base(path, options))
    render(<LiveClassesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Schedule class/ }))
    return await screen.findByRole('dialog', { name: 'Schedule a class' })
  }

  it('is offered for LiveKit classes only, and needs a course first', async () => {
    const panel = await openForm('LiveKit')
    const box = await within(panel).findByRole('checkbox', { name: /Record this class automatically/ })
    expect(box).toBeDisabled()
    expect(within(panel).getByText(/Choose a course first/)).toBeInTheDocument()
    await userEvent.selectOptions(within(panel).getByLabelText('Course'), 'c1')
    expect(box).toBeEnabled()
    expect(within(panel).getByText(/Choosing this is your agreement/)).toBeInTheDocument()
  })

  it('sends the choice with the class', async () => {
    const panel = await openForm('LiveKit')
    await userEvent.selectOptions(await within(panel).findByLabelText('Course'), 'c1')
    await userEvent.click(within(panel).getByRole('checkbox', { name: /Record this class automatically/ }))
    await userEvent.click(within(panel).getByRole('button', { name: 'Schedule class' }))
    await waitFor(() => expect(calls('/live-classes/sessions', 'POST')).toHaveLength(1))
    expect(JSON.parse(calls('/live-classes/sessions', 'POST')[0][1].body)).toMatchObject({ courseId: 'c1', autoRecord: true })
  })

  it('sends nothing about it when it is left off', async () => {
    const panel = await openForm('LiveKit')
    await userEvent.click(await within(panel).findByRole('button', { name: 'Schedule class' }))
    await waitFor(() => expect(calls('/live-classes/sessions', 'POST')).toHaveLength(1))
    expect('autoRecord' in JSON.parse(calls('/live-classes/sessions', 'POST')[0][1].body)).toBe(false)
  })

  it('is not offered for classes held in other tools', async () => {
    const panel = await openForm('Jitsi')
    await within(panel).findByLabelText('Course')
    expect(within(panel).queryByRole('checkbox', { name: /Record this class automatically/ })).toBeNull()
  })
})

describe('recordings follow where the class was held', () => {
  beforeEach(() => { request.mockReset(); permissions = ['liveclass.manage'] })

  async function openRecording(sessionProvider: string, perms = ['liveclass.manage']) {
    permissions = perms
    serve(sessionProvider === 'local' ? 'Local' : sessionProvider === 'jitsi' ? 'Jitsi' : 'Manual', sessionProvider)
    render(<LiveClassesPage initialTab="recording" />)
    await userEvent.click(await screen.findByRole('button', { name: 'Open recording of Algebra revision' }))
    return await screen.findByRole('dialog', { name: 'Algebra revision' })
  }

  it('built-in classes are recorded here, with no link form', async () => {
    const panel = await openRecording('local')
    expect(within(panel).getByRole('button', { name: 'Request recording' })).toBeInTheDocument()
    expect(within(panel).queryByLabelText('Recording link')).toBeNull()
  })

  it('classes held elsewhere are recorded there, and the recording is attached by link', async () => {
    const panel = await openRecording('jitsi')
    expect(within(panel).queryByRole('button', { name: 'Request recording' })).toBeNull()
    expect(within(panel).getByText(/recording is made there/)).toBeInTheDocument()
    await userEvent.type(within(panel).getByLabelText('Recording link'), 'https://zoom.example.org/rec/1')
    await userEvent.click(within(panel).getByRole('button', { name: 'Attach recording link' }))
    await waitFor(() => expect(calls('/recording/link', 'POST')).toHaveLength(1))
    expect(JSON.parse(calls('/recording/link', 'POST')[0][1].body)).toEqual({ url: 'https://zoom.example.org/rec/1' })
    expect(await within(panel).findByRole('link', { name: 'Open recording' })).toHaveAttribute('href', 'https://zoom.example.org/rec/1')
  })

  it('refuses a link that is not https before sending it', async () => {
    const panel = await openRecording('manual')
    await userEvent.type(within(panel).getByLabelText('Recording link'), 'http://zoom.example.org/rec/1')
    await userEvent.click(within(panel).getByRole('button', { name: 'Attach recording link' }))
    expect(await within(panel).findByRole('alert')).toHaveTextContent('https')
    expect(calls('/recording/link', 'POST')).toHaveLength(0)
  })

  it('only staff can attach a link; attendees see the explanation', async () => {
    const panel = await openRecording('manual', ['liveclass.read'])
    expect(within(panel).getByText(/recording is made there/)).toBeInTheDocument()
    expect(within(panel).queryByLabelText('Recording link')).toBeNull()
  })
})

describe('joining a LiveKit class', () => {
  beforeEach(() => { request.mockReset(); permissions = ['liveclass.manage'] })

  it('opens the room inside the page instead of a new tab, and leaving tells the server', async () => {
    const open = vi.spyOn(window, 'open').mockImplementation(() => null)
    serve('LiveKit')
    const inner = request.getMockImplementation()!
    request.mockImplementation((path: string, options?: { method?: string }) =>
      path.endsWith('/join') ? Promise.resolve({ meetingUrl: 'http://app/?liveSession=s1', liveKit: { url: 'wss://x', token: 't', room: 'lms-abc', canPublish: true, isHost: true } }) : inner(path, options))
    render(<LiveClassesPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Algebra revision' }))
    const panel = await screen.findByRole('dialog', { name: 'Algebra revision' })
    expect(within(panel).getByText(/Held in the class room inside this app/)).toBeInTheDocument()
    await userEvent.click(within(panel).getByRole('button', { name: 'Join class' }))
    expect(await within(panel).findByRole('region', { name: 'Live class room' })).toHaveTextContent('lms-abc')
    expect(open).not.toHaveBeenCalled()
    expect(within(panel).queryByRole('button', { name: 'Join class' })).toBeNull()   // already in the room
    await userEvent.click(within(panel).getByRole('button', { name: 'Leave class' }))
    await waitFor(() => expect(calls('/leave', 'POST')).toHaveLength(1))
    expect(within(panel).queryByRole('region', { name: 'Live class room' })).toBeNull()
    open.mockRestore()
  })
})

describe('recording a LiveKit class', () => {
  const recording = (over: Record<string, unknown> = {}) => ({ id: 'r1', sessionId: 's1', provider: 'livekit', providerRecordingId: 'EG_1', recordingUrl: null, status: 'Recording', attemptCount: 0, maxAttempts: 0, lastError: null, requestedAtUtc: '2030-01-01T10:00:00Z', availableAtUtc: null, retainUntilUtc: null, videoId: null, ...over })

  async function open(current: Record<string, unknown> | null, perms = ['liveclass.manage'], tracks: unknown[] = []) {
    request.mockReset(); permissions = perms
    serve('LiveKit')
    const base = request.getMockImplementation()!
    let state = current
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (path.endsWith('/recording/start') && options?.method === 'POST') { state = recording(); return Promise.resolve(state) }
      if (path.endsWith('/recording/stop') && options?.method === 'POST') { state = recording({ status: 'Processing' }); return Promise.resolve(state) }
      if (path.endsWith('/recording') && !options?.method) return Promise.resolve(state)
      if (path.endsWith('/recording/tracks')) return Promise.resolve(tracks)
      return base(path, options)
    })
    render(<LiveClassesPage initialTab="recording" />)
    await userEvent.click(await screen.findByRole('button', { name: 'Open recording of Algebra revision' }))
    return await screen.findByRole('dialog', { name: 'Algebra revision' })
  }

  it('starts and stops the recording, and says what is happening at each step', async () => {
    const panel = await open(null)
    expect(within(panel).getByText(/saves everyone's video and sound as one video/)).toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: 'Request recording' })).toBeNull()                       // the built-in flow is not offered
    await userEvent.click(within(panel).getByRole('button', { name: 'Start recording' }))
    await waitFor(() => expect(calls('/recording/start', 'POST')).toHaveLength(1))
    expect(await within(panel).findByText(/Recording now\./)).toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: 'Start recording' })).toBeNull()
    await userEvent.click(within(panel).getByRole('button', { name: 'Stop recording' }))
    await waitFor(() => expect(calls('/recording/stop', 'POST')).toHaveLength(1))
    expect(await within(panel).findByText(/being saved to the video library/)).toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: 'Stop recording' })).toBeNull()
    expect(within(panel).queryByLabelText('Recording link')).toBeNull()                                         // nothing to attach while it is being saved
  })

  it('says where a finished recording went and offers nothing more to do', async () => {
    const panel = await open(recording({ status: 'Available', videoId: 'v1', availableAtUtc: '2030-01-01T11:00:00Z' }))
    expect(await within(panel).findByText(/Saved to the video library as a class recording/)).toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: /Start recording/ })).toBeNull()
    expect(within(panel).queryByLabelText('Recording link')).toBeNull()
  })

  it('lists each person recorded on their own, with a way to open the finished ones', async () => {
    const panel = await open(recording({ status: 'Processing' }), ['liveclass.manage'], [
      { id: 't1', userId: 'u1', displayName: 'Ada', status: 'Available', durationSeconds: 62, sizeBytes: 100, lastError: null, downloadUrl: '/api/v1/tenant/courses/c1/assets/a1' },
      { id: 't2', userId: 'u2', displayName: 'Ben', status: 'Failed', durationSeconds: null, sizeBytes: 0, lastError: 'out of disk space', downloadUrl: null }])
    const list = await within(panel).findByRole('region', { name: 'Recordings of each person' })
    expect(within(list).getByText('Ada')).toBeInTheDocument()
    expect(within(list).getByText('1:02')).toBeInTheDocument()
    expect(within(list).getByRole('link', { name: 'Open the recording of Ada' })).toHaveAttribute('href', '/api/v1/tenant/courses/c1/assets/a1')
    expect(within(list).getByText('out of disk space')).toBeInTheDocument()
    expect(within(list).queryByRole('link', { name: 'Open the recording of Ben' })).toBeNull()
  })

  it('shows why a recording failed, offers to start again, and still allows attaching a link', async () => {
    const panel = await open(recording({ status: 'Failed', lastError: 'LiveKit could not finish the recording: out of disk space' }))
    expect(await within(panel).findByText(/out of disk space/)).toBeInTheDocument()
    expect(within(panel).getByRole('button', { name: 'Start recording again' })).toBeInTheDocument()
    expect(within(panel).getByLabelText('Recording link')).toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: 'Retry' })).toBeNull()                                   // retry is for the built-in flow
  })

  it('shows the server message when recording cannot start', async () => {
    const panel = await open(null)
    const inner = request.getMockImplementation()!
    request.mockImplementation((path: string, options?: { method?: string }) => path.endsWith('/recording/start')
      ? Promise.resolve().then(() => { throw new ApiError('Nobody is in the class room yet. Join the class, then start recording.', 409) })
      : inner(path, options))
    await userEvent.click(within(panel).getByRole('button', { name: 'Start recording' }))
    expect(await screen.findByText(/Nobody is in the class room yet/)).toBeInTheDocument()
  })

  it('offers neither button to people who only attend', async () => {
    const panel = await open(recording(), ['liveclass.read'])
    expect(await within(panel).findByText(/Recording now\./)).toBeInTheDocument()
    expect(within(panel).queryByRole('button', { name: /^(Start|Stop) recording/ })).toBeNull()
  })

  it('checks back while a recording is being saved and shows when it is done', async () => {
    const panel = await open(recording({ status: 'Processing' }))
    expect(await within(panel).findByText(/being saved/)).toBeInTheDocument()
    const inner = request.getMockImplementation()!
    request.mockImplementation((path: string, options?: { method?: string }) => path.endsWith('/recording') && !options?.method ? Promise.resolve(recording({ status: 'Available', videoId: 'v9' })) : inner(path, options))
    expect(await within(panel).findByText(/Saved to the video library/, undefined, { timeout: 8000 })).toBeInTheDocument()
  }, 15000)
})

describe('joining a Jitsi class', () => {
  beforeEach(() => { request.mockReset(); permissions = ['liveclass.manage'] })

  async function joinClass(provider: string, meetingUrl: string) {
    const open = vi.spyOn(window, 'open').mockImplementation(() => null)
    serve(provider === 'jitsi' ? 'Jitsi' : 'Manual', provider)
    const inner = request.getMockImplementation()!
    request.mockImplementation((path: string, options?: { method?: string }) => path.endsWith('/join') ? Promise.resolve({ meetingUrl, liveKit: null }) : inner(path, options))
    render(<LiveClassesPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Algebra revision' }))
    const panel = await screen.findByRole('dialog', { name: 'Algebra revision' })
    await userEvent.click(within(panel).getByRole('button', { name: 'Join class' }))
    return { panel, open }
  }

  it('shows the meeting inside the page, with a way to open it in its own tab and to leave', async () => {
    const { panel, open } = await joinClass('jitsi', 'https://meet.jit.si/lms-abc')
    const frame = await within(panel).findByTitle('Algebra revision (Jitsi)')
    expect(frame).toHaveAttribute('src', 'https://meet.jit.si/lms-abc')
    expect(frame.getAttribute('allow')).toContain('camera')
    expect(frame.getAttribute('allow')).toContain('display-capture')
    expect(open).not.toHaveBeenCalled()
    expect(within(panel).getByRole('link', { name: 'Open in a new tab' })).toHaveAttribute('href', 'https://meet.jit.si/lms-abc')
    expect(within(panel).queryByRole('button', { name: 'Join class' })).toBeNull()
    await userEvent.click(within(panel).getByRole('button', { name: 'Leave class' }))
    await waitFor(() => expect(calls('/leave', 'POST')).toHaveLength(1))
    expect(within(panel).queryByTitle('Algebra revision (Jitsi)')).toBeNull()
    expect(within(panel).getByRole('button', { name: 'Join class' })).toBeInTheDocument()
    open.mockRestore()
  })

  it('still sends classes held in other tools (a pasted link) to their own tab', async () => {
    const { panel, open } = await joinClass('manual', 'https://zoom.example.org/j/1')
    await waitFor(() => expect(open).toHaveBeenCalledWith('https://zoom.example.org/j/1', '_blank', 'noopener,noreferrer'))
    expect(within(panel).queryByTitle(/Jitsi/)).toBeNull()
    open.mockRestore()
  })
})

describe('the class screen while in a class', () => {
  beforeEach(() => { request.mockReset(); permissions = ['liveclass.manage'] })

  it('takes the whole width with the video beside the tabs, and goes back to the normal panel afterwards', async () => {
    vi.spyOn(window, 'open').mockImplementation(() => null)
    serve('Jitsi', 'jitsi')
    const inner = request.getMockImplementation()!
    request.mockImplementation((path: string, options?: { method?: string }) => path.endsWith('/join') ? Promise.resolve({ meetingUrl: 'https://meet.jit.si/lms-abc', liveKit: null }) : inner(path, options))
    render(<LiveClassesPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Algebra revision' }))
    const panel = await screen.findByRole('dialog', { name: 'Algebra revision' })
    expect(panel.className).toContain('max-w-4xl')                                          // before joining: the usual side panel
    expect(screen.getByTestId('class-layout').className).not.toContain('grid')
    await userEvent.click(within(panel).getByRole('button', { name: 'Join class' }))
    await within(panel).findByTitle('Algebra revision (Jitsi)')
    expect(panel.className).toContain('max-w-none')                                          // in the class: the whole screen
    expect(screen.getByTestId('class-layout').className).toContain('lg:grid-cols-[minmax(0,1fr)_24rem]')
    expect(within(panel).getByRole('tab', { name: 'Chat' })).toBeInTheDocument()            // the tabs are still there, beside the video
    await userEvent.click(within(panel).getByRole('button', { name: 'Leave class' }))
    await waitFor(() => expect(panel.className).toContain('max-w-4xl'))
  })
})

describe('raising a hand in class', () => {
  let hands: { id: string; userId: string; userName: string; status: string; raisedAtUtc: string }[]
  beforeEach(() => { request.mockReset(); hands = [] })

  async function openChat(perms: string[]) {
    permissions = perms
    serve('Local', 'local')
    const inner = request.getMockImplementation()!
    request.mockImplementation((path: string, options?: { method?: string; body?: string }) => {
      if (path.endsWith('/hand-raises') && !options?.method) return Promise.resolve(hands)
      if (path.endsWith('/hand-raise') && options?.method === 'POST') {
        const raised = JSON.parse(options.body!).raised
        hands = raised ? [...hands, { id: 'h1', userId: 'me', userName: 'Tara Teacher', status: 'Raised', raisedAtUtc: '2030-01-01T10:00:00Z' }] : hands.filter((item) => item.userId !== 'me')
        return Promise.resolve({})
      }
      if (path.includes('/hand-raises/') && path.endsWith('/lower') && options?.method === 'POST') { hands = hands.filter((item) => !path.includes(item.userId)); return Promise.resolve(null) }
      return inner(path, options)
    })
    render(<LiveClassesPage initialTab="chat" />)
    await userEvent.click(await screen.findByRole('button', { name: 'Open chat of Algebra revision' }))
    return await screen.findByRole('dialog', { name: 'Algebra revision' })
  }

  it('shows your hand as raised, and lowers it again', async () => {
    const panel = await openChat(['collaboration.manage', 'liveclass.read'])
    const button = await within(panel).findByRole('button', { name: 'Raise hand' })
    expect(button).toHaveAttribute('aria-pressed', 'false')
    await userEvent.click(button)
    await waitFor(() => expect(JSON.parse(calls('/hand-raise', 'POST')[0][1].body)).toEqual({ raised: true }))
    const lower = await within(panel).findByRole('button', { name: 'Lower hand' })
    expect(lower).toHaveAttribute('aria-pressed', 'true')
    expect(within(within(panel).getByRole('region', { name: 'Raised hands' })).getByText('You')).toBeInTheDocument()
    await userEvent.click(lower)
    await waitFor(() => expect(JSON.parse(calls('/hand-raise', 'POST')[1][1].body)).toEqual({ raised: false }))
    expect(await within(panel).findByRole('button', { name: 'Raise hand' })).toBeInTheDocument()
    expect(within(panel).queryByRole('region', { name: 'Raised hands' })).toBeNull()
  })

  it('lets staff see other hands and put them down, while classmates only see them', async () => {
    hands = [{ id: 'h9', userId: 'u9', userName: 'Ada Learner', status: 'Raised', raisedAtUtc: '2030-01-01T10:00:00Z' }]
    const staff = await openChat(['collaboration.manage', 'liveclass.manage'])
    const list = await within(staff).findByRole('region', { name: 'Raised hands' })
    expect(within(list).getByText('Ada Learner')).toBeInTheDocument()
    await userEvent.click(within(list).getByRole('button', { name: 'Lower the hand of Ada Learner' }))
    await waitFor(() => expect(calls('/hand-raises/u9/lower', 'POST')).toHaveLength(1))
    await waitFor(() => expect(within(staff).queryByRole('region', { name: 'Raised hands' })).toBeNull())
  })

  it('shows a classmate the raised hands but no way to lower someone elses', async () => {
    hands = [{ id: 'h9', userId: 'u9', userName: 'Ada Learner', status: 'Raised', raisedAtUtc: '2030-01-01T10:00:00Z' }]
    const learner = await openChat(['collaboration.manage', 'liveclass.read'])
    const list = await within(learner).findByRole('region', { name: 'Raised hands' })
    expect(within(list).getByText('Ada Learner')).toBeInTheDocument()
    expect(within(list).queryByRole('button')).toBeNull()
  })
})

describe('the waiting room', () => {
  beforeEach(() => { request.mockReset() })

  const session = (over: Record<string, unknown> = {}) => ({ ...live('local'), requireApproval: true, ...over })
  const waitingList = (...names: string[]) => names.map((name, index) => ({ userId: `u${index}`, userName: name, status: 'Waiting', requestedAtUtc: '2030-01-01T10:00:00Z' }))

  function serveClass(perms: string[], handlers: (path: string, options?: { method?: string }) => unknown | undefined, current = session()) {
    permissions = perms
    serve('Local', 'local')
    const inner = request.getMockImplementation()!
    request.mockImplementation((path: string, options?: { method?: string }) => {
      if (path.endsWith('/live-classes/sessions') && !options?.method) return Promise.resolve([current])
      const answer = handlers(path, options)
      return answer === undefined ? inner(path, options) : Promise.resolve(answer)
    })
  }

  async function openClass() {
    render(<LiveClassesPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'View details for Algebra revision' }))
    return await screen.findByRole('dialog', { name: 'Algebra revision' })
  }

  it('shows a learner waiting after Join class, then lets them in by themselves once admitted', async () => {
    let status = 'Waiting'
    const open = vi.spyOn(window, 'open').mockImplementation(() => null)
    serveClass(['liveclass.read'], (path, options) => {
      if (path.endsWith('/join') && options?.method === 'POST') return status === 'Waiting' ? { status: 'Waiting', message: 'Waiting for the host to let you in.' } : { meetingUrl: 'https://meet.example.org/x', liveKit: null }
      if (path.endsWith('/join-status')) return { status, closed: false }
      return undefined
    })
    const panel = await openClass()
    expect(within(panel).getByText(/the host lets you in after you press Join class/)).toBeInTheDocument()
    await userEvent.click(within(panel).getByRole('button', { name: 'Join class' }))
    const card = await within(panel).findByRole('status', { name: 'Waiting for the host' })
    expect(card).toHaveTextContent('Waiting for the host to let you in')
    expect(within(panel).queryByRole('button', { name: 'Join class' })).toBeNull()
    status = 'Admitted'
    await vi.waitFor(() => expect(calls('/join', 'POST')).toHaveLength(2), { timeout: 6000 })
    await vi.waitFor(() => expect(within(panel).queryByRole('status', { name: 'Waiting for the host' })).toBeNull())
    open.mockRestore()
  }, 15000)

  it('lets a waiting learner cancel, and tells a declined learner', async () => {
    let status = 'Waiting'
    serveClass(['liveclass.read'], (path, options) => {
      if (path.endsWith('/join') && options?.method === 'POST') return { status: 'Waiting' }
      if (path.endsWith('/join-status')) return { status, closed: false }
      return undefined
    })
    const panel = await openClass()
    await userEvent.click(within(panel).getByRole('button', { name: 'Join class' }))
    await userEvent.click(await within(panel).findByRole('button', { name: 'Cancel' }))
    expect(within(panel).queryByRole('status', { name: 'Waiting for the host' })).toBeNull()
    expect(within(panel).getByRole('button', { name: 'Join class' })).toBeInTheDocument()

    await userEvent.click(within(panel).getByRole('button', { name: 'Join class' }))
    await within(panel).findByRole('status', { name: 'Waiting for the host' })
    status = 'Declined'
    expect(await within(panel).findByText('The host did not let you into this class.', undefined, { timeout: 6000 })).toBeInTheDocument()
    expect(within(panel).queryByRole('status', { name: 'Waiting for the host' })).toBeNull()
  }, 20000)

  it('shows the host who is waiting, and admits or declines each', async () => {
    let list = waitingList('Ada Learner', 'Ben Learner')
    serveClass(['liveclass.manage'], (path, options) => {
      if (path.endsWith('/join-requests') && !options?.method) return list
      if (path.endsWith('/u0/admit')) { list = list.filter((item) => item.userId !== 'u0'); return null }
      if (path.endsWith('/u1/decline')) { list = list.filter((item) => item.userId !== 'u1'); return null }
      return undefined
    })
    const panel = await openClass()
    const box = await within(panel).findByRole('region', { name: 'Waiting to join' })
    expect(within(box).getByText('Waiting to join (2)')).toBeInTheDocument()
    expect(within(panel).getByText(/you let learners in/)).toBeInTheDocument()
    await userEvent.click(within(box).getByRole('button', { name: 'Let Ada Learner in' }))
    await vi.waitFor(() => expect(calls('/u0/admit', 'POST')).toHaveLength(1))
    expect(await within(panel).findByText('Waiting to join (1)')).toBeInTheDocument()
    await userEvent.click(within(panel).getByRole('button', { name: 'Decline Ben Learner' }))
    await vi.waitFor(() => expect(calls('/u1/decline', 'POST')).toHaveLength(1))
    await vi.waitFor(() => expect(within(panel).queryByRole('region', { name: 'Waiting to join' })).toBeNull())
  })

  it('admits everyone waiting with one button', async () => {
    let list = waitingList('Ada Learner', 'Ben Learner')
    serveClass(['liveclass.manage'], (path, options) => {
      if (path.endsWith('/join-requests') && !options?.method) return list
      if (path.endsWith('/admit-all')) { list = []; return { admitted: 2 } }
      return undefined
    })
    const panel = await openClass()
    await userEvent.click(await within(panel).findByRole('button', { name: 'Admit all' }))
    await vi.waitFor(() => expect(calls('/join-requests/admit-all', 'POST')).toHaveLength(1))
    await vi.waitFor(() => expect(within(panel).queryByRole('region', { name: 'Waiting to join' })).toBeNull())
  })

  it('does not look for waiting people in a class without a waiting room', async () => {
    serveClass(['liveclass.manage'], () => undefined, session({ requireApproval: false }))
    const panel = await openClass()
    expect(within(panel).queryByRole('region', { name: 'Waiting to join' })).toBeNull()
    expect(calls('/join-requests')).toHaveLength(0)
  })

  it('offers the waiting room when scheduling, switched on', async () => {
    permissions = ['liveclass.manage']
    serve('Local', 'local')
    render(<LiveClassesPage />)
    await userEvent.click(await screen.findByRole('button', { name: /Schedule class/ }))
    const form = await screen.findByRole('dialog', { name: 'Schedule a class' })
    const box = within(form).getByLabelText(/Learners wait to be let in/)
    expect(box).toBeChecked()
    await userEvent.click(box)
    expect(box).not.toBeChecked()
  })
})
