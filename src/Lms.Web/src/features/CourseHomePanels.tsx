import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/form'
import { apiRequest } from '@/lib/api'

// ---------- coming up ----------
export type TaskItem = { id: string; kind: string; title: string; courseTitle: string | null; startAtUtc: string | null; dueAtUtc: string | null; status: string; target: string }

/** Which tab of the course a kind of item is dealt with in. */
export const tabForKind = (kind: string): string => (kind === 'assignment' ? 'assignments' : kind === 'assessment' ? 'assessments' : kind === 'live-class' ? 'live' : 'overview')

/** The things a learner still has to do in the course, soonest first: late work first, then by date, undated last. Finished work is left out. */
export function comingUp(items: TaskItem[]): TaskItem[] {
  const open = items.filter((item) => ['Todo', 'Overdue', 'InProgress', 'Scheduled', 'Live'].includes(item.status))
  const when = (item: TaskItem) => new Date(item.dueAtUtc ?? item.startAtUtc ?? '9999-12-31').getTime()
  const rank = (item: TaskItem) => (item.status === 'Overdue' ? 0 : item.status === 'Live' ? 1 : 2)
  return [...open].sort((a, b) => rank(a) - rank(b) || when(a) - when(b))
}

const kindLabel: Record<string, string> = { assignment: 'Assignment', assessment: 'Assessment', 'live-class': 'Live class' }
const statusLabel = (status: string) => (status === 'Todo' ? 'To do' : status === 'InProgress' ? 'In progress' : status)

