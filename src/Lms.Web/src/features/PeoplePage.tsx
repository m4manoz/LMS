import { useCallback, useEffect, useState } from 'react'
import CalendarDateField from '@/components/CalendarDateField'
import SidePanel from '@/components/SidePanel'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, PageHeader, RowList } from '@/components/form'
import { Badge } from '@/components/ui/badge'
import { Input } from '@/components/ui/input'
import { apiRequest } from '@/lib/api'
import { useAuth } from '@/lib/auth'

export type PeopleKind = 'learners' | 'instructors'
type Row = { userId: string; displayName: string; email: string; status: string; joinedAtUtc: string; detail1: string | null; detail2: string | null; count: number }
type LearnerCourse = { courseId: string; code: string; title: string; status: string; progressPercent: number; enrolledAtUtc: string; lastAccessedAtUtc: string | null; completedAtUtc: string | null }
type InstructorCourse = { courseId: string; code: string; title: string; status: string; learners: number }
type Profile = {
  userId: string; displayName: string; email: string; status: string; roles: string[]; joinedAtUtc: string
  learner: { studentNumber: string | null; gradeLevel: string | null; dateOfBirthAd: string | null } | null
  teacher: { employeeNumber: string | null; subjectSpecialty: string | null } | null
  summary: { activeCourses: number; completedCourses: number; averageProgressPercent: number; coursesTaught: number; learnersTaught: number }
  enrollments: LearnerCourse[]; courses: InstructorCourse[]
}

const copy = {
  learners: { title: 'Learners', description: 'Everyone enrolled as a learner, with their courses and progress.', one: 'learner', countLabel: (n: number) => `${n} ${n === 1 ? 'course' : 'courses'}`, detail: (row: Row) => [row.detail1, row.detail2].filter(Boolean).join(' · ') },
  instructors: { title: 'Instructors', description: 'Teachers and what they teach.', one: 'instructor', countLabel: (n: number) => `${n} ${n === 1 ? 'course' : 'courses'} owned`, detail: (row: Row) => [row.detail1, row.detail2].filter(Boolean).join(' · ') },
} as const

const readError = (exception: unknown, fallback: string) => (exception instanceof Error && exception.message ? exception.message : fallback)
const formatDate = (value: string | null) => (value ? new Date(value).toLocaleDateString() : '—')

