import { useEffect, useState } from 'react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { ApiError, apiRequest } from '@/lib/api'

type Preference = { templateCode: string; channel: 'InApp' | 'Email'; enabled: boolean }

export const notificationTypes: { code: string; label: string }[] = [
  { code: 'ANNOUNCEMENT', label: 'Announcements' },
  { code: 'ASSIGNMENT_PUBLISHED', label: 'New assignments' },
  { code: 'ASSIGNMENT_GRADED', label: 'Assignment grades' },
  { code: 'ASSESSMENT_GRADED', label: 'Assessment grades' },
  { code: 'DEADLINE_REMINDER', label: 'Deadline reminders' },
  { code: 'ENROLLMENT_CREATED', label: 'Enrollment confirmations' },
  { code: 'ENROLLMENT_WAITLISTED', label: 'Added to a waitlist' },
  { code: 'ENROLLMENT_PROMOTED', label: 'A waitlist place opened up' },
  { code: 'COURSE_INVITATION', label: 'Course invitations' },
  { code: 'COURSE_UPDATED', label: 'Course updates' },
  { code: 'COURSE_COMPLETED', label: 'Course completions' },
  { code: 'CERTIFICATE_ISSUED', label: 'Certificates' },
]

/** A type is on unless the person has explicitly switched that channel off. */
export const isEnabled = (preferences: Preference[], code: string, channel: Preference['channel']) =>
  preferences.find((item) => item.templateCode === code && item.channel === channel)?.enabled ?? true

export default function NotificationPreferences() {
  const [preferences, setPreferences] = useState<Preference[]>([])
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState<string | null>(null)

  useEffect(() => {
    apiRequest<Preference[]>('/api/v1/tenant/notification-preferences').then(setPreferences)
      .catch((exception) => setError(readError(exception, 'Unable to load your preferences.')))
  }, [])

  async function toggle(code: string, channel: Preference['channel']) {
    const enabled = !isEnabled(preferences, code, channel)
    const key = `${code}:${channel}`
    setSaving(key); setError(null)
    try {
      await apiRequest('/api/v1/tenant/notification-preferences', { method: 'PUT', body: JSON.stringify({ templateCode: code, channel, enabled }) })
      setPreferences((current) => [...current.filter((item) => !(item.templateCode === code && item.channel === channel)), { templateCode: code, channel, enabled }])
    } catch (exception) { setError(readError(exception, 'Unable to save your preference.')) }
    finally { setSaving(null) }
  }

  return (
    <Card className="max-w-2xl">
      <CardHeader>
        <CardTitle>What you are notified about</CardTitle>
        <CardDescription>Choose where each kind of notification reaches you. Email is only sent when your organization has turned it on.</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        {error ? <div role="alert" className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">{error}</div> : null}
        <table className="w-full text-sm">
          <thead>
            <tr className="text-left text-muted-foreground"><th scope="col" className="py-2 font-medium">Notification</th><th scope="col" className="w-24 py-2 font-medium">In-app</th><th scope="col" className="w-24 py-2 font-medium">Email</th></tr>
          </thead>
          <tbody>
            {notificationTypes.map((type) => (
              <tr key={type.code} className="border-t border-border">
                <th scope="row" className="py-2 text-left font-normal">{type.label}</th>
                {(['InApp', 'Email'] as const).map((channel) => (
                  <td key={channel} className="py-2">
                    <input
                      type="checkbox" aria-label={`${type.label} — ${channel === 'InApp' ? 'in-app' : 'email'}`}
                      checked={isEnabled(preferences, type.code, channel)} disabled={saving === `${type.code}:${channel}`}
                      onChange={() => void toggle(type.code, channel)}
                    />
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </CardContent>
    </Card>
  )
}

function readError(exception: unknown, fallback: string) { return exception instanceof ApiError ? exception.message : fallback }
