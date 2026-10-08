import { useCallback, useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, NoticeBanner } from '@/components/form'
import { ApiError, apiRequest } from '@/lib/api'

type Person = { userId: string; name: string }
type Group = { id: string; name: string; members: Person[]; hasSubmitted: boolean }
type Overview = { groups: Group[]; unassigned: Person[] }

const readError = (exception: unknown, fallback: string) => (exception instanceof ApiError ? exception.message : fallback)
const MAX_MEMBERS = 12

/** Staff place enrolled learners into groups for a group assignment. Once a group has handed work in, it is locked. */
export default function AssignmentGroupsPanel({ assignmentId }: { assignmentId: string }) {
  const [overview, setOverview] = useState<Overview | null>(null)
  const [editing, setEditing] = useState<Group | null>(null)
  const [name, setName] = useState('')
  const [chosen, setChosen] = useState<string[]>([])
  const [problem, setProblem] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const base = `/api/v1/tenant/assignments/${assignmentId}/groups`

  const load = useCallback(async () => {
    try {
      const result = await apiRequest<Overview>(base)
      setOverview({ groups: Array.isArray(result?.groups) ? result.groups : [], unassigned: Array.isArray(result?.unassigned) ? result.unassigned : [] })
    } catch (exception) { setError(readError(exception, 'Unable to load the groups.')) }
  }, [base])
  useEffect(() => { setOverview(null); setEditing(null); void load() }, [load])

  const reset = () => { setEditing(null); setName(''); setChosen([]); setProblem(null) }
  const startEdit = (group: Group) => { setEditing(group); setName(group.name); setChosen(group.members.map((member) => member.userId)); setProblem(null); setNotice(null) }
  const toggle = (id: string) => setChosen((current) => (current.includes(id) ? current.filter((item) => item !== id) : [...current, id]))

  async function save(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!name.trim()) return setProblem('Enter a name for the group.')
    if (chosen.length === 0) return setProblem('Choose at least one learner.')
    if (chosen.length > MAX_MEMBERS) return setProblem(`A group can have at most ${MAX_MEMBERS} members.`)
    setBusy(true); setProblem(null); setNotice(null)
    try {
      const body = JSON.stringify({ name: name.trim(), memberUserIds: chosen })
      if (editing) await apiRequest(`${base}/${editing.id}`, { method: 'PUT', body })
      else await apiRequest(base, { method: 'POST', body })
      setNotice(editing ? 'Group changed.' : 'Group created.'); reset(); await load()
    } catch (exception) { setProblem(readError(exception, 'Unable to save the group.')) } finally { setBusy(false) }
  }

  async function remove(group: Group) {
    if (!window.confirm(`Delete the group “${group.name}”? Its members will have no group.`)) return
    setBusy(true); setError(null); setNotice(null)
    try { await apiRequest(`${base}/${group.id}`, { method: 'DELETE' }); setNotice('Group deleted.'); await load() }
    catch (exception) { setError(readError(exception, 'Unable to delete the group.')) } finally { setBusy(false) }
  }

  // Who can be picked: everyone not in a group, plus the members of the group being changed.
  const pickable = [...(editing?.members ?? []), ...(overview?.unassigned ?? [])].sort((a, b) => a.name.localeCompare(b.name))

  return (
    <div className="flex flex-col gap-4">
      <p className="text-sm text-muted-foreground">A learner can be in one group. A group hands in one piece of work and shares one grade. A group that has submitted can no longer be changed.</p>
      <ErrorBanner message={error} />
      <NoticeBanner message={notice} />
      {overview === null ? <p role="status" className="text-sm text-muted-foreground">Loading…</p>
        : overview.groups.length === 0 ? <EmptyState>No groups yet.</EmptyState>
          : (
            <ul className="flex flex-col gap-2" aria-label="Groups">
              {overview.groups.map((group) => (
                <li key={group.id} className="flex flex-wrap items-start justify-between gap-2 rounded-md border border-border p-3 text-sm">
                  <span>
                    <strong className="block">{group.name}{group.hasSubmitted ? ' · submitted' : ''}</strong>
                    <small className="text-muted-foreground">{group.members.map((member) => member.name).join(', ')}</small>
                  </span>
                  <span className="flex gap-2">
                    <Button size="sm" variant="outline" disabled={busy || group.hasSubmitted} aria-label={`Edit group ${group.name}`} onClick={() => startEdit(group)}>Edit</Button>
                    <Button size="sm" variant="outline" disabled={busy || group.hasSubmitted} aria-label={`Delete group ${group.name}`} onClick={() => void remove(group)}>Delete</Button>
                  </span>
                </li>
              ))}
            </ul>
          )}
      {overview && overview.unassigned.length > 0 ? <p className="text-sm text-muted-foreground">Not in a group yet: {overview.unassigned.map((person) => person.name).join(', ')}.</p> : null}

      <FormLayout onSubmit={(event) => void save(event)} noValidate>
        <ErrorBanner message={problem} />
        <FormSection title={editing ? `Change ${editing.name}` : 'New group'} divider={false}>
          <Field id="group-name" label="Group name" required><Input id="group-name" maxLength={100} value={name} onChange={(event) => setName(event.target.value)} /></Field>
          {pickable.length === 0 ? <p className="text-sm text-muted-foreground">Every enrolled learner is already in a group.</p> : (
            <div role="group" aria-label="Members" className="grid gap-1.5 sm:grid-cols-2">
              {pickable.map((person) => (
                <label key={person.userId} className="flex items-center gap-2 text-sm">
                  <input type="checkbox" checked={chosen.includes(person.userId)} onChange={() => toggle(person.userId)} />{person.name}
                </label>
              ))}
            </div>
          )}
        </FormSection>
        <FormActions busy={busy} submitLabel={editing ? 'Save group' : 'Create group'} onCancel={editing ? reset : undefined} />
      </FormLayout>
    </div>
  )
}
