/**
 * The pages of an organization's public website, kept in the address after the # so each one can be linked to, bookmarked and used with the back button:
 *   #/            home          #/courses?category=…&q=…&sort=…   the catalog
 *   #/course/{id} one course    #/about                          about the organization
 * Any other hash (#faq, #invite=…, #reset=…, #/platform) is not a website page; this returns null for it.
 */
export type SortKey = 'newest' | 'rating' | 'title'
export type SiteRoute =
  | { page: 'home' }
  | { page: 'courses'; category: string | null; q: string; sort: SortKey }
  | { page: 'course'; id: string }
  | { page: 'about' }

export const sortKeys: SortKey[] = ['newest', 'rating', 'title']
export const sortLabels: Record<SortKey, string> = { newest: 'Newest', rating: 'Highest rated', title: 'Title A–Z' }

/** Ids are GUIDs in practice; anything made only of letters, digits and hyphens is accepted so a bad link just shows "not available". */
const idPattern = /^[A-Za-z0-9-]{1,64}$/

export function parseSiteRoute(hash: string): SiteRoute | null {
  if (hash === '' || hash === '#' || hash === '#/') return { page: 'home' }
  if (!hash.startsWith('#/')) return null
  const [path, query = ''] = hash.slice(1).split('?')
  const parts = path.split('/').filter(Boolean)
  if (parts.length === 1 && parts[0] === 'about') return { page: 'about' }
  if (parts.length === 2 && parts[0] === 'course' && idPattern.test(parts[1])) return { page: 'course', id: parts[1].toLowerCase() }
  if (parts.length === 1 && parts[0] === 'courses') {
    const params = new URLSearchParams(query)
    const category = params.get('category')
    const sort = params.get('sort') as SortKey | null
    return { page: 'courses', category: category && idPattern.test(category) ? category.toLowerCase() : null, q: (params.get('q') ?? '').slice(0, 100), sort: sort && sortKeys.includes(sort) ? sort : 'newest' }
  }
  return null
}

/** The address (hash) of a page. The catalog leaves out whatever is at its default so links stay short. */
export function siteHref(route: SiteRoute): string {
  switch (route.page) {
    case 'home': return '#/'
    case 'about': return '#/about'
    case 'course': return `#/course/${route.id}`
    case 'courses': {
      const params = new URLSearchParams()
      if (route.category) params.set('category', route.category)
      if (route.q.trim()) params.set('q', route.q.trim())
      if (route.sort !== 'newest') params.set('sort', route.sort)
      const query = params.toString()
      return query ? `#/courses?${query}` : '#/courses'
    }
  }
}

export const catalogRoute = (over: Partial<Extract<SiteRoute, { page: 'courses' }>> = {}): SiteRoute => ({ page: 'courses', category: null, q: '', sort: 'newest', ...over })
