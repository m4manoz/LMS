import { useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'

export type VersionInfo = { id: string; versionNumber: number; status: string; changeSummary?: string | null }

type Props = {
  courseStatus: string
  current?: VersionInfo | null
  draft?: VersionInfo | null
  canManage: boolean
  canReview: boolean
  canPublish: boolean
  busy: boolean
  onStart: (summary: string) => void
  onSubmit: () => void
  onPublish: () => void
  onDiscard: () => void
}

/** What a person can do next with the course's versions, from where the course and its new version stand. */
export function versionActions(courseStatus: string, draft: VersionInfo | null | undefined, rights: { canManage: boolean; canReview: boolean; canPublish: boolean }) {
  const published = courseStatus === 'Published'
  return {
    start: published && !draft && rights.canManage,
    submit: published && draft?.status === 'Draft' && rights.canReview,
    publish: published && draft?.status === 'InReview' && rights.canPublish,
    discard: published && !!draft && rights.canManage,
  }
}

export default function CourseVersionPanel({ courseStatus, current, draft, canManage, canReview, canPublish, busy, onStart, onSubmit, onPublish, onDiscard }: Props) {
  const [summary, setSummary] = useState('')
  const can = versionActions(courseStatus, draft, { canManage, canReview, canPublish })
  if (courseStatus !== 'Published') {
    return (
      <Card>
        <CardHeader><CardTitle>Versions</CardTitle><CardDescription>This course has not been published yet. Once it is, changes are made in a new version.</CardDescription></CardHeader>
      </Card>
    )
  }
  return (
    <Card>
      <CardHeader>
        <CardTitle>Versions</CardTitle>
        <CardDescription>
          A published course is not edited directly. Start a new version to change its content: learners keep using the published version, and when you publish the new one their progress, notes and bookmarks move onto the matching lessons.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <span>Live: <strong>version {current?.versionNumber ?? 1}</strong></span>
          {draft ? <span>· New: <strong>version {draft.versionNumber}</strong> <Badge variant="outline">{draft.status === 'InReview' ? 'In review' : draft.status}</Badge></span> : null}
        </div>
        {draft?.changeSummary ? <p className="text-sm text-muted-foreground">{draft.changeSummary}</p> : null}
        {can.start ? (
          <form className="flex max-w-xl flex-wrap items-end gap-2" onSubmit={(event) => { event.preventDefault(); onStart(summary.trim()); setSummary('') }}>
            <div className="flex min-w-60 flex-1 flex-col gap-1.5">
              <Label htmlFor="version-summary">What is changing? (optional)</Label>
              <Input id="version-summary" maxLength={500} value={summary} onChange={(event) => setSummary(event.target.value)} placeholder="Add week 5 and fix the quiz links" />
            </div>
            <Button type="submit" disabled={busy}>Start new version</Button>
          </form>
        ) : null}
        {draft ? (
          <div className="flex flex-wrap gap-2">
            {can.submit ? <Button variant="secondary" disabled={busy} onClick={onSubmit}>Submit version for review</Button> : null}
            {can.publish ? <Button disabled={busy} onClick={onPublish}>Publish version {draft.versionNumber}</Button> : null}
            {can.discard ? <Button variant="softDestructive" disabled={busy} onClick={() => { if (window.confirm(`Discard version ${draft.versionNumber}? The live course and learners are not affected.`)) onDiscard() }}>Discard version</Button> : null}
          </div>
        ) : null}
        {draft?.status === 'InReview' ? <p className="text-xs text-muted-foreground">A version in review cannot be edited. Publish it, or discard it and start again.</p> : null}
      </CardContent>
    </Card>
  )
}
