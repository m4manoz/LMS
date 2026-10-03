import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import VideoPlayer from '@/components/VideoPlayer'
import { connectionProblem } from '@/components/LiveClassRoom'
import { qualityLabel, type VideoItem } from '@/lib/video'

const hls = vi.hoisted(() => {
  type Handler = (event: string, data: unknown) => void
  const instances: { handlers: Record<string, Handler>; currentLevel: number; loadSource: ReturnType<typeof vi.fn>; attachMedia: ReturnType<typeof vi.fn>; destroy: ReturnType<typeof vi.fn> }[] = []
  class FakeHls {
    static isSupported = () => true
    static Events = { MANIFEST_PARSED: 'manifestParsed', ERROR: 'error' }
    handlers: Record<string, Handler> = {}
    currentLevel = -1
    loadSource = vi.fn()
    attachMedia = vi.fn()
    destroy = vi.fn()
    constructor() { instances.push(this) }
    on(event: string, handler: Handler) { this.handlers[event] = handler }
  }
  return { instances, FakeHls }
})
vi.mock('hls.js', () => ({ default: hls.FakeHls }))

const request = vi.fn()
vi.mock('@/lib/api', async () => {
  const actual = await vi.importActual<typeof import('@/lib/api')>('@/lib/api')
  return { ...actual, apiRequest: (...args: unknown[]) => request(...args) }
})

const video: VideoItem = {
  id: 'v1', courseId: 'c1', courseTitle: 'Algebra', lessonId: null, title: 'Intro', description: null, type: 'Uploaded', status: 'Ready', statusMessage: null,
  contentType: 'video/mp4', sizeBytes: 1, durationSeconds: 60, externalUrl: null, createdBy: 'Tara', createdAtUtc: '2026-10-01T10:00:00Z', myProgress: null, hasStreaming: true,
}
const streamed = { kind: 'hls', url: '/hls/master.m3u8?token=t', fallbackUrl: '/stream?token=t', expiresAtUtc: null, embeddable: false }

describe('qualityLabel', () => {
  it('names a quality by its height, or by its number when the height is unknown', () => {
    expect(qualityLabel(720, 0)).toBe('720p')
    expect(qualityLabel(undefined, 1)).toBe('Quality 2')
    expect(qualityLabel(0, 0)).toBe('Quality 1')
  })
})

describe('the player with several qualities and captions', () => {
  beforeEach(() => { request.mockReset(); hls.instances.length = 0 })
  const serve = (link: object) => request.mockImplementation((path: string) => Promise.resolve(String(path).endsWith('/link') ? link : { lastPositionSeconds: 0, percent: 0, completed: false }))

  async function ready() {
    const { container } = render(<VideoPlayer video={video} />)
    await waitFor(() => expect(hls.instances).toHaveLength(1))
    return { container, player: hls.instances[0] }
  }
  const offer = (player: (typeof hls.instances)[0], heights: (number | undefined)[]) => act(() => player.handlers.manifestParsed('manifestParsed', { levels: heights.map((height) => ({ height })) }))

  it('feeds the master playlist to the player and offers no choice until there is one', async () => {
    serve(streamed)
    const { player } = await ready()
    expect(player.loadSource).toHaveBeenCalledWith('/hls/master.m3u8?token=t')
    expect(player.attachMedia).toHaveBeenCalled()
    expect(screen.queryByLabelText('Quality')).toBeNull()
    await offer(player, [360])                                              // a single quality: nothing to choose
    expect(screen.queryByLabelText('Quality')).toBeNull()
  })

  it('lists the qualities with Auto first, and a choice is passed to the player', async () => {
    serve(streamed)
    const { player } = await ready()
    await offer(player, [240, 360, 720])
    const choice = await screen.findByLabelText('Quality')
    expect(Array.from((choice as HTMLSelectElement).options).map((option) => option.textContent)).toEqual(['Auto', '240p', '360p', '720p'])
    expect(choice).toHaveValue('-1')
    await userEvent.selectOptions(choice, '360p')
    expect(player.currentLevel).toBe(1)
    await userEvent.selectOptions(choice, 'Auto')
    expect(player.currentLevel).toBe(-1)
  })

  it('goes back to Auto when the list of qualities changes', async () => {
    serve(streamed)
    const { player } = await ready()
    await offer(player, [360, 720])
    await userEvent.selectOptions(await screen.findByLabelText('Quality'), '720p')
    await offer(player, [360, 720, 1080])
    expect(screen.getByLabelText('Quality')).toHaveValue('-1')
  })

  it('shows the transcript as a captions track when there is one, and none otherwise', async () => {
    serve({ ...streamed, captionsUrl: '/captions.vtt?token=t', captionsLanguage: 'en' })
    const first = await ready()
    const track = first.container.querySelector('track')!
    expect(track).toHaveAttribute('kind', 'captions')
    expect(track).toHaveAttribute('src', '/captions.vtt?token=t')
    expect(track).toHaveAttribute('srclang', 'en')
    expect(track).toHaveAttribute('label', 'Transcript')
  })

  it('has no captions track without a transcript', async () => {
    serve(streamed)
    const { container } = await ready()
    expect(container.querySelector('track')).toBeNull()
  })

  it('stops offering qualities when streaming gives way to the original file', async () => {
    serve(streamed)
    const { player } = await ready()
    await offer(player, [360, 720])
    await screen.findByLabelText('Quality')
    act(() => player.handlers.error('error', { fatal: true }))
    await waitFor(() => expect(screen.queryByLabelText('Quality')).toBeNull())
    expect(player.destroy).toHaveBeenCalled()
  })
})

describe('connectionProblem', () => {
  it('explains the usual mistake of a secure address for a server on this machine', () => {
    expect(connectionProblem('wss://localhost:7880')).toContain('should change it to ws://localhost:7880')
    expect(connectionProblem('wss://127.0.0.1:7880')).toContain('ws://127.0.0.1:7880')
  })
  it('asks whether the local server is running, and keeps the plain message for real servers', () => {
    expect(connectionProblem('ws://localhost:7880')).toContain('livekit-dev.ps1')
    expect(connectionProblem('wss://school.livekit.cloud')).toBe('Could not connect to the class room. Check the connection and try again.')
    expect(connectionProblem('not an address')).toBe('Could not connect to the class room. Check the connection and try again.')
  })
})
