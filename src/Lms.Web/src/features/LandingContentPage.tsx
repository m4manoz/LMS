import { useCallback, useEffect, useMemo, useState } from 'react'
import { ArrowDown, ArrowUp, ExternalLink, Plus, RotateCcw, Save, Trash2 } from 'lucide-react'
import { ErrorBanner, Field, FormSection, NoticeBanner, PageHeader } from '@/components/form'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Textarea } from '@/components/ui/textarea'
import { ApiError, apiRequest } from '@/lib/api'
import { isSafeLink, type LandingContent, type LandingEditorData, type LandingRow } from '@/lib/publicApi'

type CourseOption = { id: string; code: string; title: string; status: string }
type CategoryOption = { id: string; name: string }

const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)
const move = <T,>(items: T[], index: number, by: -1 | 1): T[] => {
  const target = index + by
  if (target < 0 || target >= items.length) return items
  const next = [...items]; [next[index], next[target]] = [next[target], next[index]]
  return next
}

/** The first thing wrong with the page before it is sent, so the editor can say so next to the button. Mirrors the server. */
export function findProblem(content: LandingContent): string | null {
  if (!content.hero.title.trim()) return 'The main heading cannot be empty.'
  if (content.hero.primaryLabel.trim() && !isSafeLink(content.hero.primaryLink)) return 'The main button needs a link that starts with #, / or https://.'
  for (const banner of content.banners) if (banner.title.trim() && banner.buttonLabel.trim() && !isSafeLink(banner.link)) return `The banner “${banner.title.trim()}” has a button without a valid link (#, / or https://).`
  for (const item of content.intents.items) if (item.label.trim() && !isSafeLink(item.link)) return `The choice “${item.label.trim()}” needs a valid link.`
  for (const row of content.rows) {
    if (row.title.trim() && row.mode === 'category' && !row.categoryId) return `Choose a category for the row “${row.title.trim()}”.`
    if (row.title.trim() && row.mode === 'manual' && row.courseIds.length === 0) return `Choose at least one course for the row “${row.title.trim()}”.`
  }
  for (const group of content.footerGroups) for (const link of group.links) if (link.label.trim() && !isSafeLink(link.url)) return `The footer link “${link.label.trim()}” needs a valid address.`
  if (content.banners.length > 6) return 'A page can have at most 6 banners.'
  if (content.rows.length > 8) return 'A page can have at most 8 course rows.'
  if (content.faq.length > 12) return 'A page can have at most 12 questions.'
  return null
}

/** A list the editor can add to, remove from and reorder. */
function ListEditor<T>({ label, items, max, blank, onChange, render, addLabel }: { label: string; items: T[]; max: number; blank: () => T; onChange: (items: T[]) => void; render: (item: T, index: number, change: (next: T) => void) => React.ReactNode; addLabel: string }) {
  return (
    <div className="flex flex-col gap-3">
      <ul aria-label={label} className="flex flex-col gap-3">
        {items.map((item, index) => (
          <li key={index} className="flex flex-col gap-3 rounded-lg border border-border p-3">
            <div className="grid gap-3 sm:grid-cols-2">{render(item, index, (next) => onChange(items.map((current, at) => (at === index ? next : current))))}</div>
            <div className="flex gap-1.5">
              <Button type="button" size="sm" variant="outline" aria-label={`Move ${label} ${index + 1} up`} disabled={index === 0} onClick={() => onChange(move(items, index, -1))}><ArrowUp className="h-4 w-4" aria-hidden /></Button>
              <Button type="button" size="sm" variant="outline" aria-label={`Move ${label} ${index + 1} down`} disabled={index === items.length - 1} onClick={() => onChange(move(items, index, 1))}><ArrowDown className="h-4 w-4" aria-hidden /></Button>
              <Button type="button" size="sm" variant="softDestructive" aria-label={`Remove ${label} ${index + 1}`} onClick={() => onChange(items.filter((_, at) => at !== index))}><Trash2 className="mr-1 h-4 w-4" aria-hidden />Remove</Button>
            </div>
          </li>
        ))}
      </ul>
      {items.length < max ? <div><Button type="button" variant="outline" size="sm" onClick={() => onChange([...items, blank()])}><Plus className="mr-1 h-4 w-4" aria-hidden />{addLabel}</Button></div> : <small className="text-muted-foreground">The most you can have is {max}.</small>}
    </div>
  )
}

