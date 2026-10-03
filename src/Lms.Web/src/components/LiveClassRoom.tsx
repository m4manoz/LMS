import { useEffect, useReducer, useRef, useState } from 'react'
import { Hand, Mic, MicOff, MonitorOff, MonitorUp, PhoneOff, Video, VideoOff } from 'lucide-react'
import { Room, RoomEvent, Track, type Participant } from 'livekit-client'
import { Button } from '@/components/ui/button'

/** What to tell the person when the room cannot be reached, with the likely cause when the address is the usual mistake. */
export function connectionProblem(url: string): string {
  const base = 'Could not connect to the class room. Check the connection and try again.'
  try {
    const address = new URL(url)
    const local = ['localhost', '127.0.0.1', '[::1]'].includes(address.hostname)
    if (local && address.protocol === 'wss:') return `${base} The server address is ${url}, but a LiveKit server on this machine is not secure: an administrator should change it to ws://${address.host} under Integrations → Live classes.`
    if (local) return `${base} Is the LiveKit server running on this machine (scripts/livekit-dev.ps1)? Other computers cannot use a localhost address.`
  } catch { /* an address that cannot be read gets the plain message */ }
  return base
}

export type LiveKitJoin = { url: string; token: string; room: string; canPublish: boolean; isHost: boolean }

const roomEvents = [
  RoomEvent.ParticipantConnected, RoomEvent.ParticipantDisconnected, RoomEvent.TrackSubscribed, RoomEvent.TrackUnsubscribed,
  RoomEvent.TrackMuted, RoomEvent.TrackUnmuted, RoomEvent.LocalTrackPublished, RoomEvent.LocalTrackUnpublished, RoomEvent.ActiveSpeakersChanged, RoomEvent.RecordingStatusChanged,
]

/** One person's camera (or a shared screen). Remote audio is played through a hidden element. */
function Tile({ participant, source, label, className = '', handUp = false }: { participant: Participant; source: Track.Source; label: string; className?: string; handUp?: boolean }) {
  const video = useRef<HTMLVideoElement>(null)
  const publication = participant.getTrackPublication(source)
  const track = publication?.track
  useEffect(() => {
    const element = video.current
    if (!track || !element) return
    track.attach(element)
    return () => { track.detach(element) }
  }, [track])
  const showing = !!track && !publication?.isMuted
  return (
    <div className={`${className.includes('absolute') ? '' : 'relative'} aspect-video overflow-hidden rounded-md bg-muted ${className}`} data-testid={`tile-${source}`}>
      <video ref={video} autoPlay playsInline muted={participant.isLocal} className={showing ? 'h-full w-full object-contain' : 'hidden'} />
      {showing ? null : <span className="absolute inset-0 grid place-items-center text-sm text-muted-foreground">Camera off</span>}
      <span className="absolute bottom-1 left-1 rounded bg-background/80 px-2 py-0.5 text-xs">{label}</span>
      {handUp ? <span role="img" aria-label={`${label} has a hand raised`} className="absolute right-1 top-1 rounded-full bg-yellow-400 p-1.5 text-black shadow"><Hand className="h-4 w-4" aria-hidden /></span> : null}
    </div>
  )
}

function RemoteAudio({ participant }: { participant: Participant }) {
  const audio = useRef<HTMLAudioElement>(null)
  const track = participant.getTrackPublication(Track.Source.Microphone)?.track
  useEffect(() => {
    const element = audio.current
    if (!track || !element) return
    track.attach(element)
    return () => { track.detach(element) }
  }, [track])
  return <audio ref={audio} autoPlay />
}

