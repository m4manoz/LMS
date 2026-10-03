import { expect, test } from '@playwright/test'
import fs from 'node:fs'
import { ADMIN, CONVERTS_VIDEOS, LEARNER, SAMPLE_VIDEO, TEACHER, adminApi, apiAs, localInput, openLogin, openMenu, provision, signIn, signOut, watchForProblems } from './helpers'

// One story, in order: the later tests use what the earlier ones create.
test.describe.configure({ mode: 'serial' })

test.beforeAll(async ({ request }) => { await provision(request) })

test('the public page shows the organization, explains an unknown one, and leads to sign-in and invitations', async ({ page }) => {
  await page.goto('/?org=e2e')
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Learn without limits')
  await expect(page.getByRole('link', { name: 'E2E Academy home' })).toBeVisible()
  for (const section of ['Why learn with us', 'Frequently asked questions']) await expect(page.getByRole('heading', { name: section })).toBeVisible()
  await expect(page.getByText(/No courses are open yet/)).toBeVisible()                        // nothing is published yet at this point of the story

  // An organization that does not exist is explained, with a way to try another.
  await page.goto('/?org=nowhere-at-all')
  await expect(page.getByText(/organization was not found/)).toBeVisible()
  await page.getByLabel('Your organization').fill('  E2E ')
  await page.getByRole('button', { name: 'See its courses' }).click()
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Learn without limits')
  await expect(page.getByRole('link', { name: 'E2E Academy home' })).toBeVisible()

  // The top bar leads to sign-in, with the organization already known, and to joining by invitation.
  await page.getByRole('banner').getByRole('button', { name: 'Log in' }).click()
  await expect(page.getByLabel('Organization (tenant slug)')).toHaveValue('e2e')
  await page.getByRole('button', { name: 'Back to the home page' }).click()
  await page.getByRole('banner').getByRole('button', { name: 'I have an invitation' }).click()
  await expect(page.getByRole('heading', { name: 'Join with an invitation' })).toBeVisible()
})

test('on the shared portal a visitor names the organization, and on an organization’s own website it is never asked', async ({ page, browser }) => {
  await page.goto('/')
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Sign in to your organization')
  await page.getByLabel('Your organization').fill('E2E')
  await page.getByRole('button', { name: 'Continue to sign in' }).click()
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible()
  await expect(page.getByLabel('Organization (tenant slug)')).toHaveValue('e2e')           // the portal asks for it

  // The same app reached at the organization’s own address: the server says whose website this is.
  const context = await browser.newContext({ baseURL: 'http://localhost:5273' })
  const own = await context.newPage()
  await own.route('**/api/v1/public/site*', (route) => route.fulfill({ json: { mode: 'tenant', organization: { slug: 'e2e', name: 'E2E Academy' } } }))
  await own.goto('/?org=someone-else')
  await expect(own.getByRole('heading', { level: 1 })).toHaveText('Learn without limits')
  await expect(own.getByLabel('Your organization')).toHaveCount(0)
  await own.getByRole('banner').getByRole('button', { name: 'Log in' }).click()
  await expect(own.getByRole('button', { name: 'Sign in' })).toBeVisible()
  await expect(own.getByLabel('Organization (tenant slug)')).toHaveCount(0)                  // fixed: nothing to type
  await own.getByLabel('Email').fill(ADMIN.email)
  await own.getByLabel('Password').fill(ADMIN.password)
  await own.getByRole('button', { name: 'Sign in' }).click()
  await expect(own.getByRole('button', { name: 'Sign out' })).toBeVisible()
  await context.close()
})

test('the home page fits a phone screen and its menu works', async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 390, height: 800 }, baseURL: 'http://localhost:5273' })
  const phone = await context.newPage()
  await phone.goto('/')
  await expect(phone.getByRole('heading', { level: 1 })).toBeVisible()
  const overflow = await phone.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow).toBeLessThanOrEqual(1)                                   // nothing sticks out sideways
  await phone.getByRole('button', { name: 'Open menu' }).click()
  await phone.getByTestId('mobile-menu').getByRole('button', { name: 'Log in' }).click()
  await expect(phone.getByRole('button', { name: 'Sign in' })).toBeVisible()
  await context.close()
})

test('an administrator signs in and sees the grouped menu', async ({ page }) => {
  await signIn(page, ADMIN)
  const nav = page.getByRole('navigation', { name: 'Primary' })
  for (const group of ['Workspace', 'Learning', 'Classroom', 'Assessment', 'Teaching', 'AI Workspace', 'Communication', 'Analytics', 'Administration']) {
    await expect(nav.getByText(group, { exact: true })).toBeVisible()
  }
})

test('a wrong password is refused', async ({ page }) => {
  await openLogin(page)
  await page.getByLabel('Organization (tenant slug)').fill('e2e')
  await page.getByLabel('Email').fill(ADMIN.email)
  await page.getByLabel('Password').fill('not-the-password')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('alert')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Sign out' })).toHaveCount(0)
})

