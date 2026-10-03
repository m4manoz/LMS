import { useMemo, useState } from 'react'
import { Search } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { EmptyState, ListRow, RowList } from '@/components/form'
import { Select } from '@/components/ui/select'
import { filterCourses, STATUS_FILTERS, statusLabel, statusVariant, type CategoryOption, type Course } from './courseAuthoring'

type Props = { courses: Course[]; categories?: CategoryOption[]; selectedId?: string; showFilters: boolean; onSelect: (id: string) => void }

const availability = (course: Course) => (course.startDateAd || course.endDateAd ? `${course.startDateAd ?? 'Any date'} → ${course.endDateAd ?? 'Open ended'}` : 'Any time')

/** Every course on a full-width row with a "View details" action. The row of the course whose details are open carries a left accent bar. */
export default function CourseList({ courses, categories = [], selectedId, showFilters, onSelect }: Props) {
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState<string>('All')
  const [category, setCategory] = useState('All')
  const shown = useMemo(() => filterCourses(courses, search, status, category), [courses, search, status, category])
  const counts = useMemo(() => Object.fromEntries(STATUS_FILTERS.map((item) => [item, item === 'All' ? courses.length : courses.filter((course) => course.status === item).length])), [courses])

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-3">
        <div className="relative w-full max-w-sm">
          <Search className="pointer-events-none absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" aria-hidden />
          <Input className="pl-8" type="search" placeholder="Search by title or code" aria-label="Search courses" value={search} onChange={(event) => setSearch(event.target.value)} />
        </div>
        {categories.length > 0 ? (
          <Select aria-label="Filter by category" className="w-48" value={category} onChange={(event) => setCategory(event.target.value)}>
            <option value="All">All categories</option>
            {categories.map((item) => <option key={item.id} value={item.id}>{item.name}</option>)}
            <option value="none">No category</option>
          </Select>
        ) : null}
        {showFilters ? (
          <div className="flex flex-wrap gap-1.5" role="group" aria-label="Filter by status">
            {STATUS_FILTERS.filter((item) => item === 'All' || counts[item] > 0).map((item) => (
              <Button key={item} type="button" size="sm" variant={status === item ? 'secondary' : 'outline'} aria-pressed={status === item} onClick={() => setStatus(item)}>
                {item === 'All' ? 'All' : statusLabel(item)} <span className="text-muted-foreground">{counts[item]}</span>
              </Button>
            ))}
          </div>
        ) : null}
      </div>

      {courses.length === 0 ? <EmptyState>No courses are available yet.</EmptyState> : shown.length === 0 ? <EmptyState>No courses match.</EmptyState> : (
        <RowList label="Courses">
          {shown.map((course) => (
          <ListRow key={course.id} selected={course.id === selectedId} columns="sm:grid-cols-[minmax(0,1fr)_auto] md:grid-cols-[minmax(0,2fr)_110px_minmax(0,1.3fr)_100px_auto]">
            <div className="min-w-0"><strong className="block truncate">{course.title}</strong><small className="text-muted-foreground">{course.code}{course.categoryName ? ` · ${course.categoryName}` : ''}</small></div>
            <div><Badge variant={statusVariant(course.status)}>{statusLabel(course.status)}</Badge></div>
            <div className="hidden text-muted-foreground md:block"><small className="block">Available</small>{availability(course)}</div>
            <div className="hidden text-muted-foreground md:block"><small className="block">Capacity</small>{course.capacity ?? 'Unlimited'}</div>
            <div className="flex justify-end">
              <Button type="button" size="sm" variant="secondary" aria-label={`View details for ${course.title}`} aria-expanded={course.id === selectedId} onClick={() => onSelect(course.id)}>View details</Button>
            </div>
          </ListRow>
          ))}
        </RowList>
      )}
    </div>
  )
}