/** The Learners and Instructors pages: a searchable directory, and one person's profile in a side panel. */
export default function PeoplePage({ kind }: { kind: PeopleKind }) {
  const { session } = useAuth()
  const canEdit = session?.permissions.includes('user.manage') ?? false
  const text = copy[kind]
  const [rows, setRows] = useState<Row[] | null>(null)
  const [q, setQ] = useState('')
  const [profile, setProfile] = useState<Profile | null>(null)
  const [editing, setEditing] = useState(false)
  const [form, setForm] = useState({ first: '', second: '', birth: '' })
  const [error, setError] = useState<string | null>(null)
  const [problem, setProblem] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try { setRows(await apiRequest<Row[]>(`/api/v1/tenant/people?kind=${kind}${q.trim() ? `&q=${encodeURIComponent(q.trim())}` : ''}`)); setError(null) }
    catch (exception) { setError(readError(exception, `Unable to load ${text.title.toLowerCase()}.`)) }
  }, [kind, q, text.title])
  useEffect(() => {
    const timer = window.setTimeout(() => { void load() }, 250)
    return () => window.clearTimeout(timer)
  }, [load])
  // Moving between Learners and Instructors starts fresh.
  useEffect(() => { setProfile(null); setEditing(false); setQ(''); setRows(null); setNotice(null) }, [kind])

  const open = async (userId: string) => {
    setBusy(true); setError(null); setNotice(null); setEditing(false)
    try { setProfile(await apiRequest<Profile>(`/api/v1/tenant/people/${userId}`)) }
    catch (exception) { setError(readError(exception, 'Unable to open the profile.')) }
    finally { setBusy(false) }
  }

  const startEdit = () => {
    if (!profile) return
    setProblem(null)
    setForm(kind === 'learners'
      ? { first: profile.learner?.studentNumber ?? '', second: profile.learner?.gradeLevel ?? '', birth: profile.learner?.dateOfBirthAd ?? '' }
      : { first: profile.teacher?.employeeNumber ?? '', second: profile.teacher?.subjectSpecialty ?? '', birth: '' })
    setEditing(true)
  }

  async function save(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!profile) return
    if (form.first.length > 80 || form.second.length > (kind === 'learners' ? 80 : 200)) { setProblem('One of the values is too long.'); return }
    setBusy(true); setProblem(null)
    try {
      const body = kind === 'learners'
        ? { kind: 'learner', studentNumber: form.first, gradeLevel: form.second, dateOfBirthAd: form.birth || null }
        : { kind: 'instructor', employeeNumber: form.first, subjectSpecialty: form.second }
      await apiRequest(`/api/v1/tenant/people/${profile.userId}/profile`, { method: 'PUT', body: JSON.stringify(body) })
      setNotice('Details saved.'); setEditing(false)
      setProfile(await apiRequest<Profile>(`/api/v1/tenant/people/${profile.userId}`)); await load()
    } catch (exception) { setProblem(readError(exception, 'Unable to save the details.')) }
    finally { setBusy(false) }
  }

  const close = () => { setProfile(null); setEditing(false) }
  const firstLabel = kind === 'learners' ? 'Student number' : 'Employee number'
  const secondLabel = kind === 'learners' ? 'Grade or level' : 'Subject'

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader title={text.title} description={text.description} />
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />
      <Input className="max-w-sm" type="search" placeholder={`Search ${text.title.toLowerCase()}`} aria-label={`Search ${text.title.toLowerCase()}`} value={q} onChange={(event) => setQ(event.target.value)} />

      {rows === null ? <p role="status" className="text-sm text-muted-foreground">Loading…</p>
        : rows.length === 0 ? <EmptyState>{q.trim() ? `No ${text.title.toLowerCase()} match “${q.trim()}”.` : `No ${text.title.toLowerCase()} yet.`}</EmptyState>
          : (
            <RowList label={text.title}>
              {rows.map((row) => (
                <ListRow key={row.userId} selected={profile?.userId === row.userId} columns="md:grid-cols-[minmax(0,2fr)_minmax(0,1.5fr)_140px_90px]">
                  <div className="min-w-0">
                    <button type="button" className="truncate text-left font-medium hover:underline" onClick={() => void open(row.userId)}>{row.displayName}</button>
                    <p className="truncate text-xs text-muted-foreground">{row.email}</p>
                  </div>
                  <span className="truncate text-xs text-muted-foreground">{text.detail(row) || '—'}</span>
                  <span className="text-xs text-muted-foreground">{text.countLabel(row.count)}</span>
                  <Badge variant={row.status === 'Active' ? 'default' : 'secondary'}>{row.status}</Badge>
                </ListRow>
              ))}
            </RowList>
          )}

      <SidePanel open={profile !== null} label={profile?.displayName ?? 'Profile'} onClose={close}>
        {profile ? (
          <div className="flex flex-col gap-6">
            <div>
              <h3 className="text-lg font-semibold">{profile.displayName}</h3>
              <p className="text-sm text-muted-foreground">{profile.email} · joined {formatDate(profile.joinedAtUtc)}</p>
              <p className="mt-1 text-xs text-muted-foreground">{profile.roles.join(', ')}</p>
            </div>

            <dl className="grid grid-cols-2 gap-3 text-sm sm:grid-cols-3">
              {kind === 'learners' ? (<>
                <div><dt className="text-muted-foreground">Active courses</dt><dd>{profile.summary.activeCourses}</dd></div>
                <div><dt className="text-muted-foreground">Completed</dt><dd>{profile.summary.completedCourses}</dd></div>
                <div><dt className="text-muted-foreground">Average progress</dt><dd>{profile.summary.averageProgressPercent}%</dd></div>
              </>) : (<>
                <div><dt className="text-muted-foreground">Courses</dt><dd>{profile.summary.coursesTaught}</dd></div>
                <div><dt className="text-muted-foreground">Learners</dt><dd>{profile.summary.learnersTaught}</dd></div>
              </>)}
            </dl>

            {editing ? (
              <FormLayout onSubmit={save} noValidate>
                <ErrorBanner message={problem} />
                <FormSection title="Details" divider={false}>
                  <Field id="profile-first" label={firstLabel}><Input id="profile-first" maxLength={80} value={form.first} onChange={(event) => setForm({ ...form, first: event.target.value })} /></Field>
                  <Field id="profile-second" label={secondLabel}><Input id="profile-second" maxLength={kind === 'learners' ? 80 : 200} value={form.second} onChange={(event) => setForm({ ...form, second: event.target.value })} /></Field>
                  {kind === 'learners' ? <CalendarDateField id="profile-birth" label="Date of birth" value={form.birth} onChange={(value) => setForm({ ...form, birth: value })} /> : null}
                </FormSection>
                <FormActions busy={busy} submitLabel="Save details" onCancel={() => setEditing(false)} />
              </FormLayout>
            ) : (
              <FormSection title="Details" divider={false}>
                <dl className="grid grid-cols-2 gap-3 text-sm">
                  <div><dt className="text-muted-foreground">{firstLabel}</dt><dd>{(kind === 'learners' ? profile.learner?.studentNumber : profile.teacher?.employeeNumber) || '—'}</dd></div>
                  <div><dt className="text-muted-foreground">{secondLabel}</dt><dd>{(kind === 'learners' ? profile.learner?.gradeLevel : profile.teacher?.subjectSpecialty) || '—'}</dd></div>
                  {kind === 'learners' && profile.learner?.dateOfBirthAd ? <div><dt className="text-muted-foreground">Date of birth</dt><dd>{profile.learner.dateOfBirthAd}</dd></div> : null}
                </dl>
                {canEdit ? <button type="button" className="self-start text-sm underline" onClick={startEdit}>Edit details</button> : null}
              </FormSection>
            )}

            {kind === 'learners' ? (
              <FormSection title="Courses">
                {profile.enrollments.length === 0 ? <p className="text-sm text-muted-foreground">Not enrolled in any course.</p> : (
                  <ul className="flex flex-col gap-2 text-sm">
                    {profile.enrollments.map((item) => (
                      <li key={item.courseId} className="flex items-center justify-between gap-3">
                        <span className="min-w-0 truncate">{item.title} <span className="text-xs text-muted-foreground">({item.code})</span></span>
                        <span className="shrink-0 text-xs text-muted-foreground">{item.status} · {item.progressPercent}%</span>
                      </li>
                    ))}
                  </ul>
                )}
              </FormSection>
            ) : (
              <FormSection title="Courses">
                {profile.courses.length === 0 ? <p className="text-sm text-muted-foreground">Does not own any course.</p> : (
                  <ul className="flex flex-col gap-2 text-sm">
                    {profile.courses.map((item) => (
                      <li key={item.courseId} className="flex items-center justify-between gap-3">
                        <span className="min-w-0 truncate">{item.title} <span className="text-xs text-muted-foreground">({item.code})</span></span>
                        <span className="shrink-0 text-xs text-muted-foreground">{item.status} · {item.learners} {item.learners === 1 ? 'learner' : 'learners'}</span>
                      </li>
                    ))}
                  </ul>
                )}
              </FormSection>
            )}
          </div>
        ) : null}
      </SidePanel>
    </section>
  )
}
