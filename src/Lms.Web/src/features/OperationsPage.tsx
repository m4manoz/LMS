import { useEffect, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { ApiError, apiRequest } from '../lib/api'

type OperationsSummary = {
  sinceUtc: string
  counts: { activeOfflineDevices: number; activeOfflineLicenses: number; openOfflineConflicts: number; deadLetterWebhooks: number; unhealthyActiveLabs: number }
  telemetry: { name: string; count: number; lastOccurredAtUtc: string }[]
  alerts: { code: string; severity: string; message: string; actual: number; threshold: number }[]
  devicePilot: { status: string; blockingReasons: string[]; acceptanceCriteria: string[] }
}

export default function OperationsPage() {
  const [summary, setSummary] = useState<OperationsSummary | null>(null)
  const [error, setError] = useState<string | null>(null)

  async function refresh() {
    try {
      setError(null)
      setSummary(await apiRequest<OperationsSummary>('/api/v1/tenant/operations/summary'))
    } catch (exception) {
      setError(exception instanceof ApiError ? exception.message : 'Unable to load operations telemetry.')
    }
  }

  useEffect(() => { void refresh() }, [])

  return (
    <section className="col-span-full flex flex-col gap-4 text-foreground">
      <div className="flex items-end justify-between">
        <div>
          <h2 className="text-2xl font-semibold">Operational readiness</h2>
          <p className="text-sm text-muted-foreground">Tenant health, offline access, webhook reliability and the device pilot gate.</p>
        </div>
        <Button variant="outline" onClick={() => void refresh()}>Refresh</Button>
      </div>
      {error ? <div role="alert" className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">{error}</div> : null}
      {summary ? (
        <Tabs defaultValue="overview">
          <TabsList>
            <TabsTrigger value="overview">Overview</TabsTrigger>
            <TabsTrigger value="alerts">Alerts ({summary.alerts.length})</TabsTrigger>
            <TabsTrigger value="pilot">Device pilot</TabsTrigger>
            <TabsTrigger value="telemetry">Telemetry</TabsTrigger>
          </TabsList>
          <TabsContent value="overview">
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
            <Metric label="Active offline devices" value={summary.counts.activeOfflineDevices} />
            <Metric label="Active package licenses" value={summary.counts.activeOfflineLicenses} />
            <Metric label="Open sync conflicts" value={summary.counts.openOfflineConflicts} />
            <Metric label="Dead-letter webhooks" value={summary.counts.deadLetterWebhooks} />
            <Metric label="Unhealthy active labs" value={summary.counts.unhealthyActiveLabs} />
          </div>
          </TabsContent>
          <TabsContent value="alerts">
            <Card>
              <CardHeader>
                <div className="flex items-center justify-between">
                  <CardTitle>Alerts</CardTitle>
                  <Badge variant={summary.alerts.length ? 'destructive' : 'secondary'}>{summary.alerts.length ? `${summary.alerts.length} active` : 'Clear'}</Badge>
                </div>
              </CardHeader>
              <CardContent className="flex flex-col gap-2">
                {summary.alerts.length === 0 ? <p className="text-sm text-muted-foreground">No configured thresholds are currently breached.</p> : summary.alerts.map(alert => (
                  <div key={alert.code} className="rounded-md border border-border p-3 text-sm">
                    <strong>{alert.message}</strong>
                    <small className="block text-muted-foreground">{alert.severity} · {alert.actual} observed · threshold {alert.threshold}</small>
                  </div>
                ))}
              </CardContent>
            </Card>
          </TabsContent>
          <TabsContent value="pilot">
            <Card>
              <CardHeader>
                <div className="flex items-center justify-between">
                  <CardTitle>Device pilot decision</CardTitle>
                  <Badge variant={summary.devicePilot.status === 'NotReady' ? 'outline' : 'default'}>{summary.devicePilot.status}</Badge>
                </div>
              </CardHeader>
              <CardContent className="flex flex-col gap-2 text-sm">
                {summary.devicePilot.blockingReasons.length ? summary.devicePilot.blockingReasons.map(reason => <small key={reason} className="text-muted-foreground">{reason}</small>) : <p className="text-muted-foreground">Telemetry gates are satisfied. Complete the owner, cohort, rollback and support review before launch.</p>}
                <p className="mt-2 font-medium">Acceptance criteria</p>
                {summary.devicePilot.acceptanceCriteria.map(criteria => <small key={criteria} className="text-muted-foreground">✓ {criteria}</small>)}
              </CardContent>
            </Card>
          </TabsContent>
          <TabsContent value="telemetry">
          <Card>
            <CardHeader>
              <CardTitle>30-day telemetry</CardTitle>
              <CardDescription>Since {new Date(summary.sinceUtc).toLocaleDateString()}</CardDescription>
            </CardHeader>
            <CardContent className="flex flex-col gap-2">
              {summary.telemetry.length === 0 ? <p className="text-sm text-muted-foreground">No telemetry events have been recorded in this window.</p> : summary.telemetry.map(item => (
                <div key={item.name} className="flex items-center justify-between rounded-md border border-border px-3 py-2 text-sm">
                  <strong>{item.name}</strong>
                  <span>{item.count}</span>
                  <small className="text-muted-foreground">Last {new Date(item.lastOccurredAtUtc).toLocaleString()}</small>
                </div>
              ))}
            </CardContent>
          </Card>
          </TabsContent>
        </Tabs>
      ) : <p className="text-sm text-muted-foreground">Loading operational summary…</p>}
    </section>
  )
}

function Metric({ label, value }: { label: string; value: number }) {
  return (
    <Card>
      <CardHeader>
        <CardDescription>{label}</CardDescription>
        <CardTitle className="text-3xl">{value}</CardTitle>
      </CardHeader>
    </Card>
  )
}
