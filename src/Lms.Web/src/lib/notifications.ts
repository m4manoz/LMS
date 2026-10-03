/** Fired after the person reads notifications, so the bell in the header can update straight away instead of on its next poll. */
export const NOTIFICATIONS_CHANGED = 'lms:notifications-changed'
export const notificationsChanged = () => window.dispatchEvent(new Event(NOTIFICATIONS_CHANGED))

/** Which screen explains a notification, by its template code. Null when there is nowhere better to go. */
export function notificationTarget(templateCode: string): string | null {
  switch (templateCode) {
    case 'ANNOUNCEMENT': return 'announcements'
    case 'ASSIGNMENT_PUBLISHED': case 'ASSIGNMENT_GRADED': case 'DEADLINE_REMINDER': return 'assignments'
    case 'ASSESSMENT_GRADED': return 'grades'
    case 'ENROLLMENT_CREATED': case 'ENROLLMENT_WAITLISTED': case 'ENROLLMENT_PROMOTED': case 'COURSE_COMPLETED': case 'COURSE_UPDATED': return 'learning'
    case 'CERTIFICATE_ISSUED': return 'certificates'
    case 'COURSE_INVITATION': return 'invitations'
    default: return null
  }
}

/** "just now", "5 minutes ago", "3 hours ago", "2 days ago", then the date. */
export function timeAgo(iso: string, now = new Date()): string {
  const seconds = Math.max(0, Math.round((now.getTime() - new Date(iso).getTime()) / 1000))
  if (seconds < 60) return 'just now'
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes} minute${minutes === 1 ? '' : 's'} ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours} hour${hours === 1 ? '' : 's'} ago`
  const days = Math.floor(hours / 24)
  if (days < 7) return `${days} day${days === 1 ? '' : 's'} ago`
  return new Date(iso).toLocaleDateString([], { dateStyle: 'medium' })
}
