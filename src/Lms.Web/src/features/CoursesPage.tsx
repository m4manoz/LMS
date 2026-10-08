import { useEffect, useState, useRef } from 'react'
import { ArrowDown, ArrowUp, Plus } from 'lucide-react'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import SidePanel from '../components/SidePanel'
import { ErrorBanner, NoticeBanner } from '@/components/form'
import CourseContentEditor from './CourseContentEditor'
import { ReadinessChecklist } from './ReadinessChecklist'
import CourseEnrollmentPanel from './CourseEnrollmentPanel'
import CourseReviewsPanel from './CourseReviewsPanel'
import CourseLiveClasses from './CourseLiveClasses'
import CourseForm from './CourseForm'
import CourseList from './CourseList'
import CourseRulesPanel from './CourseRulesPanel'
import CourseVersionPanel from './CourseVersionPanel'
import { countLessons, emptyForm, formFrom, isReady, readiness, statusLabel, statusVariant, toPayload, type CategoryOption, type CourseDetail, type CourseFormValues, type Lesson, type Module } from './courseAuthoring'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { ApiError, apiRequest } from '../lib/api'
import { moveItem } from '../lib/order'
import { useAuth } from '../lib/auth'

export default function CoursesPage({ mode = 'catalog', onOpenClass }: { mode?: 'catalog' | 'authoring'; onOpenClass?: (sessionId: string) => void }) {
  const { session } = useAuth()
  const [courses, setCourses] = useState<CourseDetail['course'][]>([])
  const [selected, setSelected] = useState<CourseDetail | null>(null)
  const [categories, setCategories] = useState<CategoryOption[]>([])
  const [creating, setCreating] = useState(false)
  const [moduleTitle, setModuleTitle] = useState('')
  const [lessonTitles, setLessonTitles] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [tab, setTab] = useState('overview')
  // Which copy of a published course is on screen: the live one learners use, or the new version being written.
  const [viewDraft, setViewDraft] = useState(false)
  // Counts how many times the panel was closed. A response that arrives after the panel was closed must not open it again.
  const closes = useRef(0)
  const canManage = session?.permissions.includes('course.manage') ?? false
  const canReview = session?.permissions.includes('course.review') ?? false
  const canPublish = session?.permissions.includes('course.publish') ?? false
  // People who manage enrollment get the Enrollment tab wherever they open a course, not only under Course authoring.
  const canEnroll = session?.permissions.includes('enrollment.manage') ?? false

  useEffect(() => { void loadCourses(); void loadCategories() }, [session?.accessToken])

  async function loadCategories() {
    try { const result = await apiRequest<CategoryOption[]>('/api/v1/tenant/catalog/categories'); setCategories(Array.isArray(result) ? result : []) }
    catch { setCategories([]) } // categories are a convenience; the page works without them
  }

  async function loadCourses() {
    try {
      setError(null)
      const closedBefore = closes.current
      const result = await apiRequest<CourseDetail['course'][]>('/api/v1/tenant/courses')
      setCourses(result)
      if (closes.current !== closedBefore) return // the panel was closed while the list was loading: leave it closed
      if (selected && result.some((course) => course.id === selected.course.id)) await openCourse(selected.course.id)
      else setSelected(null) // nothing is opened until someone asks for it
    } catch (exception) { setError(readError(exception, 'Unable to load courses.')) }
  }

  async function openCourse(courseId: string, draft = viewDraft && selected?.course.id === courseId) {
    try {
      const closedBefore = closes.current
      const detail = await apiRequest<CourseDetail>(`/api/v1/tenant/courses/${courseId}${draft ? '?version=draft' : ''}`)
      if (closes.current !== closedBefore) return // closed while loading
      setSelected(detail); setViewDraft(detail.viewingDraft === true)
    } catch (exception) {
      // The new version may have been discarded or published since; fall back to the live course.
      if (draft && exception instanceof ApiError && exception.status === 404) { await openCourse(courseId, false); return }
      setError(readError(exception, 'Unable to load the course.'))
    }
  }

  const closePanel = () => { closes.current += 1; setCreating(false); setSelected(null); setViewDraft(false); setNotice(null); setTab('overview') }
  const choose = (courseId: string) => { setCreating(false); setNotice(null); void openCourse(courseId, false) }

  async function createCourse(values: CourseFormValues) {
    setBusy(true); setError(null); setNotice(null)
    try {
      const result = await apiRequest<CourseDetail>('/api/v1/tenant/courses', { method: 'POST', body: JSON.stringify(toPayload(values)) })
      setCreating(false); setSelected(result); setViewDraft(false); setTab('outline'); setNotice('Course created. Add its modules and lessons next.')
      setCourses(await apiRequest<CourseDetail['course'][]>('/api/v1/tenant/courses')) // refresh the list only; the new course stays open
    } catch (exception) { setError(readError(exception, 'Unable to create the course.')) }
    finally { setBusy(false) }
  }

  async function saveDetails(values: CourseFormValues) {
    if (!selected) return
    setBusy(true); setError(null); setNotice(null)
    try {
      const { title, description, startDateAd, endDateAd, capacity, categoryId } = toPayload(values) // the code is fixed once a course exists
      const body = { title, description, startDateAd, endDateAd, capacity, categoryId }
      const result = await apiRequest<CourseDetail>(`/api/v1/tenant/courses/${selected.course.id}`, { method: 'PUT', body: JSON.stringify(body) })
      setSelected(result); setNotice('Details saved.')
      setCourses(await apiRequest<CourseDetail['course'][]>('/api/v1/tenant/courses'))
    } catch (exception) { setError(readError(exception, 'Unable to save the details.')) }
    finally { setBusy(false) }
  }

  async function courseAction(action: 'submit-review' | 'publish' | 'archive') {
    if (!selected) return
    setBusy(true); setError(null); setNotice(null)
    try {
      const detail = await apiRequest<CourseDetail>(`/api/v1/tenant/courses/${selected.course.id}/${action}`, { method: 'POST' })
      setSelected(detail); setViewDraft(detail.viewingDraft === true); await loadCourses()
    } catch (exception) { setError(readError(exception, `Unable to ${action.replace('-', ' ')} the course.`)) }
    finally { setBusy(false) }
  }

  async function startVersion(changeSummary: string) {
    if (!selected) return
    setBusy(true); setError(null)
    try {
      const detail = await apiRequest<CourseDetail>(`/api/v1/tenant/courses/${selected.course.id}/versions`, { method: 'POST', body: JSON.stringify({ changeSummary }) })
      setSelected(detail); setViewDraft(true); setTab('outline')
    } catch (exception) { setError(readError(exception, 'Unable to start a new version.')) }
    finally { setBusy(false) }
  }

  async function discardVersion() {
    if (!selected) return
    setBusy(true); setError(null)
    try {
      const detail = await apiRequest<CourseDetail>(`/api/v1/tenant/courses/${selected.course.id}/versions/draft`, { method: 'DELETE' })
      setSelected(detail); setViewDraft(false)
    } catch (exception) { setError(readError(exception, 'Unable to discard the version.')) }
    finally { setBusy(false) }
  }

  /** Runs an edit to the outline, then shows the course as it now stands. */
  async function change(action: () => Promise<unknown>, failure: string) {
    if (!selected) return
    setBusy(true); setError(null)
    try { await action(); await openCourse(selected.course.id) }
    catch (exception) { setError(readError(exception, failure)) }
    finally { setBusy(false) }
  }
  const courseBase = () => `/api/v1/tenant/courses/${selected!.course.id}`
  const moveModule = (index: number, delta: number) => change(() => apiRequest(`${courseBase()}/modules/order`, { method: 'PUT', body: JSON.stringify({ ids: moveItem(selected!.modules, index, delta).map((item) => item.id) }) }), 'Unable to reorder the modules.')
  const moveLesson = (module: Module, index: number, delta: number) => change(() => apiRequest(`${courseBase()}/modules/${module.id}/lessons/order`, { method: 'PUT', body: JSON.stringify({ ids: moveItem(module.lessons, index, delta).map((item) => item.id) }) }), 'Unable to reorder the lessons.')
  const deleteModule = (module: Module) => { if (window.confirm(`Delete the module "${module.title}" and its ${module.lessons.length} lesson(s)? This cannot be undone.`)) void change(() => apiRequest(`${courseBase()}/modules/${module.id}`, { method: 'DELETE' }), 'Unable to delete the module.') }
  const deleteLesson = (lesson: Lesson) => { if (window.confirm(`Delete the lesson "${lesson.title}" and its content? This cannot be undone.`)) void change(() => apiRequest(`${courseBase()}/lessons/${lesson.id}`, { method: 'DELETE' }), 'Unable to delete the lesson.') }

  async function addModule(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (!selected || !moduleTitle.trim()) return
    setBusy(true); setError(null)
    try {
      await apiRequest(`/api/v1/tenant/courses/${selected.course.id}/modules`, { method: 'POST', body: JSON.stringify({ title: moduleTitle }) })
      setModuleTitle(''); await openCourse(selected.course.id)
    } catch (exception) { setError(readError(exception, 'Unable to add the module.')) }
    finally { setBusy(false) }
  }

  async function addLesson(event: React.FormEvent<HTMLFormElement>, moduleId: string) {
    event.preventDefault(); if (!selected || !lessonTitles[moduleId]?.trim()) return
    setBusy(true); setError(null)
    try {
      await apiRequest(`/api/v1/tenant/courses/${selected.course.id}/modules/${moduleId}/lessons`, { method: 'POST', body: JSON.stringify({ title: lessonTitles[moduleId] }) })
      setLessonTitles((current) => ({ ...current, [moduleId]: '' })); await openCourse(selected.course.id)
    } catch (exception) { setError(readError(exception, 'Unable to add the lesson.')) }
    finally { setBusy(false) }
  }

  const authoring = mode === 'authoring'
  const panelOpen = creating || selected !== null
  const status = selected?.course.status
  const editable = authoring && canManage && (status === 'Draft' || (selected?.viewingDraft === true && selected.draftVersion?.status === 'Draft'))
  const checks = selected ? readiness(selected.modules, selected.course.description) : []
  const ready = isReady(checks)

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h2 className="text-2xl font-semibold">{authoring ? 'Course authoring' : 'Course catalog'}</h2>
          <p className="text-sm text-muted-foreground">{authoring ? 'Create courses, build the outline and move them through review and publication.' : 'Browse available courses and their outlines.'}</p>
        </div>
        <div className="flex items-center gap-3">
          <span className="text-sm text-muted-foreground">{courses.length} course{courses.length === 1 ? '' : 's'}</span>
          {authoring && canManage ? <Button onClick={() => { setCreating(true); setNotice(null) }}><Plus className="mr-1 h-4 w-4" aria-hidden />New course</Button> : null}
        </div>
      </div>
      {/* While the panel is open the page is dimmed behind it, so the messages are shown inside the panel instead. */}
      {panelOpen ? null : <ErrorBanner message={error} />}
      {panelOpen ? null : <NoticeBanner message={notice} />}

      <CourseList courses={courses} categories={categories} selectedId={creating ? undefined : selected?.course.id} showFilters={authoring} onSelect={choose} />

      <SidePanel open={panelOpen} label={creating ? 'New course' : 'Course details'} onClose={closePanel}>
        <div className="mb-4 flex flex-col gap-2 empty:hidden"><ErrorBanner message={error} /><NoticeBanner message={notice} /></div>
        {creating ? (
          <Card>
            <CardHeader><CardTitle>New course</CardTitle><CardDescription>Start with the basics. It begins as a draft; you build the outline next.</CardDescription></CardHeader>
            <CardContent><CourseForm initial={emptyForm()} categories={categories} creating busy={busy} submitLabel="Create draft course" onSubmit={(values) => void createCourse(values)} onCancel={() => setCreating(false)} /></CardContent>
          </Card>
        ) : !selected ? null : (
          <div className="flex min-w-0 flex-col gap-4">
            <header className="flex flex-wrap items-start justify-between gap-3 border-b border-border pb-4">
              <div className="min-w-0">
                <p className="text-xs text-muted-foreground">{selected.course.code} · version {(selected.viewingDraft ? selected.draftVersion : selected.currentVersion)?.versionNumber ?? 1}</p>
                <div className="flex flex-wrap items-center gap-2"><h3 className="text-xl font-semibold">{selected.course.title}</h3><Badge variant={statusVariant(selected.course.status)}>{statusLabel(selected.course.status)}</Badge></div>
              </div>
              {authoring ? (
                <div className="flex flex-wrap items-center gap-2">
                  {status === 'Draft' && canReview ? <Button disabled={busy || !ready} title={ready ? undefined : 'Finish the checklist on the Overview tab first.'} onClick={() => void courseAction('submit-review')}>Submit for review</Button> : null}
                  {status === 'InReview' && canPublish ? <Button disabled={busy} onClick={() => void courseAction('publish')}>Publish course</Button> : null}
                  {status === 'Published' && !selected.draftVersion && canManage ? <Button variant="secondary" onClick={() => setTab('workflow')}>Edit with a new version</Button> : null}
                  {status === 'Published' && selected.draftVersion && !selected.viewingDraft ? <Button variant="secondary" onClick={() => void openCourse(selected.course.id, true)}>Open version {selected.draftVersion.versionNumber}</Button> : null}
                </div>
              ) : null}
            </header>

            {authoring && selected.draftVersion ? (
              <div className="flex flex-wrap items-center gap-2 text-sm" role="group" aria-label="Version on screen">
                <span className="text-muted-foreground">Showing</span>
                <Button size="sm" variant={selected.viewingDraft ? 'outline' : 'secondary'} aria-pressed={!selected.viewingDraft} onClick={() => void openCourse(selected.course.id, false)}>Live · version {selected.currentVersion?.versionNumber ?? 1}</Button>
                <Button size="sm" variant={selected.viewingDraft ? 'secondary' : 'outline'} aria-pressed={selected.viewingDraft === true} onClick={() => void openCourse(selected.course.id, true)}>New · version {selected.draftVersion.versionNumber} ({selected.draftVersion.status === 'InReview' ? 'in review' : 'draft'})</Button>
              </div>
            ) : null}

            <Tabs value={tab} onValueChange={setTab}>
              <TabsList>
                <TabsTrigger value="overview">Overview</TabsTrigger>
                {authoring ? <TabsTrigger value="details">Details</TabsTrigger> : null}
                <TabsTrigger value="outline">Outline</TabsTrigger>
                <TabsTrigger value="classes">Live classes</TabsTrigger>
                {authoring ? <TabsTrigger value="content">Content</TabsTrigger> : null}
                {authoring || canEnroll ? <TabsTrigger value="enrollment">Enrollment</TabsTrigger> : null}
                {authoring ? <TabsTrigger value="reviews">Ratings</TabsTrigger> : null}
                {authoring ? <TabsTrigger value="rules">Access rules</TabsTrigger> : null}
                {authoring ? <TabsTrigger value="workflow">Review and publish</TabsTrigger> : null}
              </TabsList>

              <TabsContent value="overview">
                <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
                  <Stat label="Modules" value={String(selected.modules.length)} />
                  <Stat label="Lessons" value={String(countLessons(selected.modules))} />
                  <Stat label="Capacity" value={selected.course.capacity ? String(selected.course.capacity) : 'Unlimited'} />
                  <Stat label="Available" value={selected.course.startDateAd || selected.course.endDateAd ? `${selected.course.startDateAd ?? 'any date'} → ${selected.course.endDateAd ?? 'open ended'}` : 'Any time'} />
                </div>
                <Card>
                  <CardHeader><CardTitle>About this course</CardTitle></CardHeader>
                  <CardContent><p className="mb-2 text-sm text-muted-foreground">Category: {selected.course.categoryName ?? 'None'}</p><p className="whitespace-pre-wrap text-sm">{selected.course.description || <span className="text-muted-foreground">No description has been added.</span>}</p></CardContent>
                </Card>
                {authoring && status === 'Draft' ? (
                  <Card>
                    <CardHeader><CardTitle>Ready for review?</CardTitle><CardDescription>{ready ? 'Everything required is in place. You can submit it for review.' : 'Finish the required items before submitting.'}</CardDescription></CardHeader>
                    <CardContent><ReadinessChecklist checks={checks} onGo={setTab} /></CardContent>
                  </Card>
                ) : null}
              </TabsContent>

              {authoring ? (
                <TabsContent value="details">
                  <Card>
                    <CardHeader>
                      <CardTitle>Course details</CardTitle>
                      <CardDescription>{status === 'Draft' && canManage ? 'Changes apply as soon as you save.' : 'Details can only be changed while the course is a draft.'}</CardDescription>
                    </CardHeader>
                    <CardContent><CourseForm key={`${selected.course.id}-${selected.course.title}-${selected.course.description}-${selected.course.categoryId}`} initial={formFrom(selected.course)} categories={categories} creating={false} busy={busy} disabled={!(status === 'Draft' && canManage)} submitLabel="Save details" onSubmit={(values) => void saveDetails(values)} /></CardContent>
                  </Card>
                </TabsContent>
              ) : null}

              <TabsContent value="outline">
                <Card>
                  <CardHeader>
                    <CardTitle>Course outline</CardTitle>
                    <CardDescription>{editable ? (selected.viewingDraft ? `Editing version ${selected.draftVersion?.versionNumber}. Learners keep the live version until you publish this one.` : 'Add, reorder and remove modules and lessons while the course is a draft.') : 'Modules and lessons in this course.'}</CardDescription>
                  </CardHeader>
                  <CardContent className="flex flex-col gap-4">
                    {editable ? (
                      <form className="flex gap-2" onSubmit={addModule}>
                        <Input value={moduleTitle} onChange={(e) => setModuleTitle(e.target.value)} placeholder="New module title" aria-label="New module title" required />
                        <Button variant="secondary" type="submit" disabled={busy}>Add module</Button>
                      </form>
                    ) : null}
                    {selected.modules.length === 0 ? <p className="text-sm text-muted-foreground">No modules yet.</p> : selected.modules.map((module, moduleIndex) => (
                      <div key={module.id} className="rounded-md border border-border p-3">
                        <div className="mb-2 flex items-center justify-between gap-3">
                          <div className="flex items-center gap-3">
                            <span className="flex h-7 w-7 items-center justify-center rounded-full bg-muted text-xs">{module.displayOrder}</span>
                            <div><strong className="block text-sm">{module.title}</strong><small className="text-muted-foreground">{module.lessons.length} lesson{module.lessons.length === 1 ? '' : 's'}</small></div>
                          </div>
                          {editable ? <OutlineControls label={`module ${module.title}`} busy={busy} first={moduleIndex === 0} last={moduleIndex === selected.modules.length - 1} onUp={() => void moveModule(moduleIndex, -1)} onDown={() => void moveModule(moduleIndex, 1)} onDelete={() => deleteModule(module)} /> : null}
                        </div>
                        {module.lessons.map((lesson, lessonIndex) => (
                          <div key={lesson.id} className="flex items-center justify-between gap-3 py-1 pl-10 text-sm text-muted-foreground">
                            <span>↳ {lesson.title}</span>
                            {editable ? <OutlineControls label={`lesson ${lesson.title}`} busy={busy} first={lessonIndex === 0} last={lessonIndex === module.lessons.length - 1} onUp={() => void moveLesson(module, lessonIndex, -1)} onDown={() => void moveLesson(module, lessonIndex, 1)} onDelete={() => deleteLesson(lesson)} /> : null}
                          </div>
                        ))}
                        {editable ? (
                          <form className="mt-2 flex gap-2 pl-10" onSubmit={(event) => void addLesson(event, module.id)}>
                            <Input value={lessonTitles[module.id] || ''} onChange={(e) => setLessonTitles((current) => ({ ...current, [module.id]: e.target.value }))} placeholder="New lesson title" aria-label={`New lesson title for ${module.title}`} required />
                            <Button variant="secondary" type="submit" disabled={busy}>Add lesson</Button>
                          </form>
                        ) : null}
                      </div>
                    ))}
                  </CardContent>
                </Card>
              </TabsContent>

              <TabsContent value="classes">
                <CourseLiveClasses key={selected.course.id} courseId={selected.course.id} onOpen={onOpenClass} canSchedule={authoring} />
              </TabsContent>

              {authoring ? (
                <TabsContent value="content">
                  <CourseContentEditor key={`${selected.course.id}-${selected.viewingDraft ? 'new' : 'live'}`} courseId={selected.course.id} modules={selected.modules} editable={editable} />
                </TabsContent>
              ) : null}

              {authoring || canEnroll ? (
                <TabsContent value="enrollment">
                  <CourseEnrollmentPanel key={selected.course.id} courseId={selected.course.id} published={selected.course.status === 'Published'} />
                </TabsContent>
              ) : null}

              {authoring ? (
                <TabsContent value="reviews">
                  <CourseReviewsPanel key={selected.course.id} courseId={selected.course.id} />
                </TabsContent>
              ) : null}

              {authoring ? (
                <TabsContent value="rules">
                  <CourseRulesPanel key={selected.course.id} courseId={selected.course.id} />
                </TabsContent>
              ) : null}

              {authoring ? (
                <TabsContent value="workflow">
                  <CourseVersionPanel courseStatus={selected.course.status} current={selected.currentVersion} draft={selected.draftVersion} canManage={canManage} canReview={canReview} canPublish={canPublish} busy={busy}
                    onStart={(summary) => void startVersion(summary)} onSubmit={() => void courseAction('submit-review')} onPublish={() => void courseAction('publish')} onDiscard={() => void discardVersion()} />
                  <Card>
                    <CardHeader>
                      <CardTitle>Course status</CardTitle>
                      <CardDescription>Draft → In review → Published → Archived. Each change is recorded in the history below.</CardDescription>
                    </CardHeader>
                    <CardContent className="flex flex-wrap gap-2">
                      {status === 'Draft' && canReview ? <Button variant="secondary" disabled={busy || !ready} onClick={() => void courseAction('submit-review')}>Submit for review</Button> : null}
                      {status === 'InReview' && canPublish ? <Button disabled={busy} onClick={() => void courseAction('publish')}>Publish course</Button> : null}
                      {status !== 'Archived' && canManage ? <Button variant="softDestructive" disabled={busy} onClick={() => { if (window.confirm(`Archive "${selected.course.title}"? Learners lose access to it.`)) void courseAction('archive') }}>Archive</Button> : null}
                    </CardContent>
                  </Card>
                  <Card>
                    <CardHeader><CardTitle>Workflow history</CardTitle></CardHeader>
                    <CardContent className="flex flex-col gap-3">
                      {!selected.workflow || selected.workflow.length === 0 ? <p className="text-sm text-muted-foreground">No workflow events yet.</p> : selected.workflow.map((event) => (
                        <div key={event.id} className="border-l-2 border-border pl-3 text-sm">
                          <strong>{event.eventType.replaceAll('_', ' ')}</strong>
                          <small className="block text-muted-foreground">{event.notes || `${event.fromStatus || 'new'} → ${event.toStatus}`} · {new Date(event.createdAtUtc).toLocaleString()}</small>
                        </div>
                      ))}
                    </CardContent>
                  </Card>
                </TabsContent>
              ) : null}
            </Tabs>
          </div>
        )}
      </SidePanel>
    </section>
  )
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-md border border-border px-4 py-3">
      <p className="text-xs text-muted-foreground">{label}</p>
      <p className="mt-0.5 text-lg font-semibold leading-tight">{value}</p>
    </div>
  )
}

function OutlineControls({ label, busy, first, last, onUp, onDown, onDelete }: { label: string; busy: boolean; first: boolean; last: boolean; onUp: () => void; onDown: () => void; onDelete: () => void }) {
  return (
    <span className="flex shrink-0 items-center gap-1">
      <Button type="button" variant="ghost" size="sm" disabled={busy || first} aria-label={`Move ${label} up`} onClick={onUp}><ArrowUp className="h-4 w-4" /></Button>
      <Button type="button" variant="ghost" size="sm" disabled={busy || last} aria-label={`Move ${label} down`} onClick={onDown}><ArrowDown className="h-4 w-4" /></Button>
      <Button type="button" variant="softDestructive" size="sm" disabled={busy} aria-label={`Delete ${label}`} onClick={onDelete}>Delete</Button>
    </span>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
