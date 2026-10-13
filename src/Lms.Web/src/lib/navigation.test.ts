import { describe, expect, it } from 'vitest'
import { canViewMenuItem, isLearner, menuAliases, menuGroups, menuLabel } from './navigation'

const items = menuGroups.flatMap((group) => group.submenus.flatMap((submenu) => submenu.items))

describe('menu structure', () => {
  it('lists every destination once', () => {
    const ids = items.map((item) => item.id)
    expect(new Set(ids).size).toBe(ids.length)
  })

  it('never repeats a label inside a group, so nothing looks like a duplicate', () => {
    for (const group of menuGroups) {
      const labels = group.submenus.flatMap((submenu) => submenu.items.map((item) => item.label))
      expect(new Set(labels).size, group.label).toBe(labels.length)
    }
  })

  it('never gives a submenu the same name as one of its items, which reads as a duplicate', () => {
    for (const group of menuGroups) for (const submenu of group.submenus) expect(submenu.items.map((item) => item.label), `${group.label}/${submenu.label}`).not.toContain(submenu.label)
  })

  it('runs from daily learning to staff work to administration', () => {
    expect(menuGroups.map((group) => group.label)).toEqual(['Workspace', 'Learning', 'Classroom', 'Assessment', 'Teaching', 'AI Workspace', 'Communication', 'Analytics', 'Administration'])
  })

  it('keeps authoring tools together under Teaching', () => {
    const teaching = menuGroups.find((group) => group.label === 'Teaching')!.submenus.flatMap((submenu) => submenu.items.map((item) => item.id))
    expect(teaching).toEqual(expect.arrayContaining(['admin-courses', 'categories', 'question-bank', 'enroll-learners', 'applications', 'schedule-class', 'cohorts']))
  })

  it('gives every group at least one item in every submenu', () => {
    for (const group of menuGroups) for (const submenu of group.submenus) expect(submenu.items.length, `${group.label}/${submenu.label}`).toBeGreaterThan(0)
  })

  it('points each old id at a real destination', () => {
    for (const target of Object.values(menuAliases)) expect(items.some((item) => item.id === target)).toBe(true)
    expect(menuLabel('communication-notifications')).toBe('Notifications')
  })

  it('gives staff Learners and Instructors pages of their own, and keeps them from learners and guardians', () => {
    const learners = items.find((item) => item.id === 'learners')!
    const instructors = items.find((item) => item.id === 'instructors')!
    for (const item of [learners, instructors]) {
      expect(canViewMenuItem(item, 'TEACHER', ['enrollment.manage', 'learner.read'])).toBe(true)
      expect(canViewMenuItem(item, 'TENANT_ADMIN', ['user.read'])).toBe(true)
      expect(canViewMenuItem(item, 'LEARNER', ['learner.read', 'course.read'])).toBe(false)
      expect(canViewMenuItem(item, 'GUARDIAN', ['learner.read', 'guardian.read'])).toBe(false)
    }
  })

  it('shows Users only to people who may list users, not to learners who can merely read their own profile', () => {
    const users = items.find((item) => item.id === 'users')!
    expect(canViewMenuItem(users, 'TENANT_ADMIN', ['user.read'])).toBe(true)
    expect(canViewMenuItem(users, 'LEARNER', ['learner.read', 'course.read'])).toBe(false)
  })

  // The permissions the built-in roles are given (see IdentityEndpoints.DefaultRoles).
  const learnerPermissions = ['tenant.read', 'course.read', 'learner.read', 'enrollment.read', 'progress.read', 'progress.manage', 'assessment.read', 'assessment.attempt', 'grade.read', 'notification.read', 'certificate.read', 'ai.use', 'liveclass.read', 'attendance.read', 'collaboration.read', 'collaboration.manage', 'recommendation.read', 'gamification.read', 'virtuallab.read']
  const teacherPermissions = [...learnerPermissions.filter((item) => item !== 'assessment.attempt'), 'course.manage', 'assessment.manage', 'grade.manage', 'liveclass.manage']
  const guardianPermissions = ['tenant.read', 'course.read', 'learner.read', 'guardian.read', 'enrollment.read', 'progress.read', 'assessment.read', 'grade.read', 'notification.read', 'certificate.read', 'liveclass.read', 'attendance.read', 'collaboration.read']
  const visible = (role: string, permissions: string[]) => items.filter((item) => canViewMenuItem(item, role, permissions)).map((item) => item.id)

  it('tells a learner from everyone else by what they do: takes assessments and does not manage courses', () => {
    expect(isLearner(learnerPermissions)).toBe(true)
    expect(isLearner(teacherPermissions)).toBe(false)
    expect(isLearner(guardianPermissions)).toBe(false)
    expect(isLearner([...learnerPermissions, 'course.manage'])).toBe(false)           // a teacher who also learns keeps the full menu
  })

  it('gives a learner a short menu: what is done in a course is inside the course', () => {
    const menu = visible('LEARNER', learnerPermissions)
    expect(menu).toEqual(expect.arrayContaining(['dashboard', 'calendar', 'my-tasks', 'notifications', 'learning', 'course-catalog', 'learning-paths', 'invitations', 'certificates', 'messages']))
    for (const inCourse of ['video-library', 'live', 'recordings', 'attendance', 'discussions', 'quizzes', 'exams', 'progress', 'assignments', 'grades', 'performance', 'attendance-reports'])
      expect(menu, inCourse).not.toContain(inCourse)
    expect(menu.length).toBeLessThanOrEqual(20)
  })

  it('keeps the whole menu for teachers and guardians', () => {
    const teacher = visible('TEACHER', teacherPermissions)
    for (const inCourse of ['video-library', 'live', 'recordings', 'attendance', 'quizzes', 'assignments', 'grades']) expect(teacher, inCourse).toContain(inCourse)
    const guardian = visible('GUARDIAN', guardianPermissions)
    for (const item of ['live', 'recordings', 'attendance', 'quizzes', 'grades']) expect(guardian, item).toContain(item)
  })

  it('leaves no group or submenu empty for a learner, so none shows as a bare heading', () => {
    const shown = new Set(visible('LEARNER', learnerPermissions))
    const groups = menuGroups.filter((group) => group.submenus.some((submenu) => submenu.items.some((item) => shown.has(item.id))))
    expect(groups.map((group) => group.label)).not.toContain('Classroom')
    expect(groups.map((group) => group.label)).not.toContain('Assessment')
    for (const group of groups) for (const submenu of group.submenus.filter((entry) => entry.items.some((item) => shown.has(item.id)))) expect(submenu.items.some((item) => shown.has(item.id))).toBe(true)
  })

  it('only hides pages from learners that their course page covers', () => {
    const hidden = items.filter((item) => item.learnerHidden).map((item) => item.id).sort()
    expect(hidden).toEqual(['assignments', 'attendance', 'attendance-reports', 'discussions', 'exams', 'grades', 'live', 'performance', 'progress', 'quizzes', 'recordings', 'video-library'])
  })
})