test('categories can be created from the panel', async ({ page }) => {
  await signIn(page, ADMIN)
  await openMenu(page, 'Course categories')
  await expect(page.getByRole('heading', { name: 'Course categories' })).toBeVisible()
  await expect(page.getByRole('dialog')).toHaveCount(0)               // nothing opens by default
  await page.getByRole('button', { name: 'New category' }).click()
  const panel = page.getByRole('dialog', { name: 'New category' })
  await panel.getByRole('button', { name: 'Create category' }).click() // empty: refused with a message
  await expect(panel.getByRole('alert')).toContainText('at least 2 characters')
  await panel.getByLabel('Name').fill('Science')
  await panel.getByRole('button', { name: 'Create category' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page.getByRole('list', { name: 'Categories' }).getByText('Science', { exact: true })).toBeVisible()
})

test('a course goes from new draft to published, then gets a second version', async ({ page }) => {
  await signIn(page, ADMIN)
  await openMenu(page, 'Course authoring')
  await expect(page.getByRole('dialog')).toHaveCount(0)

  // create
  await page.getByRole('button', { name: 'New course' }).click()
  const panel = page.getByRole('dialog')
  await panel.getByLabel('Course code').fill('E2E-101')
  await panel.getByLabel('Title').fill('Smoke Test Course')
  await panel.getByLabel('Category').selectOption({ label: 'Science' })
  await panel.getByRole('button', { name: 'Create draft course' }).click()
  await expect(panel.getByText('Course created.')).toBeVisible()

  // outline: typing must keep focus in the box for every character
  await panel.getByLabel('New module title').fill('Module A')
  await panel.getByRole('button', { name: 'Add module' }).click()
  const lessonBox = panel.getByLabel('New lesson title for Module A')
  await lessonBox.click()
  await lessonBox.pressSequentially('Welcome lesson')
  await expect(lessonBox).toHaveValue('Welcome lesson')
  await expect(lessonBox).toBeFocused()
  await panel.getByRole('button', { name: 'Add lesson' }).click()
  await expect(panel.getByText('↳ Welcome lesson')).toBeVisible()

  // content
  await panel.getByRole('tab', { name: 'Content' }).click()
  await panel.getByLabel('Text', { exact: true }).fill('Hello from the smoke test.')
  await panel.getByRole('button', { name: 'Add block' }).click()
  await expect(panel.getByText('Hello from the smoke test.')).toBeVisible()

  // review and publish from the header
  await panel.getByRole('tab', { name: 'Overview' }).click()
  await expect(panel.getByText('Ready for review?')).toBeVisible()
  await panel.getByRole('button', { name: 'Submit for review' }).first().click()
  await expect(panel.getByText('In review').first()).toBeVisible()
  await panel.getByRole('button', { name: 'Publish course' }).click()
  await expect(panel.getByText('Published').first()).toBeVisible()
  await expect(panel.getByRole('button', { name: 'Edit with a new version' })).toBeVisible()

  // a published course is read-only until a new version is started
  await panel.getByRole('tab', { name: 'Outline' }).click()
  await expect(panel.getByLabel('New module title')).toHaveCount(0)
  await panel.getByRole('tab', { name: 'Review and publish' }).click()
  await panel.getByLabel('What is changing?').fill('Add a second lesson')
  await panel.getByRole('button', { name: 'Start new version' }).click()
  await expect(panel.getByText(/Editing version 2/)).toBeVisible()
  await panel.getByLabel('New lesson title for Module A').fill('Second lesson')
  await panel.getByRole('button', { name: 'Add lesson' }).click()
  await expect(panel.getByText('↳ Second lesson')).toBeVisible()
  await panel.getByRole('tab', { name: 'Review and publish' }).click()
  await panel.getByRole('button', { name: 'Submit version for review' }).click()
  await panel.getByRole('button', { name: 'Publish version 2' }).click()
  await expect(panel.getByText('Live: version 2')).toBeVisible()   // the new version is now the live one
  await expect(panel.getByRole('button', { name: 'Start new version' })).toBeVisible()

  // close the panel with Escape; the list shows the course with its category and status
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog')).toHaveCount(0)
  const row = page.getByRole('listitem').filter({ hasText: 'Smoke Test Course' })
  await expect(row).toContainText('E2E-101 · Science')
  await expect(row).toContainText('Published')
})

test('a learner can be created and then sees the published course but no authoring tools', async ({ page }) => {
  await signIn(page, ADMIN)
  await openMenu(page, 'Users')
  await page.getByRole('button', { name: 'New user' }).click()
  const panel = page.getByRole('dialog', { name: 'New user' })
  await panel.getByLabel('Display name').fill(LEARNER.name)
  await panel.getByLabel('Email').fill(LEARNER.email)
  await panel.getByLabel('Password').fill(LEARNER.password)
  await panel.getByLabel('Role').selectOption({ label: 'Learner' })
  await panel.getByRole('button', { name: 'Create user' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await signOut(page)

  await signIn(page, LEARNER)
  const nav = page.getByRole('navigation', { name: 'Primary' })
  await expect(nav.getByText('Teaching', { exact: true })).toHaveCount(0)       // authoring is staff-only
  await expect(nav.getByText('Administration', { exact: true })).toHaveCount(0)
  await openMenu(page, 'Course catalog')
  const row = page.getByRole('listitem').filter({ hasText: 'Smoke Test Course' })
  await expect(row).toContainText('Science')
  await expect(page.getByRole('button', { name: 'New course' })).toHaveCount(0)
  await row.getByRole('button', { name: 'View details for Smoke Test Course' }).click()
  const details = page.getByRole('dialog', { name: 'Course details' })
  await expect(details.getByText('About this course')).toBeVisible()
  await details.getByRole('tab', { name: 'Outline' }).click()
  await expect(details.getByText('↳ Welcome lesson')).toBeVisible()
  await expect(details.getByText('↳ Second lesson')).toBeVisible()      // the second version is the live one
  await expect(details.getByRole('tab', { name: 'Details' })).toHaveCount(0)
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog')).toHaveCount(0)
})

test('an administrator enrolls a new learner from Enroll learners, sees them in the list and removes them', async ({ page, request }) => {
  await signIn(page, ADMIN)
  await openMenu(page, 'Users')
  await page.getByRole('button', { name: 'New user' }).click()
  const person = page.getByRole('dialog', { name: 'New user' })
  await person.getByLabel('Display name').fill('Noor Newcomer')
  await person.getByLabel('Email').fill('noor@e2e.test')
  await person.getByLabel('Password').fill('Newcomer-pass-123')
  await person.getByLabel('Role').selectOption({ label: 'Learner' })
  await person.getByRole('button', { name: 'Create user' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)

  await openMenu(page, 'Enroll learners')
  await page.getByLabel('Course').selectOption({ label: 'E2E-101 · Smoke Test Course' })
  const course = page
  await expect(course.getByRole('button', { name: 'Enroll selected' })).toBeDisabled()          // nobody chosen yet
  await course.getByLabel('Find people').fill('noor')
  const offered = course.getByRole('list', { name: 'People who can be added' })
  await expect(offered.getByRole('listitem')).toHaveCount(1)
  await offered.getByLabel(/Noor Newcomer/).check()
  await course.getByRole('button', { name: 'Enroll 1 selected' }).click()
  await expect(course.getByText('1 enrolled.')).toBeVisible()
  const enrolled = course.getByRole('list', { name: 'Enrolled learners' })
  await expect(enrolled.getByText('Noor Newcomer')).toBeVisible()
  await expect(offered.getByText('Noor Newcomer')).toHaveCount(0)                                // no longer offered

  // The learner can now sign in and sees the course among theirs.
  const learner = await apiAs(request, { email: 'noor@e2e.test', password: 'Newcomer-pass-123' })
  expect((await learner.get('/api/v1/tenant/enrollments')).length).toBe(1)

  // Removing takes them out again.
  page.once('dialog', (dialog) => void dialog.accept())
  await enrolled.getByRole('button', { name: 'Remove Noor Newcomer' }).click()
  await expect(course.getByText('Noor Newcomer was removed from the course.')).toBeVisible()
  await expect(enrolled.getByText('Noor Newcomer')).toHaveCount(0)
  await page.keyboard.press('Escape')
})

test('a visitor finds a course on the public page and applies; staff approve it and change the page', async ({ page, browser }) => {
  // A visitor with no account opens the front page and finds the published course.
  const visitorContext = await browser.newContext({ baseURL: 'http://localhost:5273' })
  const visitor = await visitorContext.newPage()
  await visitor.goto('/?org=e2e')
  const card = visitor.getByRole('button', { name: /Smoke Test Course/ }).first()
  await expect(card).toBeVisible()
  await visitor.getByLabel('Search courses').first().fill('smoke')
  await expect(visitor.getByRole('region', { name: 'Search results' }).getByRole('status')).toContainText('1 course')
  await visitor.getByRole('region', { name: 'Search results' }).getByRole('button', { name: /Smoke Test Course/ }).click()
  const dialog = visitor.getByRole('dialog', { name: 'Smoke Test Course' })
  await expect(dialog.getByText('Module A')).toBeVisible()                                      // the outline
  await expect(dialog.getByText('Welcome lesson')).toBeVisible()

  // She applies; the form checks first.
  await dialog.getByRole('button', { name: 'Apply now' }).click()
  await expect(dialog.getByText(/Enter your name/)).toBeVisible()
  await dialog.getByLabel(/Full name/).fill('Vera Visitor')
  await dialog.getByLabel(/^Email/).fill('vera@e2e.test')
  await dialog.getByLabel(/Why do you want/).fill('I want to learn science.')
  await dialog.getByRole('button', { name: 'Apply now' }).click()
  await expect(dialog.getByRole('status', { name: 'Application sent' })).toBeVisible()

  // Staff see the application and approve it, which sends an invitation.
  await signIn(page, ADMIN)
  await openMenu(page, 'Applications')
  const row = page.getByRole('listitem').filter({ hasText: 'Vera Visitor' })
  await expect(row).toContainText('Smoke Test Course')
  await expect(row).toContainText('I want to learn science.')
  await row.getByRole('button', { name: 'Approve Vera Visitor' }).click()
  await expect(page.getByText(/invitation was emailed|Share the link or code|already/).first()).toBeVisible()
  await expect(page.getByRole('listitem').filter({ hasText: 'Vera Visitor' })).toHaveCount(0)          // no longer waiting
  await page.getByLabel('Show applications').selectOption('Approved')
  await expect(page.getByRole('listitem').filter({ hasText: 'Vera Visitor' })).toContainText('Approved')

  // Staff change the words on the page, and visitors see the change.
  await openMenu(page, 'Landing page')
  await expect(page.getByText(/Visitors now see the standard page/)).toBeVisible()
  await page.getByLabel(/Heading/).first().fill('E2E study hub')
  await page.getByRole('button', { name: 'Save changes' }).click()
  await expect(page.getByText(/Visitors see the new page now/)).toBeVisible()
  await visitor.reload()
  await expect(visitor.getByRole('heading', { level: 1 })).toHaveText('E2E study hub')

  // A banner and a link are checked: an unsafe link is refused with a reason.
  await page.getByLabel(/Button link/).first().fill('javascript:alert(1)')
  await page.getByRole('button', { name: 'Save changes' }).click()
  await expect(page.getByText(/main button needs a link/)).toBeVisible()

  // Back to the standard page.
  await page.getByLabel(/Button link/).first().fill('#courses')
  page.once('dialog', (confirmation) => void confirmation.accept())
  await page.getByRole('button', { name: /Use standard page/ }).click()
  await expect(page.getByText('The standard page is back.')).toBeVisible()
  await visitor.reload()
  await expect(visitor.getByRole('heading', { level: 1 })).toHaveText('Learn without limits')
  await visitorContext.close()
})

test('a teacher schedules a class for a course, and an enrolled learner joins it and chats', async ({ page, browser, request }) => {
  // Set-up through the API: a teacher, and Lena enrolled in the published course.
  const api = await adminApi(request)
  expect([201, 409]).toContain((await api.post('/api/v1/tenant/users', { email: TEACHER.email, displayName: TEACHER.name, password: TEACHER.password, roleCode: 'TEACHER' })).status())
  const courses = await api.get('/api/v1/tenant/courses')
  const course = courses.find((item: { code: string }) => item.code === 'E2E-101')
  const users = await api.get('/api/v1/tenant/users')
  const lena = users.find((item: { email: string }) => item.email === LEARNER.email)
  expect([201, 409]).toContain((await api.post('/api/v1/tenant/enrollments', { courseId: course.id, learnerUserId: lena.id })).status())

  // The teacher schedules a class that has already started, so joining makes it live.
  await signIn(page, TEACHER)
  await openMenu(page, 'Schedule class')
  const panel = page.getByRole('dialog', { name: 'Schedule a class' })
  await panel.getByLabel('Title').fill('E2E revision')
  await panel.getByLabel(/Learners wait to be let in/).uncheck()          // this test is about joining and chatting; the waiting room has its own test
  await panel.getByLabel('Course').selectOption({ label: 'E2E-101 · Smoke Test Course' })
  await panel.getByLabel('Starts').fill(localInput(-5))
  await panel.getByLabel('Ends').fill(localInput(55))
  await panel.getByRole('button', { name: 'Schedule class' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  const teacherRow = page.getByRole('listitem').filter({ hasText: 'E2E revision' })
  await expect(teacherRow).toContainText('Smoke Test Course')
  await expect(teacherRow).toContainText('Scheduled')

  // Lena, in her own browser session, sees it, joins and chats.
  const lenaContext = await browser.newContext({ baseURL: 'http://localhost:5273' })
  const lenaPage = await lenaContext.newPage()
  await signIn(lenaPage, LEARNER)

  // The class is also shown inside the course: from My learning she opens it straight from the course.
  await openMenu(lenaPage, 'My learning')
  await lenaPage.getByRole('button', { name: /Smoke Test Course/ }).first().click()
  await expect(lenaPage.getByRole('heading', { name: 'Live classes' })).toBeVisible()
  await lenaPage.getByRole('button', { name: 'Open class E2E revision' }).click()
  await expect(lenaPage.getByRole('dialog', { name: 'E2E revision' })).toBeVisible()
  await lenaPage.keyboard.press('Escape')

  await openMenu(lenaPage, 'Live classes')
  await lenaPage.getByRole('button', { name: 'View details for E2E revision' }).click()
  const lenaPanel = lenaPage.getByRole('dialog', { name: 'E2E revision' })
  const popup = lenaPage.context().waitForEvent('page')
  await lenaPanel.getByRole('button', { name: 'Join class' }).click()
  const meeting = await popup
  const joinUrl = meeting.url()
  expect(joinUrl).toContain('liveSession=')                    // the built-in provider links back to the app; there is no video
  // The link opens the class itself (she is already signed in in this browser), not a blank page.
  await expect(meeting.getByRole('dialog', { name: 'E2E revision' })).toBeVisible()
  await meeting.close()

  // Someone who is not signed in follows the same link: they sign in first, then land on the class.
  const guest = await browser.newContext({ baseURL: 'http://localhost:5273' })
  const guestPage = await guest.newPage()
  await guestPage.goto(joinUrl)
  await guestPage.getByRole('banner').getByRole('button', { name: 'Log in' }).click()
  await guestPage.getByLabel('Organization (tenant slug)').fill('e2e')
  await guestPage.getByLabel('Email').fill(LEARNER.email)
  await guestPage.getByLabel('Password').fill(LEARNER.password)
  await guestPage.getByRole('button', { name: 'Sign in' }).click()
  await expect(guestPage.getByRole('dialog', { name: 'E2E revision' })).toBeVisible()
  expect(guestPage.url()).not.toContain('liveSession')         // the address is cleaned up
  await guest.close()
  await expect(lenaPanel.getByText('Live', { exact: true }).first()).toBeVisible()
  await lenaPanel.getByRole('tab', { name: 'Chat' }).click()
  await lenaPanel.getByLabel('Chat message').fill('Hello from Lena')
  await lenaPanel.getByRole('button', { name: 'Send' }).click()
  await expect(lenaPanel.getByText('Hello from Lena')).toBeVisible()

  // The teacher sees who attended and what was said.
  await teacherRow.getByRole('button', { name: 'View details for E2E revision' }).click()
  const teacherPanel = page.getByRole('dialog', { name: 'E2E revision' })
  await teacherPanel.getByRole('tab', { name: 'Attendance' }).click()
  await expect(teacherPanel.getByText('Lena Learner')).toBeVisible()
  await expect(teacherPanel.getByText('Present')).toBeVisible()
  await teacherPanel.getByRole('tab', { name: 'Chat' }).click()
  await expect(teacherPanel.getByText('Hello from Lena')).toBeVisible()

  // The teacher closes the class: it is Completed, and the learner can no longer join.
  await teacherPanel.getByRole('tab', { name: 'Overview' }).click()
  page.once('dialog', (dialog) => void dialog.accept())
  await teacherPanel.getByRole('button', { name: 'Close class' }).click()
  await expect(teacherPanel.getByText('Class closed.')).toBeVisible()
  await expect(teacherPanel.getByRole('button', { name: 'Join class' })).toHaveCount(0)
  await lenaPage.reload()
  await openMenu(lenaPage, 'Live classes')
  await lenaPage.getByRole('button', { name: 'View details for E2E revision' }).click()
  const afterClose = lenaPage.getByRole('dialog', { name: 'E2E revision' })
  await expect(afterClose.getByText(/can no longer be joined/)).toBeVisible()
  await expect(afterClose.getByRole('button', { name: 'Join class' })).toHaveCount(0)
  await lenaContext.close()
})

test('a class with a waiting room: the learner waits, the teacher admits, and the learner comes in by themselves', async ({ page, browser }) => {
  // The teacher schedules a class with the waiting room left on (the default).
  await signIn(page, TEACHER)
  await openMenu(page, 'Schedule class')
  const panel = page.getByRole('dialog', { name: 'Schedule a class' })
  await panel.getByLabel('Title').fill('Waiting room class')
  await panel.getByLabel('Course').selectOption({ label: 'E2E-101 · Smoke Test Course' })
  await expect(panel.getByLabel(/Learners wait to be let in/)).toBeChecked()
  await panel.getByLabel('Starts').fill(localInput(-5))
  await panel.getByLabel('Ends').fill(localInput(55))
  await panel.getByRole('button', { name: 'Schedule class' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await page.getByRole('button', { name: 'View details for Waiting room class' }).click()
  const teacherPanel = page.getByRole('dialog', { name: 'Waiting room class' })
  await expect(teacherPanel.getByText(/you let learners in/)).toBeVisible()

  // Lena presses Join class and is made to wait: nothing opens, and she is not yet counted present.
  const lenaContext = await browser.newContext({ baseURL: 'http://localhost:5273' })
  const lena = await lenaContext.newPage()
  await signIn(lena, LEARNER)
  await openMenu(lena, 'Live classes')
  await lena.getByRole('button', { name: 'View details for Waiting room class' }).click()
  const lenaPanel = lena.getByRole('dialog', { name: 'Waiting room class' })
  await expect(lenaPanel.getByText(/the host lets you in after you press Join class/)).toBeVisible()
  const popups: string[] = []
  lenaContext.on('page', (opened) => popups.push(opened.url()))
  await lenaPanel.getByRole('button', { name: 'Join class' }).click()
  await expect(lenaPanel.getByRole('status', { name: 'Waiting for the host' })).toBeVisible()
  expect(popups).toEqual([])

  // The teacher sees her waiting (the list refreshes by itself) and admits her.
  const waiting = teacherPanel.getByRole('region', { name: 'Waiting to join' })
  await expect(waiting.getByText('Lena Learner')).toBeVisible({ timeout: 15_000 })
  await teacherPanel.getByRole('tab', { name: 'Attendance' }).click()
  await expect(teacherPanel.getByText('No attendance recorded yet.')).toBeVisible()               // waiting is not attending
  await waiting.getByRole('button', { name: 'Let Lena Learner in' }).click()
  await expect(teacherPanel.getByRole('region', { name: 'Waiting to join' })).toHaveCount(0)

  // Lena comes in on her own, without pressing anything again.
  const popup = await lenaContext.waitForEvent('page', { timeout: 15_000 })
  expect(popup.url()).toContain('liveSession=')
  await popup.close()
  await expect(lenaPanel.getByRole('status', { name: 'Waiting for the host' })).toHaveCount(0)
  await lenaContext.close()
  await page.keyboard.press('Escape')
})

test('a teacher uploads a video, a learner watches it with a signed link and the teacher sees the analytics', async ({ page, browser, request }) => {
  const video = fs.readFileSync(SAMPLE_VIDEO)
  await signIn(page, TEACHER)
  await openMenu(page, 'Video library')
  await expect(page.getByRole('heading', { name: 'Video library' })).toBeVisible()
  await expect(page.getByRole('dialog')).toHaveCount(0)

  // Upload: the form checks before sending, then the file goes up and appears in the list.
  await page.getByRole('button', { name: 'Add video' }).click()
  const add = page.getByRole('dialog', { name: 'Add a video' })
  await add.getByRole('button', { name: 'Upload video' }).click()
  await expect(add.getByRole('alert')).toContainText('Give the video a title')
  await add.getByLabel('Title').fill('E2E lecture')
  await add.getByLabel('Course').selectOption({ label: 'E2E-101 · Smoke Test Course' })
  await add.getByLabel('Video file').setInputFiles({ name: 'lecture.mp4', mimeType: 'video/mp4', buffer: video })
  await add.getByRole('button', { name: 'Upload video' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0, { timeout: 20_000 })
  const row = page.getByRole('listitem').filter({ hasText: 'E2E lecture' })
  await expect(row).toContainText('Smoke Test Course')
  await expect(row).toContainText('122 KB')
  // Conversion runs in the background: the row says so, then shows the poster picture made from the video.
  if (CONVERTS_VIDEOS) await expect(row.locator('img')).toBeVisible({ timeout: 90_000 })
  await expect(page.getByText(/stored/)).toContainText('1 video')

  // A video that is not a video is refused by the server, with its message.
  await page.getByRole('button', { name: 'Add video' }).click()
  const bad = page.getByRole('dialog', { name: 'Add a video' })
  await bad.getByLabel('Title').fill('Not a video')
  await bad.getByLabel('Course').selectOption({ label: 'E2E-101 · Smoke Test Course' })
  await bad.getByLabel('Video file').setInputFiles({ name: 'fake.mp4', mimeType: 'video/mp4', buffer: Buffer.from('this is not a video file at all') })
  await bad.getByRole('button', { name: 'Upload video' }).click()
  await expect(bad.getByRole('alert')).toContainText('do not match')
  await bad.getByRole('button', { name: 'Cancel' }).click()

  // Lena, enrolled in the course, watches it: the player gets a signed link that works without a sign-in header and supports seeking.
  const lenaContext = await browser.newContext({ baseURL: 'http://localhost:5273' })
  const lena = await lenaContext.newPage()
  await signIn(lena, LEARNER)
  await openMenu(lena, 'Video library')
  await expect(lena.getByRole('button', { name: 'Add video' })).toHaveCount(0)
  await lena.getByRole('button', { name: 'View details for E2E lecture' }).click()
  const watching = lena.getByRole('dialog', { name: 'E2E lecture' })
  await expect(watching.locator('video')).toBeVisible()
  const asLinkLena = await apiAs(request, LEARNER)
  const listed = await asLinkLena.get('/api/v1/tenant/videos')
  const videoId = listed.find((item: { title: string }) => item.title === 'E2E lecture').id
  const link = await asLinkLena.get(`/api/v1/tenant/videos/${videoId}/link`)
  // The original file is always reachable by a signed link that needs no sign-in header, and supports seeking.
  const source = CONVERTS_VIDEOS ? link.fallbackUrl : link.url
  expect(source).toContain('/stream?token=')
  const whole = await lenaContext.request.get(source)
  expect(whole.status()).toBe(200)
  expect((await whole.body()).equals(video)).toBe(true)
  const part = await lenaContext.request.get(source, { headers: { Range: 'bytes=100-199' } })
  expect(part.status()).toBe(206)
  expect((await lenaContext.request.get(source + 'tampered')).status()).toBe(404)
  if (CONVERTS_VIDEOS) {
    // The converted version is a master playlist offering several qualities; every address in it carries the token.
    expect(link.kind).toBe('hls')
    expect(link.url).toContain('/hls/master.m3u8?token=')
    const master = await lenaContext.request.get(link.url)
    expect(master.status()).toBe(200)
    const masterText = await master.text()
    expect(masterText.match(/#EXT-X-STREAM-INF/g)).toHaveLength(2)          // the video is 360 pixels tall: 360p and 240p
    const bestQuality = masterText.split('\n').find((line) => line.startsWith('v0-'))!
    const playlist = await lenaContext.request.get(`/api/v1/tenant/videos/${videoId}/hls/${bestQuality}`)
    expect(playlist.status()).toBe(200)
    const text = await playlist.text()
    expect(text).toContain('#EXT-X-ENDLIST')
    const piece = text.split('\n').find((line) => line.startsWith('v0-seg'))!
    expect((await lenaContext.request.get(`/api/v1/tenant/videos/${videoId}/hls/${piece}`)).status()).toBe(200)   // the piece named in the playlist already carries the token
  }

  // Her progress is remembered and shown, and the teacher sees how people watched.
  const asLena = await apiAs(request, LEARNER)
  const videos = await asLena.get('/api/v1/tenant/videos')
  const id = videos.find((item: { title: string }) => item.title === 'E2E lecture').id
  await asLena.post(`/api/v1/tenant/videos/${id}/progress`, { positionSeconds: 4, durationSeconds: 8, watchedSecondsDelta: 4, started: true })
  await lena.reload()
  await openMenu(lena, 'Video library')
  await expect(lena.getByRole('listitem').filter({ hasText: 'E2E lecture' })).toContainText('Watched 50%')
  await lenaContext.close()

  await page.reload()
  await openMenu(page, 'Video library')
  await page.getByRole('button', { name: 'View details for E2E lecture' }).click()
  const manage = page.getByRole('dialog', { name: 'E2E lecture' })
  await manage.getByRole('tab', { name: 'Analytics' }).click()
  await expect(manage.getByText('Started watching')).toBeVisible()
  await expect(manage.getByText('Reached 50%')).toBeVisible()
  // The course is published, so lessons only change in a new version: the video says so.
  await manage.getByRole('tab', { name: 'Lesson' }).click()
  await expect(manage.getByText(/lessons can only change in a new version/)).toBeVisible()

  // Deleting it removes it for everyone.
  await manage.getByRole('tab', { name: 'Details' }).click()
  page.once('dialog', (dialog) => void dialog.accept())
  await manage.getByRole('button', { name: 'Delete video' }).click()
  await expect(page.getByText('The video was deleted.')).toBeVisible()
  await expect(page.getByRole('listitem').filter({ hasText: 'E2E lecture' })).toHaveCount(0)
})

test('a linked video plays inside the page when its host is trusted', async ({ page, browser }) => {
  await signIn(page, TEACHER)
  await openMenu(page, 'Video library')
  await page.getByRole('button', { name: 'Add video' }).click()
  const add = page.getByRole('dialog', { name: 'Add a video' })
  await add.getByRole('radio', { name: 'Link to a video' }).click()
  await add.getByLabel('Title').fill('Linked lecture')
  await add.getByLabel('Course').selectOption({ label: 'E2E-101 · Smoke Test Course' })
  await add.getByLabel('Video link').fill('http://not-secure.example.org/1')
  await add.getByRole('button', { name: 'Add video' }).click()
  await expect(add.getByRole('alert')).toContainText('https')
  await add.getByLabel('Video link').fill('https://www.youtube-nocookie.com/embed/abc123')
  await add.getByRole('button', { name: 'Add video' }).click()
  await expect(page.getByRole('listitem').filter({ hasText: 'Linked lecture' })).toContainText('Linked')

  const lenaContext = await browser.newContext({ baseURL: 'http://localhost:5273' })
  await lenaContext.route('https://www.youtube-nocookie.com/**', (route) => route.fulfill({ contentType: 'text/html', body: '<p>player</p>' }))
  const lena = await lenaContext.newPage()
  await signIn(lena, LEARNER)
  await openMenu(lena, 'Video library')
  await lena.getByRole('button', { name: 'View details for Linked lecture' }).click()
  await expect(lena.getByRole('dialog', { name: 'Linked lecture' }).locator('iframe')).toHaveAttribute('src', 'https://www.youtube-nocookie.com/embed/abc123')
  await lenaContext.close()
})

test('an administrator switches live classes to a pasted meeting link, then to Jitsi', async ({ page, browser }) => {
  const meetingRoom = 'https://meet.example.org/room-123'
  // Classes held elsewhere are real web pages we do not control; answer for them so the test needs no internet.
  const answer = (route: import('@playwright/test').Route) => route.fulfill({ contentType: 'text/html', body: '<h1>Meeting room</h1>' })

  await signIn(page, ADMIN)
  await openMenu(page, 'Integrations')
  await page.getByRole('tab', { name: 'Live classes' }).click()
  await page.getByRole('radio', { name: /Your own meeting link/ }).check()
  await page.getByRole('button', { name: 'Save settings' }).click()
  await expect(page.getByText(/Classes already scheduled keep the tool/)).toBeVisible()

  // The teacher must now paste a link when scheduling.
  const teacherContext = await browser.newContext({ baseURL: 'http://localhost:5273' })
  await teacherContext.route('https://meet.example.org/**', answer)
  const teacher = await teacherContext.newPage()
  await signIn(teacher, TEACHER)
  await openMenu(teacher, 'Schedule class')
  const form = teacher.getByRole('dialog', { name: 'Schedule a class' })
  await form.getByLabel('Title').fill('Zoom revision')
  await form.getByLabel(/Learners wait to be let in/).uncheck()
  await form.getByLabel('Course').selectOption({ label: 'E2E-101 · Smoke Test Course' })
  await form.getByLabel('Starts').fill(localInput(-5))
  await form.getByLabel('Ends').fill(localInput(55))
  await form.getByRole('button', { name: 'Schedule class' }).click()
  await expect(form.getByRole('alert')).toContainText('Paste the meeting link')
  await form.getByLabel('Meeting link').fill(meetingRoom)
  await form.getByRole('button', { name: 'Schedule class' }).click()
  await expect(teacher.getByRole('dialog')).toHaveCount(0)

  // The learner joins and is sent to that meeting.
  const learnerContext = await browser.newContext({ baseURL: 'http://localhost:5273' })
  await learnerContext.route('https://meet.example.org/**', answer)
  const learner = await learnerContext.newPage()
  await signIn(learner, LEARNER)
  await openMenu(learner, 'Live classes')
  await learner.getByRole('button', { name: 'View details for Zoom revision' }).click()
  const popup = learnerContext.waitForEvent('page')
  await learner.getByRole('dialog', { name: 'Zoom revision' }).getByRole('button', { name: 'Join class' }).click()
  const meeting = await popup
  await expect(meeting.getByRole('heading', { name: 'Meeting room' })).toBeVisible()
  expect(meeting.url()).toBe(meetingRoom)
  await meeting.close()

  // Its recording is attached by link (the host agrees first).
  await teacher.getByRole('button', { name: 'View details for Zoom revision' }).click()
  const details = teacher.getByRole('dialog', { name: 'Zoom revision' })
  await details.getByRole('tab', { name: 'Recording' }).click()
  await expect(details.getByRole('button', { name: 'Request recording' })).toHaveCount(0)
  await details.getByRole('button', { name: 'Give recording consent' }).click()
  await details.getByLabel('Recording link').fill('https://meet.example.org/recordings/1')
  await details.getByRole('button', { name: 'Attach recording link' }).click()
  await expect(details.getByRole('link', { name: 'Open recording' })).toHaveAttribute('href', 'https://meet.example.org/recordings/1')
  await learnerContext.close()
  await teacherContext.close()

  // Jitsi: the room is made for each class and no link is asked for.
  await page.getByRole('radio', { name: /Jitsi Meet/ }).check()
  await page.getByRole('button', { name: 'Save settings' }).click()
  await expect(page.getByText(/Classes already scheduled keep the tool/)).toBeVisible()
  const jitsiContext = await browser.newContext({ baseURL: 'http://localhost:5273' })
  const jitsiTeacher = await jitsiContext.newPage()
  await signIn(jitsiTeacher, TEACHER)
  await openMenu(jitsiTeacher, 'Schedule class')
  const jitsiForm = jitsiTeacher.getByRole('dialog', { name: 'Schedule a class' })
  await expect(jitsiForm.getByLabel('Meeting link')).toHaveCount(0)
  await jitsiForm.getByLabel('Title').fill('Jitsi revision')
  await jitsiForm.getByLabel('Starts').fill(localInput(-5))
  await jitsiForm.getByLabel('Ends').fill(localInput(55))
  await jitsiForm.getByRole('button', { name: 'Schedule class' }).click()
  await expect(jitsiTeacher.getByRole('listitem').filter({ hasText: 'Jitsi revision' })).toBeVisible()
  await jitsiContext.close()

  // Back to the placeholder so the other tests see what they expect.
  await page.getByRole('radio', { name: /Placeholder/ }).check()
  await page.getByRole('button', { name: 'Save settings' }).click()
  await expect(page.getByText(/Classes already scheduled keep the tool/)).toBeVisible()
})

test('a LiveKit class is held inside the app: teacher and learner see each other, and attendance is taken', async ({ browser, request }) => {
  // Needs a LiveKit server on this machine: scripts/livekit-dev.ps1 (or the docker run command in the README)
  const up = await request.get('http://localhost:7880').then((response) => response.ok(), () => false)
  test.skip(!up, 'no LiveKit server on localhost:7880')

  const admin = await adminApi(request)
  const saved = await admin.put('/api/v1/tenant/integrations/live-classes', { provider: 'LiveKit', liveKitUrl: 'ws://localhost:7880', liveKitApiKey: 'devkey', liveKitApiSecret: 'secret' })
  expect(saved.ok()).toBeTruthy()
  expect(await saved.text()).not.toContain('"secret"')

  const teacherContext = await browser.newContext({ baseURL: 'http://localhost:5273', permissions: ['camera', 'microphone'] })
  const teacher = await teacherContext.newPage()
  await signIn(teacher, TEACHER)
  await openMenu(teacher, 'Schedule class')
  const form = teacher.getByRole('dialog', { name: 'Schedule a class' })
  await expect(form.getByLabel('Meeting link')).toHaveCount(0)
  await form.getByLabel('Title').fill('Room class')
  await form.getByLabel(/Learners wait to be let in/).uncheck()
  await form.getByLabel('Course').selectOption({ label: 'E2E-101 · Smoke Test Course' })
  await form.getByLabel('Starts').fill(localInput(-5))
  await form.getByLabel('Ends').fill(localInput(55))
  await form.getByRole('button', { name: 'Schedule class' }).click()
  await expect(teacher.getByRole('dialog')).toHaveCount(0)

  await teacher.getByRole('button', { name: 'View details for Room class' }).click()
  const teacherDetails = teacher.getByRole('dialog', { name: 'Room class' })
  await expect(teacherDetails.getByText('Held in the class room inside this app.')).toBeVisible()
  await teacherDetails.getByRole('button', { name: 'Join class' }).click()
  const teacherRoom = teacherDetails.getByRole('region', { name: 'Live class room' })
  await expect(teacherRoom.getByTestId('room-count')).toHaveText('1 in the class')

  const learnerContext = await browser.newContext({ baseURL: 'http://localhost:5273', permissions: ['camera', 'microphone'] })
  const learner = await learnerContext.newPage()
  await signIn(learner, LEARNER)
  await openMenu(learner, 'Live classes')
  await learner.getByRole('button', { name: 'View details for Room class' }).click()
  const learnerDetails = learner.getByRole('dialog', { name: 'Room class' })
  await learnerDetails.getByRole('button', { name: 'Join class' }).click()
  const learnerRoom = learnerDetails.getByRole('region', { name: 'Live class room' })
  await expect(learnerRoom.getByTestId('room-count')).toHaveText('2 in the class')
  await expect(teacherRoom.getByTestId('room-count')).toHaveText('2 in the class')   // each sees the other

  // The other person's camera really arrives: a playing video with a picture size.
  await expect.poll(() => teacherRoom.locator('video').evaluateAll((videos) => videos.filter((v) => (v as HTMLVideoElement).videoWidth > 0).length)).toBeGreaterThanOrEqual(2)

  await learnerRoom.getByRole('button', { name: 'Mute microphone' }).click()
  await expect(learnerRoom.getByRole('button', { name: 'Unmute microphone' })).toBeVisible()

  // Leaving takes the learner out of the room for the teacher too.
  await learnerRoom.getByRole('button', { name: 'Leave class' }).click()
  await expect(learnerDetails.getByRole('button', { name: 'Join class' })).toBeVisible()
  await expect(teacherRoom.getByTestId('room-count')).toHaveText('1 in the class')

  // Attendance was taken when the learner joined.
  await teacherDetails.getByRole('tab', { name: 'Attendance' }).click()
  await expect(teacherDetails.getByText(LEARNER.name).first()).toBeVisible()
  await teacherRoom.getByRole('button', { name: 'Leave class' }).click()
  await learnerContext.close()
  await teacherContext.close()

  // Back to the placeholder so the other tests see what they expect.
  expect((await admin.put('/api/v1/tenant/integrations/live-classes', { provider: 'Local' })).ok()).toBeTruthy()
})

test('the BS date picker opens inside the course panel and the calendar is chosen only in the header', async ({ page }) => {
  await signIn(page, ADMIN)
  await openMenu(page, 'Course authoring')
  await page.getByRole('combobox', { name: 'Calendar' }).selectOption('BS')
  await page.getByRole('button', { name: 'New course' }).click()
  const panel = page.getByRole('dialog', { name: 'New course' })
  await expect(panel.getByRole('combobox', { name: 'Calendar mode' })).toHaveCount(0)   // no per-field switch
  await panel.getByLabel('Available until').click()
  const picker = panel.getByRole('dialog', { name: 'Available until calendar' })
  await expect(picker).toBeVisible()
  // it scrolled itself into view and sits fully inside the panel, not clipped by it
  const [box, around] = [await picker.boundingBox(), await panel.boundingBox()]
  expect(box!.y).toBeGreaterThanOrEqual(around!.y)
  expect(box!.y + box!.height).toBeLessThanOrEqual(around!.y + around!.height + 1)
  await picker.getByRole('button', { name: '15', exact: true }).click()
  await expect(picker).toHaveCount(0)
  await page.keyboard.press('Escape')
  await page.getByRole('combobox', { name: 'Calendar' }).selectOption('AD')   // leave the setting as it was
})

test('the calendar follows the AD/BS setting, steps by month and lists the next 30 days', async ({ page }) => {
  await signIn(page, ADMIN)
  await openMenu(page, 'Calendar')
  await expect(page.getByRole('heading', { name: 'Calendar' })).toBeVisible()
  const title = page.getByText(/^[A-Za-z]+ \d{4}( BS)?$/).first()
  await expect(title).not.toContainText('BS')                                  // AD by default
  await page.getByRole('combobox', { name: 'Calendar' }).selectOption('BS')
  await expect(title).toContainText('BS')                                      // the whole month, not just the day numbers
  const bsTitle = (await title.textContent())!
  await page.getByRole('button', { name: 'Next month' }).click()
  await expect(title).not.toHaveText(bsTitle)
  await page.getByRole('button', { name: 'Today' }).click()
  await expect(title).toHaveText(bsTitle)
  await page.getByRole('tab', { name: /Next 30 days/ }).click()
  await expect(page.getByText(/Nothing scheduled in the next 30 days|Zoom revision|Jitsi revision|E2E revision/).first()).toBeVisible()   // the class scheduled earlier is coming up
  await expect(page.getByRole('button', { name: 'Next month' })).toHaveCount(0)
  await page.getByRole('combobox', { name: 'Calendar' }).selectOption('AD')    // leave the setting as it was
})

test('forgotten password: the reset page answers the same way for any address', async ({ page }) => {
  await openLogin(page)
  await page.getByRole('button', { name: 'Forgot your password?' }).click()
  await page.getByLabel('Organization (tenant slug)').fill('e2e')
  await page.getByLabel('Email').fill('nobody@e2e.test')
  await page.getByRole('button', { name: 'Send reset code' }).click()
  await expect(page.getByRole('status')).toContainText('If that address belongs to an account')
})

test('every page in the menu opens without script or server errors', async ({ page }) => {
  const problems = watchForProblems(page)
  await signIn(page, ADMIN)
  const nav = page.getByRole('navigation', { name: 'Primary' })
  // Menu items are the buttons that do not toggle a submenu; click them by position so same-named headings cannot interfere.
  const items = nav.locator('button:not([aria-expanded])')
  const labels = (await items.allInnerTexts()).map((raw) => raw.replace(/^[^\p{L}\p{N}]+/u, '').trim()) // drop the icon
  expect(labels.length).toBeGreaterThan(30)
  expect(labels.filter((label) => !label), `menu items without a name: positions ${labels.map((l, i) => (l ? -1 : i)).filter((i) => i >= 0)}`).toEqual([])
  const opened: string[] = []
  for (const [index, label] of labels.entries()) {
    if (await page.getByRole('dialog').count()) await page.keyboard.press('Escape') // a page may open a panel (Schedule class does); it covers the menu
    await items.nth(index).click()
    await expect(page.locator('main h1, main h2, main h3').first(), `the "${label}" page shows a heading`).toBeVisible()
    await page.waitForLoadState('networkidle')
    opened.push(label)
  }
  expect(opened.length).toBe(labels.length)
  expect(problems).toEqual([])
})