/** The class room: everyone's video, with microphone, camera, screen sharing and leave. */
/** raised: the ids of the people whose hands are up; a hand shows on their picture. */
export default function LiveClassRoom({ join, displayName, onLeave, raised = [] }: { join: LiveKitJoin; displayName: string; onLeave: () => void; raised?: string[] }) {
  // A fresh Room for every connection attempt: a room that was disconnected cannot be reused safely.
  const [room, setRoom] = useState<Room | null>(null)
  const [state, setState] = useState<'connecting' | 'connected' | 'failed'>('connecting')
  const [problem, setProblem] = useState<string | null>(null)
  const [, refresh] = useReducer((n: number) => n + 1, 0)
  const [mic, setMic] = useState(false)
  const [camera, setCamera] = useState(false)
  const [sharing, setSharing] = useState(false)
  const left = useRef(false)

  useEffect(() => {
    let cancelled = false
    const room = new Room({ adaptiveStream: true, dynacast: true })
    setRoom(room)
    for (const event of roomEvents) room.on(event, refresh)
    room.on(RoomEvent.Disconnected, () => { if (!left.current && !cancelled) { setState('failed'); setProblem('You were disconnected from the class.') } })
    ;(async () => {
      try {
        await room.connect(join.url, join.token)
        if (cancelled) { await room.disconnect(); return }
        setState('connected')
        if (join.canPublish) {
          // Devices are optional: a learner with no camera can still listen and watch.
          await room.localParticipant.setMicrophoneEnabled(true).then(() => setMic(true), () => setMic(false))
          await room.localParticipant.setCameraEnabled(true).then(() => setCamera(true), () => setCamera(false))
        }
        refresh()
      } catch {
        if (!cancelled) { setState('failed'); setProblem(connectionProblem(join.url)) }
      }
    })()
    return () => { cancelled = true; for (const event of roomEvents) room.off(event, refresh); void room.disconnect() }
  }, [join])

  async function toggle(kind: 'mic' | 'camera' | 'screen') {
    setProblem(null)
    try {
      if (!room) return
      const local = room.localParticipant
      if (kind === 'mic') { await local.setMicrophoneEnabled(!mic); setMic(!mic) }
      if (kind === 'camera') { await local.setCameraEnabled(!camera); setCamera(!camera) }
      if (kind === 'screen') { await local.setScreenShareEnabled(!sharing); setSharing(!sharing) }
    } catch { setProblem(kind === 'screen' ? 'Screen sharing was not started.' : 'That device is not available.') }
  }

  function leave() { left.current = true; void room?.disconnect(); onLeave() }

  const people = room ? [room.localParticipant, ...Array.from(room.remoteParticipants.values())] : []
  const name = (participant: Participant) => (participant.isLocal ? `${displayName} (you)` : participant.name || participant.identity)

  // Two people and no screen being shared: the other person fills the picture and your own camera is a small inset.
  const someoneSharing = people.some((person) => person.getTrackPublication(Track.Source.ScreenShare)?.track)
  const remote = people.find((person) => !person.isLocal)
  const inset = people.length === 2 && !someoneSharing && remote !== undefined

  return (
    <section aria-label="Live class room" className="flex flex-col gap-3">
      {room?.isRecording ? <p role="status" aria-label="Recording" className="flex items-center gap-2 text-sm font-medium text-destructive"><span aria-hidden className="h-2.5 w-2.5 animate-pulse rounded-full bg-destructive" />This class is being recorded</p> : null}
      {state === 'connecting' ? <p role="status" className="text-sm text-muted-foreground">Connecting to the class…</p> : null}
      {problem ? <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/5 p-2 text-sm text-destructive">{problem}</p> : null}
      {state === 'connected' && inset ? (
        <div className="relative mx-auto w-full max-w-[calc((100vh-14rem)*16/9)]">
          <Tile participant={remote} source={Track.Source.Camera} label={name(remote)} handUp={raised.includes(remote.identity)} />
          <Tile participant={room!.localParticipant} source={Track.Source.Camera} label={name(room!.localParticipant)} handUp={raised.includes(room!.localParticipant.identity)} className="absolute bottom-3 right-3 w-1/4 min-w-28 shadow-lg ring-2 ring-background" />
          <RemoteAudio participant={remote} />
        </div>
      ) : null}
      {state === 'connected' && !inset ? (
        <div className={`grid max-h-[calc(100vh-14rem)] gap-2 overflow-y-auto ${people.length <= 1 ? "grid-cols-1" : people.length <= 4 ? "sm:grid-cols-2" : "sm:grid-cols-2 xl:grid-cols-3"}`}>
          {people.map((person) => (
            <div key={person.sid || person.identity} className="contents">
              <Tile participant={person} source={Track.Source.Camera} label={name(person)} handUp={raised.includes(person.identity)} />
              {person.getTrackPublication(Track.Source.ScreenShare)?.track ? <Tile participant={person} source={Track.Source.ScreenShare} label={`${name(person)} – screen`} /> : null}
              {person.isLocal ? null : <RemoteAudio participant={person} />}
            </div>
          ))}
        </div>
      ) : null}
      <p className="text-xs text-muted-foreground" data-testid="room-count">{state === 'connected' ? `${people.length} in the class` : ''}</p>
      <div className="flex flex-wrap gap-2">
        {join.canPublish ? (
          <>
            <Button variant="outline" disabled={state !== 'connected'} aria-pressed={mic} onClick={() => void toggle('mic')}>{mic ? <Mic className="mr-1.5 h-4 w-4" aria-hidden /> : <MicOff className="mr-1.5 h-4 w-4" aria-hidden />}{mic ? 'Mute microphone' : 'Unmute microphone'}</Button>
            <Button variant="outline" disabled={state !== 'connected'} aria-pressed={camera} onClick={() => void toggle('camera')}>{camera ? <Video className="mr-1.5 h-4 w-4" aria-hidden /> : <VideoOff className="mr-1.5 h-4 w-4" aria-hidden />}{camera ? 'Turn camera off' : 'Turn camera on'}</Button>
            <Button variant="outline" disabled={state !== 'connected'} aria-pressed={sharing} onClick={() => void toggle('screen')}>{sharing ? <MonitorOff className="mr-1.5 h-4 w-4" aria-hidden /> : <MonitorUp className="mr-1.5 h-4 w-4" aria-hidden />}{sharing ? 'Stop sharing' : 'Share screen'}</Button>
          </>
        ) : null}
        <Button variant="softDestructive" onClick={leave}><PhoneOff className="mr-1.5 h-4 w-4" aria-hidden />Leave class</Button>
      </div>
    </section>
  )
}
