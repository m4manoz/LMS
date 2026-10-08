import { useEffect, useMemo, useState } from 'react'
import { ChevronDown, GraduationCap, Menu, Search, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ApiError } from '@/lib/api'
import { normalizeOrganization, organizationFromUrl, rememberOrganization, rememberedOrganization, validateOrganization } from '@/lib/organization'
import { getLanding, isSafeLink, landingImageUrl, type PublicCourse, type PublicLanding, type PublicOrganization } from '@/lib/publicApi'
import { cn } from '@/lib/utils'
import { catalogRoute, parseSiteRoute, siteHref, type SiteRoute } from '@/lib/siteRoutes'
import AboutPage from './landing/AboutPage'
import CatalogPage from './landing/CatalogPage'
import CoursePage from './landing/CoursePage'
import { BannerCarousel, CourseCard, CourseRow, FaqList, SiteFooter, iconFor } from './landing/parts'

// Kept here because the sign-in page and its tests have always imported them from the landing page.
export { normalizeOrganization, validateOrganization }

type Props = {
  /** Set when this website belongs to one organization: its page is shown, and nobody is asked which organization they mean. */
  fixed?: PublicOrganization
  /** Called with the organization's short name when the visitor wants to sign in, or undefined when it is not known. */
  onSignIn: (organization?: string) => void
  onJoin: () => void
}

const jump = (id: string) => document.getElementById(id)?.scrollIntoView?.({ behavior: 'smooth', block: 'start' })

/** The shared portal's home: a visitor names their organization to sign in, or to see its courses first. */
function PortalHome({ message, onSignIn, onChoose }: { message?: string | null; onSignIn: (slug: string) => void; onChoose: (slug: string) => void }) {
  const [value, setValue] = useState(() => rememberedOrganization() ?? '')
  const [problem, setProblem] = useState<string | null>(null)
  const go = (then: (slug: string) => void) => { const text = validateOrganization(value); setProblem(text); if (!text) then(normalizeOrganization(value)) }
  return (
    <form className="mx-auto flex w-full max-w-xl flex-col gap-2 text-left" onSubmit={(event) => { event.preventDefault(); go(onSignIn) }}>
      <label htmlFor="org-name" className="text-sm font-medium">Your organization</label>
      <Input id="org-name" className="h-12 text-base" placeholder="for example: riverside-school" autoComplete="organization" value={value} onChange={(event) => setValue(event.target.value)} aria-invalid={problem ? true : undefined} />
      <div className="flex flex-col gap-2 sm:flex-row">
        <Button type="submit" size="lg" className="h-12 flex-1 px-6 text-base">Continue to sign in</Button>
        <Button type="button" variant="outline" size="lg" className="h-12 flex-1 px-6 text-base" onClick={() => go(onChoose)}>See its courses</Button>
      </div>
      {problem || message ? <small role="alert" className="text-destructive">{problem ?? message}</small> : <small className="text-muted-foreground">The short name your school or company uses to sign in.</small>}
    </form>
  )
}