const text = (id: string, label: string, value: string, onChange: (value: string) => void, extra: { hint?: string; max?: number; area?: boolean; required?: boolean } = {}) => (
  <Field id={id} label={label} hint={extra.hint} required={extra.required} className={extra.area ? 'sm:col-span-2' : undefined}>
    {extra.area ? <Textarea id={id} rows={3} maxLength={extra.max} value={value} onChange={(event) => onChange(event.target.value)} /> : <Input id={id} maxLength={extra.max} value={value} onChange={(event) => onChange(event.target.value)} />}
  </Field>
)

const LINK_HINT = 'Use #courses, #faq, #login, #join, a path such as /?org=… or an https:// address.'

/** Where staff control the words, banners and course rows of the public front page. */
export default function LandingContentPage() {
  const [data, setData] = useState<LandingEditorData | null>(null)
  const [draft, setDraft] = useState<LandingContent | null>(null)
  const [saved, setSaved] = useState('')
  const [courses, setCourses] = useState<CourseOption[]>([])
  const [categories, setCategories] = useState<CategoryOption[]>([])
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const apply = useCallback((next: LandingEditorData) => { setData(next); setDraft(next.content); setSaved(JSON.stringify(next.content)) }, [])
  useEffect(() => {
    apiRequest<LandingEditorData>('/api/v1/tenant/landing').then(apply).catch((exception) => setError(readError(exception, 'Unable to load the landing page.')))
    apiRequest<CourseOption[]>('/api/v1/tenant/courses').then((list) => setCourses(Array.isArray(list) ? list.filter((course) => course.status === 'Published') : [])).catch(() => setCourses([]))
    apiRequest<CategoryOption[]>('/api/v1/tenant/catalog/categories').then((list) => setCategories(Array.isArray(list) ? list : [])).catch(() => setCategories([]))
  }, [apply])

  const dirty = useMemo(() => draft !== null && JSON.stringify(draft) !== saved, [draft, saved])
  if (!data || !draft) return <section className="flex flex-col gap-4"><PageHeader title="Landing page" description="Control what visitors see on your public front page." /><ErrorBanner message={error} />{error ? null : <p className="text-sm text-muted-foreground">Loading…</p>}</section>

  const set = (patch: Partial<LandingContent>) => { setDraft({ ...draft, ...patch }); setNotice(null) }

  async function save() {
    if (!draft) return
    const problem = findProblem(draft)
    if (problem) return setError(problem)
    setBusy(true); setError(null); setNotice(null)
    try { apply(await apiRequest<LandingEditorData>('/api/v1/tenant/landing', { method: 'PUT', body: JSON.stringify(draft) })); setNotice('Saved. Visitors see the new page now.') }
    catch (exception) { setError(readError(exception, 'Unable to save the landing page.')) }
    finally { setBusy(false) }
  }

  async function reset() {
    if (!window.confirm('Go back to the standard page? Everything you wrote on it is removed.')) return
    setBusy(true); setError(null); setNotice(null)
    try { apply(await apiRequest<LandingEditorData>('/api/v1/tenant/landing/reset', { method: 'POST' })); setNotice('The standard page is back.') }
    catch (exception) { setError(readError(exception, 'Unable to reset the landing page.')) }
    finally { setBusy(false) }
  }

  const rowEditor = (row: LandingRow, index: number, change: (next: LandingRow) => void) => (
    <>
      {text(`row-title-${index}`, 'Heading', row.title, (value) => change({ ...row, title: value }), { max: 120, required: true })}
      {text(`row-sub-${index}`, 'Line under the heading', row.subtitle ?? '', (value) => change({ ...row, subtitle: value || null }), { max: 200 })}
      <Field id={`row-mode-${index}`} label="Which courses">
        <Select id={`row-mode-${index}`} value={row.mode} onChange={(event) => change({ ...row, mode: event.target.value, categoryId: null, courseIds: [] })}>
          <option value="newest">The newest courses</option>
          <option value="popular">The most popular (most learners)</option>
          <option value="category">A category</option>
          <option value="manual">Courses I choose</option>
        </Select>
      </Field>
      <Field id={`row-limit-${index}`} label="How many to show"><Input id={`row-limit-${index}`} inputMode="numeric" value={String(row.limit)} onChange={(event) => change({ ...row, limit: Math.max(1, Math.min(24, Number(event.target.value.replace(/\D/g, '')) || 1)) })} /></Field>
      {row.mode === 'category' ? (
        <Field id={`row-cat-${index}`} label="Category" required>
          <Select id={`row-cat-${index}`} value={row.categoryId ?? ''} onChange={(event) => change({ ...row, categoryId: event.target.value || null })}>
            <option value="">Choose a category</option>
            {categories.map((category) => <option key={category.id} value={category.id}>{category.name}</option>)}
          </Select>
        </Field>
      ) : null}
      {row.mode === 'manual' ? (
        <fieldset className="sm:col-span-2"><legend className="mb-1 text-sm font-medium">Courses (published ones)</legend>
          {courses.length === 0 ? <small className="text-muted-foreground">There are no published courses yet.</small> : <ul className="grid max-h-48 gap-1 overflow-y-auto sm:grid-cols-2">{courses.map((course) => (
            <li key={course.id}><label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={row.courseIds.includes(course.id)} onChange={(event) => change({ ...row, courseIds: event.target.checked ? [...row.courseIds, course.id] : row.courseIds.filter((id) => id !== course.id) })} />{course.code} · {course.title}</label></li>
          ))}</ul>}
        </fieldset>
      ) : null}
    </>
  )

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title="Landing page" description="The public front page visitors see before they sign in: your words, banners, course rows, questions and footer."
        actions={<>
          <Button variant="outline" asChild><a href={`/?org=${data.slug}`} target="_blank" rel="noreferrer noopener"><ExternalLink className="mr-1.5 h-4 w-4" aria-hidden />View page</a></Button>
          <Button variant="outline" disabled={busy || data.isDefault} onClick={() => void reset()}><RotateCcw className="mr-1.5 h-4 w-4" aria-hidden />Use standard page</Button>
          <Button disabled={busy || !dirty} onClick={() => void save()}><Save className="mr-1.5 h-4 w-4" aria-hidden />Save changes</Button>
        </>} />
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />
      {data.isDefault ? <p className="rounded-md border border-border p-3 text-sm text-muted-foreground">Visitors now see the standard page. Change anything here and press Save changes to make it your own.</p> : null}
      {dirty ? <p role="status" className="text-sm text-muted-foreground">You have changes that are not saved yet.</p> : null}

      <Tabs defaultValue="top">
        <TabsList>
          <TabsTrigger value="top">Top of the page</TabsTrigger>
          <TabsTrigger value="rows">Course rows</TabsTrigger>
          <TabsTrigger value="why">Why learn with you</TabsTrigger>
          <TabsTrigger value="proof">Stories and questions</TabsTrigger>
          <TabsTrigger value="footer">Footer</TabsTrigger>
        </TabsList>

        <TabsContent value="top">
          <div className="flex flex-col gap-6">
            <FormSection title="Main heading" description="The first thing visitors read.">
              <div className="grid gap-3 sm:grid-cols-2">
                {text('hero-title', 'Heading', draft.hero.title, (value) => set({ hero: { ...draft.hero, title: value } }), { max: 200, required: true })}
                {text('hero-search', 'Search box hint', draft.hero.searchPlaceholder, (value) => set({ hero: { ...draft.hero, searchPlaceholder: value } }), { max: 80 })}
                {text('hero-sub', 'Text under the heading', draft.hero.subtitle, (value) => set({ hero: { ...draft.hero, subtitle: value } }), { max: 400, area: true })}
                {text('hero-button', 'Button label', draft.hero.primaryLabel, (value) => set({ hero: { ...draft.hero, primaryLabel: value } }), { max: 60, hint: 'Leave empty for no button.' })}
                {text('hero-link', 'Button link', draft.hero.primaryLink, (value) => set({ hero: { ...draft.hero, primaryLink: value } }), { hint: LINK_HINT })}
              </div>
            </FormSection>
            <FormSection title="Banners" description="Large messages that turn over at the top, such as a new course or an offer. Up to 6.">
              <ListEditor label="banner" items={draft.banners} max={6} addLabel="Add a banner" blank={() => ({ id: '', title: '', text: '', buttonLabel: '', link: '', theme: 'blue' })} onChange={(banners) => set({ banners })}
                render={(banner, index, change) => (<>
                  {text(`banner-title-${index}`, 'Title', banner.title, (value) => change({ ...banner, title: value }), { max: 200, required: true })}
                  <Field id={`banner-theme-${index}`} label="Colour"><Select id={`banner-theme-${index}`} value={banner.theme} onChange={(event) => change({ ...banner, theme: event.target.value })}>{data.themes.map((theme) => <option key={theme} value={theme}>{theme[0].toUpperCase() + theme.slice(1)}</option>)}</Select></Field>
                  {text(`banner-text-${index}`, 'Text', banner.text, (value) => change({ ...banner, text: value }), { max: 400, area: true })}
                  {text(`banner-button-${index}`, 'Button label', banner.buttonLabel, (value) => change({ ...banner, buttonLabel: value }), { max: 60 })}
                  {text(`banner-link-${index}`, 'Button link', banner.link, (value) => change({ ...banner, link: value }), { hint: LINK_HINT })}
                </>)} />
            </FormSection>
            <FormSection title="What brings people here" description="Quick choices under the banners. Each one scrolls to a part of the page or opens a link.">
              {text('intents-title', 'Heading', draft.intents.title, (value) => set({ intents: { ...draft.intents, title: value } }), { max: 120 })}
              <ListEditor label="choice" items={draft.intents.items} max={8} addLabel="Add a choice" blank={() => ({ label: '', link: '#courses' })} onChange={(items) => set({ intents: { ...draft.intents, items } })}
                render={(item, index, change) => (<>
                  {text(`intent-label-${index}`, 'Label', item.label, (value) => change({ ...item, label: value }), { max: 80, required: true })}
                  {text(`intent-link-${index}`, 'Link', item.link, (value) => change({ ...item, link: value }), { hint: LINK_HINT })}
                </>)} />
            </FormSection>
          </div>
        </TabsContent>

        <TabsContent value="rows">
          <div className="flex flex-col gap-6">
            <FormSection title="Course rows" description="Rows of course cards, such as “Most popular” or “New”. Only published courses appear. A row with nothing to show is left out. Up to 8.">
              <ListEditor label="row" items={draft.rows} max={8} addLabel="Add a row" blank={() => ({ id: '', title: '', subtitle: null, mode: 'newest', categoryId: null, courseIds: [], limit: 8 })} onChange={(rows) => set({ rows })} render={rowEditor} />
            </FormSection>
            <FormSection title="Categories" description="A grid of your course categories that visitors can click to filter the courses.">
              <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={draft.showCategories} onChange={(event) => set({ showCategories: event.target.checked })} />Show the categories</label>
              {text('categories-title', 'Heading', draft.categoriesTitle, (value) => set({ categoriesTitle: value }), { max: 120 })}
            </FormSection>
          </div>
        </TabsContent>

        <TabsContent value="why">
          <div className="flex flex-col gap-6">
            <FormSection title="Reasons to learn with you" description="Short cards with an icon. Up to 8.">
              {text('features-title', 'Heading', draft.featuresTitle, (value) => set({ featuresTitle: value }), { max: 120 })}
              <ListEditor label="reason" items={draft.features} max={8} addLabel="Add a reason" blank={() => ({ title: '', text: '', icon: 'star' })} onChange={(features) => set({ features })}
                render={(feature, index, change) => (<>
                  {text(`feature-title-${index}`, 'Title', feature.title, (value) => change({ ...feature, title: value }), { max: 100, required: true })}
                  <Field id={`feature-icon-${index}`} label="Icon"><Select id={`feature-icon-${index}`} value={feature.icon} onChange={(event) => change({ ...feature, icon: event.target.value })}>{data.icons.map((icon) => <option key={icon} value={icon}>{icon}</option>)}</Select></Field>
                  {text(`feature-text-${index}`, 'Text', feature.text, (value) => change({ ...feature, text: value }), { max: 300, area: true })}
                </>)} />
            </FormSection>
            <FormSection title="Numbers" description="Big figures such as “5,000 learners”. Only add numbers that are true. Up to 4.">
              <ListEditor label="number" items={draft.stats} max={4} addLabel="Add a number" blank={() => ({ value: '', label: '' })} onChange={(stats) => set({ stats })}
                render={(stat, index, change) => (<>
                  {text(`stat-value-${index}`, 'Number', stat.value, (value) => change({ ...stat, value }), { max: 20, required: true })}
                  {text(`stat-label-${index}`, 'What it counts', stat.label, (value) => change({ ...stat, label: value }), { max: 80, required: true })}
                </>)} />
            </FormSection>
          </div>
        </TabsContent>

        <TabsContent value="proof">
          <div className="flex flex-col gap-6">
            <FormSection title="Learner stories" description="Real words from your learners, with their permission. Up to 8.">
              {text('testimonials-title', 'Heading', draft.testimonialsTitle, (value) => set({ testimonialsTitle: value }), { max: 120 })}
              <ListEditor label="story" items={draft.testimonials} max={8} addLabel="Add a story" blank={() => ({ name: '', role: null, quote: '' })} onChange={(testimonials) => set({ testimonials })}
                render={(item, index, change) => (<>
                  {text(`story-name-${index}`, 'Name', item.name, (value) => change({ ...item, name: value }), { max: 80, required: true })}
                  {text(`story-role-${index}`, 'Role or course', item.role ?? '', (value) => change({ ...item, role: value || null }), { max: 120 })}
                  {text(`story-quote-${index}`, 'What they said', item.quote, (value) => change({ ...item, quote: value }), { max: 600, area: true, required: true })}
                </>)} />
            </FormSection>
            <FormSection title="Questions and answers" description="Shown as a list that opens. Up to 12.">
              {text('faq-title', 'Heading', draft.faqTitle, (value) => set({ faqTitle: value }), { max: 120 })}
              <ListEditor label="question" items={draft.faq} max={12} addLabel="Add a question" blank={() => ({ question: '', answer: '' })} onChange={(faq) => set({ faq })}
                render={(item, index, change) => (<>
                  {text(`faq-q-${index}`, 'Question', item.question, (value) => change({ ...item, question: value }), { max: 200, required: true, area: true })}
                  {text(`faq-a-${index}`, 'Answer', item.answer, (value) => change({ ...item, answer: value }), { max: 1500, required: true, area: true })}
                </>)} />
            </FormSection>
          </div>
        </TabsContent>

        <TabsContent value="footer">
          <FormSection title="Footer" description="The bottom of the page: a short description, columns of links, and a copyright line.">
            <div className="grid gap-3 sm:grid-cols-2">
              {text('footer-about', 'About you', draft.footerAbout, (value) => set({ footerAbout: value }), { max: 400, area: true })}
              {text('footer-copy', 'Copyright line', draft.copyright, (value) => set({ copyright: value }), { max: 200 })}
            </div>
            <ListEditor label="column" items={draft.footerGroups} max={5} addLabel="Add a column" blank={() => ({ title: '', links: [] })} onChange={(footerGroups) => set({ footerGroups })}
              render={(group, index, change) => (<>
                {text(`group-title-${index}`, 'Column title', group.title, (value) => change({ ...group, title: value }), { max: 80, required: true })}
                <div className="sm:col-span-2"><ListEditor label={`link in column ${index + 1}`} items={group.links} max={10} addLabel="Add a link" blank={() => ({ label: '', url: '' })} onChange={(links) => change({ ...group, links })}
                  render={(link, at, changeLink) => (<>
                    {text(`group-${index}-link-label-${at}`, 'Label', link.label, (value) => changeLink({ ...link, label: value }), { max: 80, required: true })}
                    {text(`group-${index}-link-url-${at}`, 'Address', link.url, (value) => changeLink({ ...link, url: value }), { hint: LINK_HINT })}
                  </>)} /></div>
              </>)} />
          </FormSection>
        </TabsContent>
      </Tabs>
    </section>
  )
}
