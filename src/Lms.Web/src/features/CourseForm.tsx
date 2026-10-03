import { useState } from 'react'
import CalendarDateField from '../components/CalendarDateField'
import { ErrorBanner, Field, FormActions, FormLayout, FormSection } from '@/components/form'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { validateForm, type CategoryOption, type CourseFormValues } from './courseAuthoring'

type Props = {
  initial: CourseFormValues
  categories: CategoryOption[]
  /** Creating allows choosing the code; editing keeps it fixed. */
  creating: boolean
  busy: boolean
  submitLabel: string
  disabled?: boolean
  onSubmit: (values: CourseFormValues) => void
  onCancel?: () => void
}

/** Basic course details, grouped into identity, availability and capacity. Used to create a course and to edit a draft. */
export default function CourseForm({ initial, categories, creating, busy, submitLabel, disabled = false, onSubmit, onCancel }: Props) {
  const [values, setValues] = useState(initial)
  const [problem, setProblem] = useState<string | null>(null)
  const set = (patch: Partial<CourseFormValues>) => setValues((current) => ({ ...current, ...patch }))

  return (
    <FormLayout onSubmit={(event) => { event.preventDefault(); const message = validateForm(values, creating); setProblem(message); if (!message) onSubmit(values) }}>
      <ErrorBanner message={problem} />
      <FormSection title="About the course" disabled={disabled}>
        <div className="grid gap-3 sm:grid-cols-[180px_minmax(0,1fr)]">
          <Field id="course-code" label="Course code" required={creating}><Input id="course-code" value={values.code} onChange={(e) => set({ code: e.target.value })} placeholder="MATH-101" maxLength={80} disabled={!creating} /></Field>
          <Field id="course-title" label="Title" required><Input id="course-title" value={values.title} onChange={(e) => set({ title: e.target.value })} maxLength={250} /></Field>
        </div>
        <Field id="course-category" label="Category" className="max-w-sm" hint="Groups the course in the catalog. Manage categories under Course categories.">
          <Select id="course-category" value={values.categoryId} onChange={(e) => set({ categoryId: e.target.value })}>
            <option value="">No category</option>
            {categories.map((category) => <option key={category.id} value={category.id}>{category.name}</option>)}
          </Select>
        </Field>
        <Field id="course-description" label="Description" hint="Shown to learners in the catalog."><Textarea id="course-description" value={values.description} onChange={(e) => set({ description: e.target.value })} rows={4} /></Field>
      </FormSection>

      <FormSection title="Availability" disabled={disabled}>
        <div className="grid gap-3 sm:grid-cols-2">
          <CalendarDateField id="course-start-date" label="Available from" value={values.startDateAd} onChange={(value) => set({ startDateAd: value })} />
          <CalendarDateField id="course-end-date" label="Available until" value={values.endDateAd} onChange={(value) => set({ endDateAd: value })} />
        </div>
        <Field id="course-capacity" label="Enrollment capacity" className="max-w-xs" hint="Learners beyond this join a waitlist."><Input id="course-capacity" type="number" min="1" max="1000000" value={values.capacity} onChange={(e) => set({ capacity: e.target.value })} placeholder="Unlimited" /></Field>
      </FormSection>

      <FormActions busy={busy} disabled={disabled} submitLabel={submitLabel} onCancel={onCancel} />
    </FormLayout>
  )
}
