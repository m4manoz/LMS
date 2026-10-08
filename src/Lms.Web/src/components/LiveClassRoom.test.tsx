import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import LiveClassRoom from './LiveClassRoom'

const fake = vi.hoisted(() => {
  type Handler = () => void
  const person = (identity: string, name: string, local = false) => ({
    identity, name, sid: `S_${identity}`, isLocal: local, isMicrophoneEnabled: true,
    setMicrophoneEnabled: async (on: boolean) => { person_state(identity).isMicrophoneEnabled = on; Room.current?.emit(on ? 'tum' : 'tm') },
    setCameraEnabled: async () => undefined, setScreenShareEnabled: async () => undefined,
    getTrackPublication: () => undefined,
  })
  const registry = new Map<string, ReturnType<typeof person>>()
  const person_state = (identity: string) => registry.get(identity)!
  class Room {
    static current: Room | null = null
    handlers = new Map<string, Handler[]>()
    isRecording = false
    localParticipant = person('me', 'Tara', true)
    remoteParticipants = new Map<string, ReturnType<typeof person>>()
    constructor() { Room.current = this; registry.clear(); registry.set('me', this.localParticipant) }
    on(event: string, handler: Handler) { this.handlers.set(event, [...(this.handlers.get(event) ?? []), handler]); return this }
    off(event: string, handler: Handler) { this.handlers.set(event, (this.handlers.get(event) ?? []).filter((item) => item !== handler)); return this }
    emit(event: string) { for (const handler of this.handlers.get(event) ?? []) handler() }
    async connect() { for (const [id, name] of [['ada', 'Ada'], ['ben', 'Ben']]) { const remote = person(id, name); registry.set(id, remote); this.remoteParticipants.set(id, remote) } }
    async disconnect() { return undefined }
  }
  return { Room, registry }
})

vi.mock('livekit-client', () => ({
  Room: fake.Room,
  RoomEvent: { ParticipantConnected: 'pc', ParticipantDisconnected: 'pd', TrackSubscribed: 'ts', TrackUnsubscribed: 'tu', TrackMuted: 'tm', TrackUnmuted: 'tum', LocalTrackPublished: 'ltp', LocalTrackUnpublished: 'ltu', ActiveSpeakersChanged: 'as', RecordingStatusChanged: 'rs', Disconnected: 'disconnected' },
  Track: { Source: { Camera: 'camera', Microphone: 'microphone', ScreenShare: 'screen_share' } },
}))

const join = { url: 'wss://x', token: 't', room: 'lms-abc', canPublish: true, isHost: true }

describe('the class room for the host', () => {
  beforeEach(() => { fake.registry.clear() })

  it('puts a mute button on everyone else and one to mute everyone else, and says who was meant', async () => {
    const onMute = vi.fn()
    render(<LiveClassRoom join={join} displayName="Tara" onLeave={vi.fn()} onMute={onMute} />)
    await userEvent.click(await screen.findByRole('button', { name: 'Mute Ada' }))
    expect(onMute).toHaveBeenLastCalledWith('ada')
    await userEvent.click(screen.getByRole('button', { name: 'Mute Ben' }))
    expect(onMute).toHaveBeenLastCalledWith('ben')
    expect(screen.queryByRole('button', { name: /^Mute Tara/ })).toBeNull()                                  // not on yourself
    await userEvent.click(screen.getByRole('button', { name: 'Mute everyone else' }))
    expect(onMute).toHaveBeenLastCalledWith(null)
  })

  it('offers none of it to people who are not the host', async () => {
    render(<LiveClassRoom join={{ ...join, isHost: false }} displayName="Ada" onLeave={vi.fn()} />)
    await screen.findByText(/3 in the class/)
    expect(screen.queryByRole('button', { name: /^Mute (Ada|Ben)/ })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Mute everyone else' })).toBeNull()
  })

  it('shows your own microphone as off when the host switched it off, and on again when you switch it on', async () => {
    render(<LiveClassRoom join={{ ...join, isHost: false }} displayName="Ben" onLeave={vi.fn()} />)
    const mic = await screen.findByRole('button', { name: 'Mute microphone' })
    expect(mic).toHaveAttribute('aria-pressed', 'true')
    fake.registry.get('me')!.isMicrophoneEnabled = false                                                      // the host switches it off from outside
    act(() => fake.Room.current!.emit('tm'))
    const off = await screen.findByRole('button', { name: 'Unmute microphone' })
    expect(off).toHaveAttribute('aria-pressed', 'false')
    await userEvent.click(off)
    expect(await screen.findByRole('button', { name: 'Mute microphone' })).toHaveAttribute('aria-pressed', 'true')
  })
})
