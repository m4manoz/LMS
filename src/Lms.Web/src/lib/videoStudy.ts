import { formatDuration } from './video'

export type Chapter = { startSeconds: number; title: string }
export type VideoNoteItem = { id: string; positionSeconds: number; text: string; createdAtUtc: string; updatedAtUtc: string }

/** Playback speeds offered, slowest first. */
export const SPEEDS = [0.5, 0.75, 1, 1.25, 1.5, 1.75, 2] as const
export const speedLabel = (speed: number) => (speed === 1 ? 'Normal' : `${speed}×`)

/** The next faster or slower speed, staying within the range offered. */
export function stepSpeed(current: number, direction: 1 | -1): number {
  const index = SPEEDS.findIndex((item) => item >= current - 1e-9)
  const position = index === -1 ? SPEEDS.length - 1 : index
  return SPEEDS[Math.min(SPEEDS.length - 1, Math.max(0, position + direction))]
}

/** "1:05", "01:05", "1:02:03" or a plain number of seconds; null when it is not a time. */
export function parseTime(text: string): number | null {
  const value = text.trim()
  if (/^\d+$/.test(value)) return Number(value)
  const match = /^(?:(\d{1,2}):)?(\d{1,2}):(\d{2})$/.exec(value)
  if (!match) return null
  const [, hours, minutes, seconds] = match
  if (Number(seconds) > 59 || (hours !== undefined && Number(minutes) > 59)) return null
  return Number(hours ?? 0) * 3600 + Number(minutes) * 60 + Number(seconds)
}

/** One chapter a line, as people write them in a video description: "0:00 Introduction". Returns the chapters, or what is wrong with a line. */
export function parseChapters(text: string): { chapters: Chapter[]; error: string | null } {
  const chapters: Chapter[] = []
  const lines = text.split('\n').map((line) => line.trim()).filter(Boolean)
  for (const [index, line] of lines.entries()) {
    const match = /^(\S+)\s+(.+)$/.exec(line)
    const start = match ? parseTime(match[1]) : null
    if (!match || start === null) return { chapters: [], error: `Line ${index + 1} should start with a time, such as 2:30, then the title.` }
    chapters.push({ startSeconds: start, title: match[2].trim() })
  }
  chapters.sort((a, b) => a.startSeconds - b.startSeconds)
  if (chapters.length > 0 && chapters[0].startSeconds !== 0) return { chapters: [], error: 'The first chapter must start at 0:00.' }
  if (chapters.some((item, index) => index > 0 && item.startSeconds === chapters[index - 1].startSeconds)) return { chapters: [], error: 'Two chapters cannot start at the same time.' }
  if (chapters.some((item) => item.title.length > 120)) return { chapters: [], error: 'A chapter title can be at most 120 characters.' }
  if (chapters.length > 60) return { chapters: [], error: 'A video can have at most 60 chapters.' }
  return { chapters, error: null }
}

export const formatChapters = (chapters: Chapter[]) => chapters.map((item) => `${formatDuration(item.startSeconds)} ${item.title}`).join('\n')

/** The chapter playing at the given time: the last one that has started. */
export function currentChapter(chapters: Chapter[], seconds: number): Chapter | null {
  let found: Chapter | null = null
  for (const chapter of chapters) { if (chapter.startSeconds <= seconds) found = chapter; else break }
  return found
}

export type ShortcutAction = { type: 'toggle' } | { type: 'skip'; seconds: number } | { type: 'speed'; direction: 1 | -1 } | { type: 'mute' } | { type: 'fullscreen' } | { type: 'jump'; fraction: number } | { type: 'start' } | { type: 'end' }

/** What a key does in the player, or null for a key the player leaves alone. Combinations with Ctrl, Alt or Meta are never taken over. */
export function shortcutFor(event: { key: string; shiftKey?: boolean; ctrlKey?: boolean; altKey?: boolean; metaKey?: boolean }): ShortcutAction | null {
  if (event.ctrlKey || event.altKey || event.metaKey) return null
  switch (event.key) {
    case ' ': case 'k': case 'K': return { type: 'toggle' }
    case 'ArrowLeft': return { type: 'skip', seconds: -5 }
    case 'ArrowRight': return { type: 'skip', seconds: 5 }
    case 'j': case 'J': return { type: 'skip', seconds: -10 }
    case 'l': case 'L': return { type: 'skip', seconds: 10 }
    case 'm': case 'M': return { type: 'mute' }
    case 'f': case 'F': return { type: 'fullscreen' }
    case '<': return { type: 'speed', direction: -1 }
    case '>': return { type: 'speed', direction: 1 }
    case 'Home': return { type: 'start' }
    case 'End': return { type: 'end' }
    default: return /^[0-9]$/.test(event.key) ? { type: 'jump', fraction: Number(event.key) / 10 } : null
  }
}

/** Keys typed into a field belong to the field, not the player. */
export const typingTarget = (target: EventTarget | null) => target instanceof HTMLElement && (['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName) || target.isContentEditable === true)