/** The public front page: an organization's courses, news and answers, where a visitor can find a course and apply for it. */
export default function LandingPage({ fixed, onSignIn, onJoin }: Props) {
  // On an organization's own website the organization is known. On the shared portal it is only known once the visitor names one (or the address does, ?org=acme).
  const [organization, setOrganization] = useState<string | null>(() => fixed?.slug ?? organizationFromUrl(window.location.search))
  const [data, setData] = useState<PublicLanding | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [category, setCategory] = useState<string | null>(null)
  // Which page of the website is showing. It lives in the address after the # so pages can be linked to and the back button works.
  const [route, setRoute] = useState<SiteRoute>(() => parseSiteRoute(window.location.hash) ?? { page: 'home' })
  const [menu, setMenu] = useState(false)
  const [explore, setExplore] = useState(false)

  useEffect(() => {
    if (!organization) { setData(null); return }
    let current = true
    setError(null)
    getLanding(organization)
      .then((landing) => { if (current) { setData(landing); rememberOrganization(landing.organization.slug) } })
      .catch((exception) => { if (current) { setData(null); setError(exception instanceof ApiError && exception.status === 404 && !fixed ? 'That organization was not found. Check its short name.' : 'The page could not be loaded. Please try again.') } })
    return () => { current = false }
  }, [organization, fixed])

  useEffect(() => {
    const changed = () => { const next = parseSiteRoute(window.location.hash); if (next) setRoute(next) }   // a section anchor (#faq) is not a page
    window.addEventListener('hashchange', changed)
    return () => window.removeEventListener('hashchange', changed)
  }, [])
  // A new page starts at its top.
  useEffect(() => { document.documentElement.scrollTop = 0 }, [route.page, route.page === 'course' ? route.id : ''])

  /** Opens a page of the website. A replace changes the address without adding a back-button step (used while typing in filters). */
  function go(next: SiteRoute, replace = false) {
    const href = siteHref(next)
    if (replace) { window.history.replaceState(null, '', `${window.location.pathname}${window.location.search}${href}`); setRoute(next) }
    else if (window.location.hash === href) setRoute(next)
    else window.location.hash = href
    setMenu(false); setExplore(false)
  }
  const openCourse = (course: PublicCourse) => go({ page: 'course', id: course.id })

  const courses = data?.courses ?? []
  const byId = useMemo(() => new Map(courses.map((course) => [course.id, course])), [courses])
  const filtering = search.trim() !== '' || category !== null
  const results = useMemo(() => {
    const words = search.trim().toLowerCase().split(/\s+/).filter(Boolean)
    return courses.filter((course) => (category === null || course.categoryId === category)
      && words.every((word) => `${course.title} ${course.code} ${course.summary} ${course.category ?? ''} ${course.teacher ?? ''}`.toLowerCase().includes(word)))
  }, [courses, search, category])

  /** What a button or link on the page does: the page's own sections, signing in, joining by invitation, or leaving for another address. */
  function follow(link: string) {
    setMenu(false); setExplore(false)
    if (link === '#login') return onSignIn(organization ?? undefined)
    if (link === '#join') return onJoin()
    if (link.startsWith('#/')) { const target = parseSiteRoute(link); if (target) return go(target) }
    // Away from the home page the sections it scrolls to are not there, so they map to the page that has them.
    if (link.startsWith('#') && route.page !== 'home') {
      if (link === '#courses' || link === '#categories') return go(catalogRoute())
      if (link === '#faq' || link === '#why') return go({ page: 'about' })
    }
    if (link.startsWith('#')) { if (link === '#courses') { setSearch(''); setCategory(null) } return jump(link.slice(1)) }
    if (!isSafeLink(link)) return
    if (link.startsWith('/')) window.location.assign(link)
    else window.open(link, '_blank', 'noopener,noreferrer')
  }

  function showCategory(id: string | null) { setCategory(id); setSearch(''); setExplore(false); setMenu(false); window.setTimeout(() => jump('courses'), 0) }
  function chooseOrganization(slug: string) { setError(null); setOrganization(slug) }
  /** Back from a preview to the portal's home, where another organization can be named. */
  function chooseAnother() { setOrganization(null); setData(null); setError(null) }

  const content = data?.content
  const name = data?.organization.name ?? fixed?.name ?? 'Learn'

  return (
    <div className="min-h-screen bg-background text-foreground" id="top">
      <header className="sticky top-0 z-40 border-b border-border/70 bg-background/90 backdrop-blur">
        <div className="mx-auto flex h-16 max-w-6xl items-center gap-3 px-4">
          <a href="/" className="flex shrink-0 items-center gap-2 font-semibold" aria-label={`${name} home`}>
            {content?.logoImageId && data ? <img src={landingImageUrl(data.organization.slug, content.logoImageId)} alt="" className="h-9 w-auto max-w-[8rem] rounded object-contain" />
              : <span className="flex h-9 w-9 items-center justify-center rounded-lg bg-primary text-primary-foreground"><GraduationCap className="h-5 w-5" /></span>}
            <span className="hidden text-lg sm:inline">{name}</span>
          </a>
          {data ? (
            <>
              <nav aria-label="Main" className="hidden items-center gap-1 md:flex">
                {([['Home', { page: 'home' }], ['Courses', catalogRoute()], ['About', { page: 'about' }]] as [string, SiteRoute][]).map(([label, target]) => (
                  <a key={label} href={siteHref(target)} aria-current={route.page === target.page || (label === 'Courses' && route.page === 'course') ? 'page' : undefined}
                    className={cn('rounded-md px-3 py-2 text-sm font-medium hover:bg-muted', (route.page === target.page || (label === 'Courses' && route.page === 'course')) && 'text-primary')}>{label}</a>
                ))}
              </nav>
              <div className="relative hidden md:block">
                <Button type="button" variant="ghost" aria-expanded={explore} aria-haspopup="true" onClick={() => setExplore((value) => !value)}>Explore<ChevronDown className="ml-1 h-4 w-4" aria-hidden /></Button>
                {explore ? (
                  <div role="menu" aria-label="Categories" className="absolute left-0 top-11 w-60 rounded-lg border border-border bg-popover p-1 shadow-lg">
                    <button type="button" role="menuitem" className="w-full rounded-md px-3 py-2 text-left text-sm hover:bg-muted" onClick={() => showCategory(null)}>All courses</button>
                    {data.categories.map((item) => <button key={item.id} type="button" role="menuitem" className="flex w-full items-center justify-between rounded-md px-3 py-2 text-left text-sm hover:bg-muted" onClick={() => showCategory(item.id)}>{item.name}<small className="text-muted-foreground">{item.courses}</small></button>)}
                  </div>
                ) : null}
              </div>
              <form role="search" className="relative mx-auto hidden max-w-md flex-1 md:block" onSubmit={(event) => { event.preventDefault(); if (route.page === 'home') jump('courses'); else go(catalogRoute({ q: search })) }}>
                <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
                <Input type="search" aria-label="Search courses" className="h-10 rounded-full pl-9" placeholder={content?.hero.searchPlaceholder || 'Search courses'} value={search} onChange={(event) => { setSearch(event.target.value); setCategory(null) }} />
              </form>
            </>
          ) : <span className="flex-1" />}
          <div className="hidden items-center gap-2 md:flex">
            {!fixed && organization ? <Button variant="ghost" onClick={chooseAnother}>Change organization</Button> : null}
            <Button variant="outline" onClick={() => follow('#login')}>Log in</Button>
            <Button onClick={() => follow('#join')}>I have an invitation</Button>
          </div>
          <Button variant="ghost" size="icon" className="ml-auto md:hidden" aria-label={menu ? 'Close menu' : 'Open menu'} aria-expanded={menu} onClick={() => setMenu((value) => !value)}>{menu ? <X className="h-5 w-5" /> : <Menu className="h-5 w-5" />}</Button>
        </div>
        {menu ? (
          <div data-testid="mobile-menu" className="flex flex-col gap-2 border-t border-border px-4 py-3 md:hidden">
            {data ? <nav aria-label="Pages" className="flex gap-2">{([['Home', { page: 'home' }], ['Courses', catalogRoute()], ['About', { page: 'about' }]] as [string, SiteRoute][]).map(([label, target]) => <a key={label} href={siteHref(target)} onClick={() => setMenu(false)} className="rounded-md border border-border px-3 py-1.5 text-sm font-medium hover:bg-muted">{label}</a>)}</nav> : null}
            {data ? <Input type="search" aria-label="Search courses" placeholder="Search courses" value={search} onChange={(event) => { setSearch(event.target.value); setCategory(null) }} /> : null}
            {data?.categories.map((item) => <button key={item.id} type="button" className="rounded-md px-2 py-2 text-left text-sm hover:bg-muted" onClick={() => showCategory(item.id)}>{item.name} <small className="text-muted-foreground">({item.courses})</small></button>)}
            <div className="mt-1 flex gap-2"><Button variant="outline" className="flex-1" onClick={() => follow('#login')}>Log in</Button><Button className="flex-1" onClick={() => follow('#join')}>I have an invitation</Button></div>
          </div>
        ) : null}
      </header>

      {content && data && route.page === 'courses' ? (
        <CatalogPage courses={courses} categories={data.categories} route={route} onRoute={go} onOpen={openCourse} />
      ) : content && data && route.page === 'course' ? (
        <CoursePage organization={data.organization.slug} organizationName={data.organization.name} courses={courses} courseId={route.id} onOpen={openCourse} onBack={() => go(catalogRoute())} onLogin={() => follow('#login')} />
      ) : content && data && route.page === 'about' ? (
        <AboutPage name={data.organization.name} content={content} onLink={follow} />
      ) : content && data ? (
        <main>
          <section className="border-b border-border bg-gradient-to-b from-primary/10 to-transparent">
            <div className="mx-auto flex max-w-6xl flex-col gap-8 px-4 py-14 sm:py-20 md:flex-row md:items-center md:justify-between">
              <div className="flex flex-col items-start gap-5">
                <h1 className="max-w-3xl text-4xl font-bold tracking-tight sm:text-5xl">{content.hero.title}</h1>
                {content.hero.subtitle ? <p className="max-w-2xl text-lg text-muted-foreground">{content.hero.subtitle}</p> : null}
                <div className="flex flex-wrap items-center gap-3">
                  {content.hero.primaryLabel && content.hero.primaryLink ? <Button size="lg" className="h-12 px-6 text-base" onClick={() => follow(content.hero.primaryLink)}>{content.hero.primaryLabel}</Button> : null}
                </div>
              </div>
              {content.heroImageId ? <img src={landingImageUrl(data.organization.slug, content.heroImageId)} alt="" className="h-auto max-h-72 w-full rounded-2xl object-cover md:w-5/12" /> : null}
            </div>
          </section>

          <div className="mx-auto flex max-w-6xl flex-col gap-14 px-4 py-10">
            <BannerCarousel banners={content.banners} slug={data.organization.slug} onLink={follow} />

            {content.intents.items.length > 0 ? (
              <section aria-label={content.intents.title || 'What brings you here'} className="flex flex-col gap-3">
                {content.intents.title ? <h2 className="text-xl font-semibold">{content.intents.title}</h2> : null}
                <div className="flex flex-wrap gap-2">{content.intents.items.map((item) => <button key={item.label} type="button" onClick={() => follow(item.link)} className="rounded-full border border-border bg-card px-4 py-2 text-sm font-medium hover:border-primary hover:text-primary">{item.label}</button>)}</div>
              </section>
            ) : null}

            <div id="courses" className="flex scroll-mt-24 flex-col gap-10">
              {filtering ? (
                <section aria-label="Search results" className="flex flex-col gap-3">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <h2 className="text-xl font-semibold" role="status">{results.length} course{results.length === 1 ? '' : 's'}{category ? ` in ${data.categories.find((item) => item.id === category)?.name ?? 'this category'}` : ''}{search.trim() ? ` for “${search.trim()}”` : ''}</h2>
                    <Button type="button" variant="outline" size="sm" onClick={() => { setSearch(''); setCategory(null) }}>Clear search</Button>
                  </div>
                  {results.length === 0 ? <p className="rounded-lg border border-dashed border-border p-6 text-sm text-muted-foreground">No course matches. Try another word, or clear the search to see everything.</p> : <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">{results.map((course) => <div key={course.id} className="flex [&>button]:w-full"><CourseCard course={course} onOpen={openCourse} /></div>)}</div>}
                </section>
              ) : data.rows.length > 0 ? data.rows.map((row) => <CourseRow key={row.id} title={row.title} subtitle={row.subtitle} courses={row.courseIds.map((id) => byId.get(id)).filter((course): course is PublicCourse => course !== undefined)} onOpen={openCourse} />)
                : courses.length > 0 ? <CourseRow title="Courses" courses={courses} onOpen={openCourse} />
                  : <p className="rounded-lg border border-dashed border-border p-8 text-center text-muted-foreground">No courses are open yet. Please check back soon.</p>}
            </div>

            {content.showCategories && data.categories.length > 0 ? (
              <section id="categories" aria-label={content.categoriesTitle || 'Categories'} className="flex scroll-mt-24 flex-col gap-3">
                <h2 className="text-xl font-semibold">{content.categoriesTitle || 'Explore categories'}</h2>
                <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
                  {data.categories.map((item) => <button key={item.id} type="button" onClick={() => showCategory(item.id)} className="rounded-xl border border-border bg-card p-4 text-left transition hover:border-primary hover:shadow"><strong className="block">{item.name}</strong><small className="text-muted-foreground">{item.courses} course{item.courses === 1 ? '' : 's'}</small></button>)}
                </div>
              </section>
            ) : null}

            {content.features.length > 0 ? (
              <section id="why" aria-label={content.featuresTitle || 'Why learn with us'} className="flex scroll-mt-24 flex-col gap-4">
                <h2 className="text-xl font-semibold">{content.featuresTitle || 'Why learn with us'}</h2>
                <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
                  {content.features.map((feature) => { const Icon = iconFor(feature.icon); return (
                    <div key={feature.title} className="flex flex-col gap-2 rounded-xl border border-border bg-card p-5">
                      <span className="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10 text-primary"><Icon className="h-5 w-5" aria-hidden /></span>
                      <strong>{feature.title}</strong><p className="text-sm text-muted-foreground">{feature.text}</p>
                    </div>
                  ) })}
                </div>
              </section>
            ) : null}

            {content.stats.length > 0 ? (
              <section aria-label="In numbers" className={cn('grid gap-4 rounded-2xl bg-primary p-8 text-primary-foreground', content.stats.length > 1 ? 'sm:grid-cols-2 lg:grid-cols-4' : '')}>
                {content.stats.map((stat) => <div key={stat.label} className="text-center"><div className="text-4xl font-bold">{stat.value}</div><div className="mt-1 text-sm opacity-90">{stat.label}</div></div>)}
              </section>
            ) : null}

            {content.testimonials.length > 0 ? (
              <section aria-label={content.testimonialsTitle || 'What learners say'} className="flex flex-col gap-4">
                <h2 className="text-xl font-semibold">{content.testimonialsTitle || 'What learners say'}</h2>
                <div className="grid gap-4 md:grid-cols-2">
                  {content.testimonials.map((item) => <figure key={item.name + item.quote} className="flex flex-col gap-3 rounded-xl border border-border bg-card p-5"><blockquote className="text-sm">“{item.quote}”</blockquote><figcaption className="text-sm"><strong>{item.name}</strong>{item.role ? <span className="text-muted-foreground"> · {item.role}</span> : null}</figcaption></figure>)}
                </div>
              </section>
            ) : null}

            {content.faq.length > 0 ? (
              <section id="faq" aria-label={content.faqTitle || 'Frequently asked questions'} className="flex scroll-mt-24 flex-col gap-4">
                <h2 className="text-center text-xl font-semibold">{content.faqTitle || 'Frequently asked questions'}</h2>
                <FaqList items={content.faq} />
              </section>
            ) : null}
          </div>
        </main>
      ) : (
        <main className="mx-auto flex max-w-3xl flex-col gap-6 px-4 py-24 text-center">
          {fixed ? (
            <>
              <h1 className="text-4xl font-bold tracking-tight">{fixed.name}</h1>
              {error ? <p role="alert" className="text-destructive">{error}</p> : <p role="status" className="text-muted-foreground">Loading courses…</p>}
              {error ? <div><Button variant="outline" onClick={() => window.location.reload()}>Try again</Button></div> : null}
            </>
          ) : organization && !error ? (
            <>
              <h1 className="text-4xl font-bold tracking-tight">Learn without limits</h1>
              <p role="status" className="text-muted-foreground">Loading courses…</p>
            </>
          ) : (
            <>
              <h1 className="text-4xl font-bold tracking-tight">Sign in to your organization</h1>
              <p className="text-lg text-muted-foreground">Enter your organization to sign in, or to see its courses and apply.</p>
              <PortalHome message={error} onSignIn={(slug) => onSignIn(slug)} onChoose={chooseOrganization} />
            </>
          )}
        </main>
      )}

      {content && data ? <SiteFooter name={data.organization.name} about={content.footerAbout} groups={content.footerGroups} copyright={content.copyright} onLink={follow} logoUrl={content.logoImageId ? landingImageUrl(data.organization.slug, content.logoImageId) : undefined} /> : null}
    </div>
  )
}
