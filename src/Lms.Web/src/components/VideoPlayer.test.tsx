import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { VideoItem } from '@/lib/video'
import VideoPlayer from './VideoPlayer'

const request = vi.fn()
vi.mock('@/lib/api', async () => ({ ...(await vi.importActual<typeof import('@/lib/api')>('@/lib/api')), apiRequest: (...args: unknown[]) => request(...args) }))

const video = { id: 'v1', courseId: 'c1', courseTitle: 'Algebra', lessonId: null, title: 'Limits', description: null, type: 'Uploaded', status: 'Ready', statusMessage: null, contentType: 'video/mp4', sizeBytes: 1, durationSeconds: 100, externalUrl: null, createdBy: 'Tara', createdAtUtc: '2030-01-01T00:00:00Z', myProgress: null } as VideoItem

async function open() {
  request.mockImplementation((path: string) => Promise.resolve(path.endsWith('/link') ? { kind: 'stream', url: '/stream', expiresAtUtc: null, embeddable: false } : {}))
  const { container } = render(<VideoPlayer video={video} />)
  const player = await screen.findByRole('group', { name: 'Video player' })
  const element = container.querySelector('video') as HTMLVideoElement
  return { player, element }
}

describe('the video player controls', () => {
  beforeEach(() => { request.mockReset(); window.localStorage.clear() })
  afterEach(() => { vi.restoreAllMocks(); Object.defineProperty(document, 'pictureInPictureEnabled', { value: undefined, configurable: true }) })

  it('plays at the chosen speed and remembers it for the next video', async () => {
    const { element } = await open()
    expect(screen.getByLabelText('Playback speed')).toHaveValue('1')
    await userEvent.selectOptions(screen.getByLabelText('Playback speed'), '1.5')
    expect(element.playbackRate).toBe(1.5)
    expect(window.localStorage.getItem('lms.video.speed')).toBe('1.5')

    document.body.innerHTML = ''
    const next = await open()
    expect(screen.getByLabelText('Playback speed')).toHaveValue('1.5')
    await waitFor(() => expect(next.element.playbackRate).toBe(1.5))
  })

  it('ignores a remembered speed that is not one of the choices', async () => {
    window.localStorage.setItem('lms.video.speed', '9')
    await open()
    expect(screen.getByLabelText('Playback speed')).toHaveValue('1')
  })

  it('skips, changes speed, mutes and jumps with the keyboard', async () => {
    const { player, element } = await open()
    player.focus()
    await userEvent.keyboard('l')
    expect(element.currentTime).toBe(10)
    await userEvent.keyboard('{ArrowLeft}')
    expect(element.currentTime).toBe(5)
    await userEvent.keyboard('j')
    expect(element.currentTime).toBe(0)                                       // never before the start
    await userEvent.keyboard('5')
    expect(element.currentTime).toBe(50)
    await userEvent.keyboard('{End}')
    expect(element.currentTime).toBe(99)
    await userEvent.keyboard('{Home}')
    expect(element.currentTime).toBe(0)
    await userEvent.keyboard('>')
    expect(screen.getByLabelText('Playback speed')).toHaveValue('1.25')
    expect(element.playbackRate).toBe(1.25)
    await userEvent.keyboard('<')
    await userEvent.keyboard('<')
    expect(screen.getByLabelText('Playback speed')).toHaveValue('0.75')
    expect(element.muted).toBe(false)
    await userEvent.keyboard('m')
    expect(element.muted).toBe(true)
  })

  it('plays and pauses with the space bar and K', async () => {
    const play = vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue(undefined)
    const pause = vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => undefined)
    const { player, element } = await open()
    player.focus()
    await userEvent.keyboard(' ')
    expect(play).toHaveBeenCalledTimes(1)
    Object.defineProperty(element, 'paused', { value: false, configurable: true })
    await userEvent.keyboard('k')
    expect(pause).toHaveBeenCalledTimes(1)
  })

  it('leaves keys alone that the player does not use, and keys typed into a field', async () => {
    const { player, element } = await open()
    const field = document.createElement('input')
    player.appendChild(field)
    field.focus()
    await userEvent.keyboard('l5')
    expect(element.currentTime).toBe(0)
    player.focus()
    await userEvent.keyboard('q')
    expect(element.currentTime).toBe(0)
  })

  it('offers picture in picture only where the browser supports it', async () => {
    await open()
    expect(screen.queryByRole('button', { name: 'Picture in picture' })).toBeNull()
    document.body.innerHTML = ''
    Object.defineProperty(document, 'pictureInPictureEnabled', { value: true, configurable: true })
    const { element } = await open()
    const request_ = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(element, 'requestPictureInPicture', { value: request_, configurable: true })
    await userEvent.click(screen.getByRole('button', { name: 'Picture in picture' }))
    expect(request_).toHaveBeenCalledTimes(1)
  })

  it('lists the keys', async () => {
    await open()
    expect(screen.getByText(/space or K play and pause/)).toBeInTheDocument()
  })
})
