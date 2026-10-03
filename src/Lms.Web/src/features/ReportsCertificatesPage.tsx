import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { cn } from '@/lib/utils'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { ApiError, apiRequest, getStoredSession } from '../lib/api'
import { useAuth } from '../lib/auth'

type Notification = { id: string; templateCode: string; subject: string; body: string; status: string; createdAtUtc: string; sentAtUtc?: string | null; readAtUtc?: string | null }
type Certificate = { id: string; certificateNumber: string; verificationCode: string; courseTitle: string; learnerName: string; scorePercentage?: number | null; issuedAtUtc: string; revokedAtUtc?: string | null; verificationPath: string }
type Enrollment = { id: string; courseTitle: string; status: string; progressPercent: number }
type Overview = { publishedCourseCount: number; enrollmentCount: number; completedEnrollmentCount: number; completionRatePercent: number; averageProgressPercent: number; gradedAttemptCount: number; averageGradePercent: number; certificateCount: number }
type Phase5Focus = 'overview' | 'notifications' | 'certificates' | 'reports'

export default function ReportsCertificatesPage({ focus = 'overview', hideTitle = false }: { focus?: Phase5Focus; hideTitle?: boolean }) {
  const { session } = useAuth()
  const [notifications, setNotifications] = useState<Notification[]>([])
  const [certificates, setCertificates] = useState<Certificate[]>([])
  const [enrollments, setEnrollments] = useState<Enrollment[]>([])
  const [overview, setOverview] = useState<Overview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const canReport = session?.permissions.includes('report.read') ?? false
  const canExport = session?.permissions.includes('report.export') ?? false
  const canIssue = session?.permissions.includes('certificate.manage') ?? false
  const showNotifications = focus === 'overview' || focus === 'notifications'
  const showCertificates = focus === 'overview' || focus === 'certificates'

  useEffect(() => { void load() }, [session?.accessToken])

  async function load() {
    setError(null)
    try { setNotifications(await apiRequest<Notification[]>('/api/v1/tenant/notifications')) } catch (exception) { setError(readError(exception, 'Unable to load notifications.')) }
    try { setCertificates(await apiRequest<Certificate[]>('/api/v1/tenant/certificates')) } catch { setCertificates([]) }
    if (canIssue) {
      try { setEnrollments(await apiRequest<Enrollment[]>('/api/v1/tenant/enrollments')) } catch { setEnrollments([]) }
    }
    if (canReport) {
      try { setOverview(await apiRequest<Overview>('/api/v1/tenant/reports/overview')) } catch { setOverview(null) }
    }
  }

  async function markRead(notificationId: string) {
    try { await apiRequest(`/api/v1/tenant/notifications/${notificationId}/read`, { method: 'POST' }); setNotifications((current) => current.map((item) => item.id === notificationId ? { ...item, readAtUtc: new Date().toISOString() } : item)) }
    catch (exception) { setError(readError(exception, 'Unable to mark notification as read.')) }
  }

  async function issueCertificate(enrollmentId: string) {
    setBusy(true); setError(null)
    try { await apiRequest(`/api/v1/tenant/enrollments/${enrollmentId}/certificate`, { method: 'POST' }); await load() }
    catch (exception) { setError(readError(exception, 'Unable to issue certificate.')) }
    finally { setBusy(false) }
  }

  async function downloadReport(fileName: string) {
    const stored = getStoredSession()
    if (!stored) return
    setBusy(true); setError(null)
    try {
      const response = await fetch(`/api/v1/tenant/reports/${fileName}`, { headers: { Authorization: `Bearer ${stored.accessToken}`, 'X-Tenant-Slug': stored.tenant.slug } })
      if (!response.ok) throw new ApiError(`Report download failed with status ${response.status}.`, response.status)
      const blob = await response.blob(); const url = URL.createObjectURL(blob); const anchor = document.createElement('a'); anchor.href = url; anchor.download = fileName; anchor.click(); URL.revokeObjectURL(url)
    } catch (exception) { setError(readError(exception, 'Unable to download report.')) }
    finally { setBusy(false) }
  }

  const title = focus === 'notifications' ? 'Notifications' : focus === 'certificates' ? 'Certificates' : focus === 'reports' ? 'Reports and analytics' : 'Operations overview'
  const completed = enrollments.filter((item) => item.status === 'Completed')
  return (
    <section className="col-span-full flex flex-col gap-4 text-foreground">
      <div className="flex items-end justify-between">
        {hideTitle ? <span /> : <h2 className="text-2xl font-semibold">{title}</h2>}
        {showNotifications ? <Badge variant="outline">{notifications.filter((item) => !item.readAtUtc).length} unread</Badge> : null}
      </div>
      {error ? <div role="alert" className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">{error}</div> : null}
      {focus === 'reports' ? (
        <Tabs defaultValue="overview">
          <TabsList>
            <TabsTrigger value="overview">Overview</TabsTrigger>
            {canExport ? <TabsTrigger value="exports">Exports</TabsTrigger> : null}
          </TabsList>
          <TabsContent value="overview">
      {overview ? (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          <Metric label="Published courses" value={overview.publishedCourseCount} />
          <Metric label="Enrollments" value={overview.enrollmentCount} />
          <Metric label="Completion rate" value={`${overview.completionRatePercent}%`} />
          <Metric label="Average progress" value={`${overview.averageProgressPercent}%`} />
          <Metric label="Average grade" value={`${overview.averageGradePercent}%`} />
          <Metric label="Certificates" value={overview.certificateCount} />
        </div>
      ) : null}
          </TabsContent>
          {canExport ? <TabsContent value="exports">
        <Card>
          <CardHeader><CardTitle>Exports</CardTitle></CardHeader>
          <CardContent className="flex flex-wrap gap-2">
            <Button variant="secondary" disabled={busy} onClick={() => void downloadReport('enrollments.csv')}>Download enrollments CSV</Button>
            <Button variant="secondary" disabled={busy} onClick={() => void downloadReport('grades.csv')}>Download grades CSV</Button>
          </CardContent>
        </Card>
          </TabsContent> : null}
        </Tabs>
      ) : null}
      <div className="grid gap-4 lg:grid-cols-2">
        {showNotifications ? (
          <Card>
            <CardHeader><CardTitle>Notifications</CardTitle></CardHeader>
            <CardContent className="flex flex-col gap-2">
              {notifications.length === 0 ? <p className="text-sm text-muted-foreground">No notifications yet.</p> : notifications.slice(0, 20).map((item) => (
                <div key={item.id} className={cn('flex items-start justify-between gap-3 rounded-md border border-border px-3 py-2 text-sm', !item.readAtUtc && 'border-primary')}>
                  <div>
                    <strong>{item.subject}</strong>
                    <p className="text-muted-foreground">{item.body}</p>
                    <small className="text-muted-foreground">{new Date(item.createdAtUtc).toLocaleString()}</small>
                  </div>
                  {!item.readAtUtc ? <Button variant="soft" size="sm" onClick={() => void markRead(item.id)}>Mark read</Button> : null}
                </div>
              ))}
            </CardContent>
          </Card>
        ) : null}
        {showCertificates ? (
          <Card>
            <CardHeader><CardTitle>Certificates</CardTitle></CardHeader>
            <CardContent className="flex flex-col gap-2">
              {certificates.length === 0 ? <p className="text-sm text-muted-foreground">No certificates issued yet.</p> : certificates.map((certificate) => (
                <div key={certificate.id} className="rounded-md border border-border px-3 py-2 text-sm">
                  <strong className="block">{certificate.courseTitle}</strong>
                  <span className="text-muted-foreground">{certificate.certificateNumber}</span>
                  <small className="block text-muted-foreground">{certificate.learnerName} · {certificate.scorePercentage != null ? `${certificate.scorePercentage}%` : 'Completed'} · {new Date(certificate.issuedAtUtc).toLocaleDateString()}</small>
                  <a className="text-primary underline" href={certificate.verificationPath} target="_blank" rel="noreferrer">Verify certificate</a>
                </div>
              ))}
              {canIssue && completed.length > 0 ? (
                <div className="mt-2 flex flex-col gap-2 border-t border-border pt-3">
                  <p className="text-sm font-medium">Issue certificates</p>
                  {completed.map((enrollment) => (
                    <div key={enrollment.id} className="flex items-center justify-between text-sm">
                      <span>{enrollment.courseTitle}</span>
                      <Button variant="soft" size="sm" disabled={busy} onClick={() => void issueCertificate(enrollment.id)}>Issue</Button>
                    </div>
                  ))}
                </div>
              ) : null}
            </CardContent>
          </Card>
        ) : null}
      </div>
    </section>
  )
}

function Metric({ label, value }: { label: string; value: string | number }) {
  return (
    <Card>
      <CardHeader>
        <CardDescription>{label}</CardDescription>
        <CardTitle className="text-3xl">{value}</CardTitle>
      </CardHeader>
    </Card>
  )
}
function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
