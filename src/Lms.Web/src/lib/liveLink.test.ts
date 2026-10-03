import { afterEach, describe, expect, it } from 'vitest'
import { clearLiveLink, parseLiveLink } from './liveLink'

const id = 'b29464f4-fec3-40bd-9e37-656bca0a496f'

describe('parseLiveLink', () => {
  it('reads a join link and a host link', () => {
    expect(parseLiveLink(`?liveSession=${id}`)).toEqual({ sessionId: id, recording: false })
    expect(parseLiveLink(`?liveSession=${id}&host=1`)).toEqual({ sessionId: id, recording: false })
  })

  it('reads a recording link', () => expect(parseLiveLink(`?recording=${id}`)).toEqual({ sessionId: id, recording: true }))

  it('accepts upper-case ids and ignores spaces around them', () => expect(parseLiveLink(`?liveSession=%20${id.toUpperCase()}%20`)?.sessionId).toBe(id.toUpperCase()))

  it('ignores anything that is not a class id, so a bad link cannot reach the page', () => {
    for (const search of ['', '?', '?liveSession=', '?liveSession=abc', `?liveSession=${id}x`, '?liveSession=../../admin', '?other=1', `?liveSession=<script>`]) expect(parseLiveLink(search), search).toBeNull()
  })
})

describe('clearLiveLink', () => {
  afterEach(() => history.replaceState(null, '', '/'))

  it('removes the class parameters but keeps everything else', () => {
    history.replaceState(null, '', `/?liveSession=${id}&host=1&keep=yes#section`)
    clearLiveLink()
    expect(window.location.search).toBe('?keep=yes')
    expect(window.location.hash).toBe('#section')
  })

  it('leaves a clean address when nothing else is there', () => {
    history.replaceState(null, '', `/?recording=${id}`)
    clearLiveLink()
    expect(window.location.search).toBe('')
  })
})
