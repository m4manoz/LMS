import { describe, expect, it } from 'vitest'
import { currentChapter, formatChapters, parseChapters, parseTime, shortcutFor, SPEEDS, speedLabel, stepSpeed, typingTarget } from './videoStudy'

describe('parseTime', () => {
  it('reads minutes and seconds, hours, and plain seconds', () => {
    expect(parseTime('0:00')).toBe(0)
    expect(parseTime('2:30')).toBe(150)
    expect(parseTime('02:05')).toBe(125)
    expect(parseTime('1:02:03')).toBe(3723)
    expect(parseTime(' 45 ')).toBe(45)
  })
  it('refuses what is not a time', () => {
    for (const bad of ['', 'abc', '1:75', '1:2', '1:61:00', '-3', '1:2:3:4']) expect(parseTime(bad)).toBeNull()
  })
})

describe('parseChapters', () => {
  it('reads one chapter a line, in any order, ignoring blank lines', () => {
    const { chapters, error } = parseChapters('2:30 The main idea\n\n0:00 Introduction\n1:00:00 The very end')
    expect(error).toBeNull()
    expect(chapters).toEqual([{ startSeconds: 0, title: 'Introduction' }, { startSeconds: 150, title: 'The main idea' }, { startSeconds: 3600, title: 'The very end' }])
  })
  it('says which line is wrong', () => {
    expect(parseChapters('0:00 Intro\nsoon The end').error).toBe('Line 2 should start with a time, such as 2:30, then the title.')
    expect(parseChapters('0:00').error).toMatch(/Line 1/)
  })
  it('wants the first chapter at the very start, no two at the same time and sensible titles', () => {
    expect(parseChapters('0:10 Late').error).toBe('The first chapter must start at 0:00.')
    expect(parseChapters('0:00 A\n0:00 B').error).toBe('Two chapters cannot start at the same time.')
    expect(parseChapters(`0:00 ${'x'.repeat(121)}`).error).toMatch(/120/)
    expect(parseChapters(Array.from({ length: 61 }, (_, index) => `${index}:00 Part`).join('\n')).error).toMatch(/at most 60/)
  })
  it('accepts no chapters at all, which clears the outline', () => {
    expect(parseChapters('  \n ')).toEqual({ chapters: [], error: null })
  })
  it('writes chapters back the way they are read', () => {
    const chapters = [{ startSeconds: 0, title: 'Intro' }, { startSeconds: 3725, title: 'Late' }]
    expect(formatChapters(chapters)).toBe('0:00 Intro\n1:02:05 Late')
    expect(parseChapters(formatChapters(chapters)).chapters).toEqual(chapters)
  })
})

describe('currentChapter', () => {
  const chapters = [{ startSeconds: 0, title: 'A' }, { startSeconds: 60, title: 'B' }, { startSeconds: 120, title: 'C' }]
  it('is the last chapter that has started', () => {
    expect(currentChapter(chapters, 0)?.title).toBe('A')
    expect(currentChapter(chapters, 59)?.title).toBe('A')
    expect(currentChapter(chapters, 60)?.title).toBe('B')
    expect(currentChapter(chapters, 9999)?.title).toBe('C')
  })
  it('is nothing when there are no chapters', () => expect(currentChapter([], 10)).toBeNull())
})

describe('speeds', () => {
  it('steps through the speeds offered and stops at either end', () => {
    expect(stepSpeed(1, 1)).toBe(1.25)
    expect(stepSpeed(1, -1)).toBe(0.75)
    expect(stepSpeed(2, 1)).toBe(2)
    expect(stepSpeed(0.5, -1)).toBe(0.5)
    expect(stepSpeed(1.1, 1)).toBe(1.5)                                      // a speed that is not offered goes to the next one
    expect(SPEEDS).toContain(1)
  })
  it('is named in words for normal and with a multiplication sign otherwise', () => {
    expect(speedLabel(1)).toBe('Normal')
    expect(speedLabel(1.5)).toBe('1.5×')
  })
})

describe('shortcutFor', () => {
  it('maps the keys the player uses', () => {
    expect(shortcutFor({ key: ' ' })).toEqual({ type: 'toggle' })
    expect(shortcutFor({ key: 'k' })).toEqual({ type: 'toggle' })
    expect(shortcutFor({ key: 'ArrowRight' })).toEqual({ type: 'skip', seconds: 5 })
    expect(shortcutFor({ key: 'ArrowLeft' })).toEqual({ type: 'skip', seconds: -5 })
    expect(shortcutFor({ key: 'j' })).toEqual({ type: 'skip', seconds: -10 })
    expect(shortcutFor({ key: 'L' })).toEqual({ type: 'skip', seconds: 10 })
    expect(shortcutFor({ key: '>' })).toEqual({ type: 'speed', direction: 1 })
    expect(shortcutFor({ key: '<' })).toEqual({ type: 'speed', direction: -1 })
    expect(shortcutFor({ key: 'm' })).toEqual({ type: 'mute' })
    expect(shortcutFor({ key: 'f' })).toEqual({ type: 'fullscreen' })
    expect(shortcutFor({ key: '5' })).toEqual({ type: 'jump', fraction: 0.5 })
    expect(shortcutFor({ key: '0' })).toEqual({ type: 'jump', fraction: 0 })
    expect(shortcutFor({ key: 'Home' })).toEqual({ type: 'start' })
    expect(shortcutFor({ key: 'End' })).toEqual({ type: 'end' })
  })
  it('leaves other keys and combinations with Ctrl, Alt or Meta to the browser', () => {
    expect(shortcutFor({ key: 'a' })).toBeNull()
    expect(shortcutFor({ key: 'Tab' })).toBeNull()
    expect(shortcutFor({ key: 'f', ctrlKey: true })).toBeNull()
    expect(shortcutFor({ key: 'ArrowLeft', altKey: true })).toBeNull()
    expect(shortcutFor({ key: 'k', metaKey: true })).toBeNull()
  })
  it('does not take over keys typed into a field', () => {
    expect(typingTarget(document.createElement('textarea'))).toBe(true)
    expect(typingTarget(document.createElement('input'))).toBe(true)
    expect(typingTarget(document.createElement('select'))).toBe(true)
    expect(typingTarget(document.createElement('div'))).toBe(false)
    expect(typingTarget(null)).toBe(false)
  })
})
