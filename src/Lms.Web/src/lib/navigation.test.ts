import { describe, expect, it } from 'vitest'
import { canViewMenuItem, menuAliases, menuGroups, menuLabel } from './navigation'

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
})