/** Deadlines and classes of this course, each with a button to the tab where it is dealt with. */
export function CourseComingUp({ courseId, onGo }: { courseId: string; onGo: (tab: string) => void }) {
  const [items, setItems] = useState<TaskItem[] | null>(null)
  useEffect(() => {
    let active = true
    const from = new Date(Date.now() - 14 * 86_400_000).toISOString()
    const to = new Date(Date.now() + 90 * 86_400_000).toISOString()
    apiRequest<{ items: TaskItem[] }>(`/api/v1/tenant/tasks?courseId=${courseId}&from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`)
      .then((feed) => { if (active) setItems(Array.isArray(feed?.items) ? feed.items : []) }).catch(() => { if (active) setItems([]) })
    return () => { active = false }
  }, [courseId])
  if (items === null) return null
  const next = comingUp(items).slice(0, 8)
  return (
    <section aria-label="Coming up" className="flex flex-col gap-2 rounded-md border border-border p-4">
      <h3 className="text-base font-semibold">Coming up</h3>
      {next.length === 0 ? <p className="text-sm text-muted-foreground">Nothing is waiting for you in this course.</p> : (
        <ul className="flex flex-col gap-2">
          {next.map((item) => (
            <li key={item.id} className="flex flex-wrap items-center justify-between gap-2 text-sm">
              <span className="min-w-0"><strong className="block truncate">{item.title}</strong>
                <small className="text-muted-foreground">{kindLabel[item.kind] ?? item.kind}{item.dueAtUtc || item.startAtUtc ? ` · ${new Date((item.dueAtUtc ?? item.startAtUtc)!).toLocaleString()}` : ''}</small></span>
              <span className="flex items-center gap-2"><Badge variant={item.status === 'Overdue' || item.status === 'Live' ? 'destructive' : 'secondary'}>{statusLabel(item.status)}</Badge>
                <Button type="button" size="sm" variant="outline" aria-label={`Open ${item.title}`} onClick={() => onGo(tabForKind(item.kind))}>Open</Button></span>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}

// ---------- announcements ----------
export type CourseAnnouncement = { id: string; title: string; body: string; isPinned: boolean; courseId: string | null; courseTitle: string | null; authorName: string; createdAtUtc: string }

/** The course's announcements and the organization's own, pinned first. `limit` shows only the first few. */
export function CourseAnnouncements({ courseId, limit }: { courseId: string; limit?: number }) {
  const [items, setItems] = useState<CourseAnnouncement[] | null>(null)
  useEffect(() => {
    let active = true
    apiRequest<CourseAnnouncement[]>(`/api/v1/tenant/community/announcements?courseId=${courseId}`).then((list) => { if (active) setItems(Array.isArray(list) ? list : []) }).catch(() => { if (active) setItems([]) })
    return () => { active = false }
  }, [courseId])
  if (items === null) return null
  const shown = limit ? items.slice(0, limit) : items
  return (
    <section aria-label="Announcements" className="flex flex-col gap-2 rounded-md border border-border p-4">
      <h3 className="text-base font-semibold">Announcements</h3>
      {shown.length === 0 ? <p className="text-sm text-muted-foreground">No announcements.</p> : (
        <ul className="flex flex-col gap-3">
          {shown.map((item) => (
            <li key={item.id} className="text-sm">
              <p className="flex flex-wrap items-center gap-2"><strong>{item.title}</strong>{item.isPinned ? <Badge variant="secondary">Pinned</Badge> : null}{item.courseId ? null : <Badge variant="outline">Whole organization</Badge>}</p>
              <p className="whitespace-pre-wrap text-muted-foreground">{item.body}</p>
              <small className="text-muted-foreground">{item.authorName} · {new Date(item.createdAtUtc).toLocaleDateString()}</small>
            </li>
          ))}
        </ul>
      )}
      {limit && items.length > limit ? <small className="text-muted-foreground">{items.length - limit} more in the Discussion tab.</small> : null}
    </section>
  )
}

// ---------- grades and certificate ----------
type GradeRow = { kind: string; title: string; status: string; score: number | null; maxPoints: number; percent: number | null; isLate: boolean; feedback: string | null }
type CourseGradesData = { courseId: string; rows: GradeRow[]; earnedPoints: number; possiblePoints: number; overallPercent: number | null; letter?: string | null; passed?: boolean | null }
type CertificateData = { id: string; certificateNumber: string; courseTitle: string; scorePercentage?: number | null; issuedAtUtc: string; revokedAtUtc?: string | null; verificationPath: string }

export const percentText = (value: number | null | undefined) => (value === null || value === undefined ? '—' : `${Math.round(value)}%`)

/** The learner's grades in this course, row by row, and the certificate once one is issued. */
export function CourseGrades({ courseId }: { courseId: string }) {
  const [grades, setGrades] = useState<CourseGradesData | null | undefined>(undefined)
  const [certificates, setCertificates] = useState<CertificateData[] | null>(null)
  useEffect(() => {
    let active = true
    apiRequest<CourseGradesData[]>(`/api/v1/tenant/gradebook/me?courseId=${courseId}`).then((list) => { if (active) setGrades(Array.isArray(list) ? list[0] ?? null : null) }).catch(() => { if (active) setGrades(null) })
    apiRequest<CertificateData[]>(`/api/v1/tenant/certificates?courseId=${courseId}`).then((list) => { if (active) setCertificates(Array.isArray(list) ? list : []) }).catch(() => { if (active) setCertificates([]) })
    return () => { active = false }
  }, [courseId])
  return (
    <div className="flex flex-col gap-4">
      <section aria-label="My grades" className="flex flex-col gap-3 rounded-md border border-border p-4">
        <h3 className="text-base font-semibold">My grades</h3>
        {grades === undefined ? null : grades === null ? <EmptyState>Grades appear here once the course is published and you have work marked.</EmptyState> : (
          <>
            <p className="flex flex-wrap items-center gap-3 text-sm">
              <strong className="text-lg">{percentText(grades.overallPercent)}</strong>
              {grades.letter ? <Badge>{grades.letter}</Badge> : null}
              {grades.passed === true ? <Badge variant="default">Passing</Badge> : grades.passed === false ? <Badge variant="destructive">Not passing yet</Badge> : null}
              <span className="text-muted-foreground">{grades.earnedPoints} of {grades.possiblePoints} points</span>
            </p>
            {grades.rows.length === 0 ? <p className="text-sm text-muted-foreground">Nothing has been marked yet.</p> : (
              <ul className="flex flex-col gap-2">
                {grades.rows.map((row, index) => (
                  <li key={`${row.kind}:${row.title}:${index}`} className="flex flex-col gap-0.5 rounded-md border border-border px-3 py-2 text-sm">
                    <span className="flex flex-wrap items-center justify-between gap-2"><strong>{row.title}</strong>
                      <span>{row.score !== null ? `${row.score} / ${row.maxPoints}` : '—'} · {percentText(row.percent)}</span></span>
                    <small className="text-muted-foreground">{row.kind} · {row.status}{row.isLate ? ' · late' : ''}</small>
                    {row.feedback ? <p className="text-muted-foreground">{row.feedback}</p> : null}
                  </li>
                ))}
              </ul>
            )}
          </>
        )}
      </section>
      <section aria-label="My certificate" className="flex flex-col gap-2 rounded-md border border-border p-4">
        <h3 className="text-base font-semibold">Certificate</h3>
        {certificates === null ? null : certificates.length === 0 ? <p className="text-sm text-muted-foreground">You will get a certificate when you complete the course.</p> : (
          <ul className="flex flex-col gap-2">
            {certificates.map((certificate) => (
              <li key={certificate.id} className="text-sm">
                <strong className="block">{certificate.courseTitle}</strong>
                <span className="text-muted-foreground">{certificate.certificateNumber} · issued {new Date(certificate.issuedAtUtc).toLocaleDateString()}{certificate.scorePercentage != null ? ` · ${certificate.scorePercentage}%` : ''}</span>
                {certificate.revokedAtUtc ? <Badge variant="destructive" className="ml-2">Revoked</Badge> : null}
                <a className="block text-primary underline" href={certificate.verificationPath} target="_blank" rel="noreferrer">Open and verify the certificate</a>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  )
}
