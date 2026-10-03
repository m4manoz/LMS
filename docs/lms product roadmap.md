# LMS Product Roadmap (Phase 13 onward)

Decisions (confirmed):
1. Market: online classes for all kinds of institutions (generic multi-tenant platform, not school-only). Live classes are therefore core, not optional; the parent portal is optional per tenant.
2. UI kit: shadcn/ui (Tailwind + Radix).
3. Payment and email providers: configurable per tenant behind provider abstractions, with no single vendor hard-coded.
4. Hosting: cloud (container deployment, managed PostgreSQL, S3-compatible object storage, managed secret store).

Goal: turn the phase 0-12 baseline into a product a school can run a term on. Each milestone ships with tests (InMemory API tests, tenant-isolation tests, frontend build check) and a UAT script.

## M0 - Cleanup and test foundation (1 week)
- Done: production secret guard, removed hard-coded script password, port/README fixes, WebApplicationFactory test harness (11 tests).
- Add: permission, multi-role, refresh-token, idempotency and audit tests; PostgreSQL Testcontainers suite (migrations, constraints, concurrent enrollment); GitHub Actions/CI running `dotnet test` and `npm run build`; `git init` and a `.sln`.

## M1 - Frontend rebuild (3-4 weeks)
- Adopt a UI kit (e.g. shadcn/ui or MUI) plus TanStack Query, a form library and an i18n layer (English and Nepali).
- Replace phase-named pages with feature pages: Dashboard, Courses, CourseAuthoring, Learning, Assessments, Assignments, Reports, Certificates, LiveClasses, Admin.
- Role-specific dashboards: learner (my courses, deadlines, progress), teacher (to grade, class overview), admin (enrollment, engagement, alerts).
- Add Vitest + React Testing Library, ESLint, and Playwright smoke tests for sign-in, enroll, submit.

## M2 - Core learning loop (4-5 weeks)
- Course content blocks (text, image, PDF, video, link, code) with chapter/topic/activity editors; categories, tags, prerequisites.
- Object storage (MinIO locally, S3-compatible in production) with upload sessions.
- Assignments and submissions with file upload, due dates, late policy, feedback.
- Gradebook: per-course and per-learner grades, weighting, export.
- Enrollment: invitations with acceptance tokens, cohorts/classes, waitlist promotion, drip and prerequisite rules.
- Discussions/forums per course, announcements, calendar of deadlines.

## M3 - Assessment depth (2-3 weeks)
- Rubrics, question pools and randomization, assessment versioning, accommodations (extra time), file-upload answers.

## M4 - People and communication (3 weeks)
- Phase 1 gaps: guardian relationships with consent, parent portal (read-only progress, grades, attendance), invitations, password reset, MFA readiness, campus/class scope.
- Email provider (and SMS if needed) behind the existing notification queue; deadline reminders; configurable report builder.

## M5 - Provider integrations (3-4 weeks)
- Priority raised: live online classes are the core product for this market, so consider moving the LiveKit adapter ahead of M3/M4.
- Live classes: LiveKit adapter (schedule, join, attendance import, recordings).
- AI: one real provider adapter behind `IAiProvider`, with budget limits, evaluation set and content filters; human approval stays mandatory.
- Search: PostgreSQL full-text catalog search; pgvector for grounded AI retrieval.

## M6 - Commercial and operations (3-4 weeks)
- Fees/payments via a provider abstraction (local gateways such as eSewa/Khalti if Nepal), invoices, refunds.
- Phase 13 items: managed secret-provider connector with rotation, OpenTelemetry/Prometheus export with SLO dashboards, scheduled backup/restore rehearsals.
- PWA pilot once the telemetry gates in `phase12 device pilot decision.md` are met.

## Planned navigation modules (status)
Menu entries that exist in the app, and what backs them:

| Menu entry | Status | Notes |
|---|---|---|
| Course categories, Learning paths, Learning resources | Done (catalog API + UI + tests) | Link resources only; file upload still goes through course assets |
| Announcements | Done (API + UI + tests) | Tenant-wide or per-course, pinning and expiry; in-app notifications fan out to the audience |
| Messages | Done (API + UI + tests) | Direct messages and per-course chats with unread counts and a header icon; learners can only start chats with staff (setting Messaging:AllowLearnerToLearner); polling, not live push; no attachments, editing or deletion yet |
| Forums | Done (API + UI + tests) | Threads, replies, pin/lock/delete moderation; no edit history or attachments yet |
| My tasks | Done (API + UI + tests) | One feed of assignments, assessments, live classes and grading work; to-do/completed tabs, grouped by urgency |
| Calendar | Done (month + agenda views) | Shows assignment deadlines and live classes, AD/BS day numbers; assessments have no deadlines yet, so they are not on it |
| Organizations / tenants | Planned (M6) | Platform-level tenant management; today tenants are provisioned by API key |
| Integrations | Email done; others planned | Per-tenant SMTP or log provider, protected password or secret-store reference, test email, delivery log; payments, video, AI and storage providers to follow the same pattern |
| Assignments | Done (API + UI + tests) | Text/file submissions, deadlines, late policy with penalty, resubmission until graded, grading with feedback; no rubrics, group work or plagiarism checks yet |
| Gradebook | Done (API + UI + tests) | Class matrix, learner My grades with feedback, CSV export; unweighted by default; per-course weighted categories, organization grade scales with letters/points, pass marks and an extended CSV (v2) |
| Notifications | Done for in-app (queue, fan-out, reminders, header bell) | Announcements, new assignments and grades notify; a worker sends one deadline reminder per learner per assignment (24 h ahead, configurable); email copies go out through the tenant's provider with retry and dead-letter; per-person in-app and email preferences; SMS not built |
| Course content blocks | Done (API + authoring UI + learner view + tests) | Text, code, link, embed (allow-listed hosts), image, PDF, video, audio and download blocks per lesson; ordered, draft-only editing; upload type + signature checks; files load through authenticated fetch; storage provider is now Local or S3-compatible (see docs/storage.md) |
| File storage (S3-compatible) | Done (adapter + signed links + readiness + tests incl. a real server) | Deployment-level provider with tenant-prefixed keys, local fallback, streaming through short-lived signed links; no per-tenant buckets, object deletion or virus scanning yet |
| Cohorts | Done (API + UI + tests) | Groups of learners; enroll or invite a whole cohort with a per-person outcome report |
| Course invitations | Done (API + UI + tests) | Email-addressed, one-time token (hashed in storage), accept/decline, expiry, resend. People without an account are emailed a sign-up link and code (sent directly, never queued, so the code is not stored in clear text), can look up the invitation and register through public rate-limited endpoints, and are enrolled on registration. Gaps: a person whose email already has an account in another organization cannot join this one |
| Course versioning | Done (API + UI + tests) | A published course is edited through a draft copy; modules and lessons can be added, renamed, reordered and deleted there. Review then publish swaps it in and moves progress, notes, bookmarks, module rules, assessments and files onto matching lessons. Learners who have finished everything left complete at publish; enrolled learners get a "course updated" notice with the change summary. Discard is safe |
| Password reset | Done (API + UI + tests) | Emailed single-use code (hashed, 60 minutes, one email a minute per person), identical answers for unknown accounts, link built only from `App:PublicUrl`, signs the person out of every organization. Needs email set up for the organization; no change-password-while-signed-in yet |
| Live class provider | Done (API + UI + tests) | Per organization: your own meeting link (Zoom, Meet, Teams), Jitsi (public or own server), or a placeholder. Attendance, chat, polls and announcements stay in the LMS; recordings made in the meeting tool are attached by link. Not yet: recording by the LMS itself |
| Public landing page and applications | Done (API + UI + tests, e2e) | A Coursera-style front page per organization: search, categories, banners, course rows (newest, popular, a category or chosen courses), reasons, numbers, learner stories, FAQ and footer. Visitors open a course, see its outline and apply (no account needed; a hidden-field and rate-limit guard against bots). Staff approve an application, which sends a course invitation (email, or a link and code to share), or decline it. Staff control all the words, banners, rows and footer links under Administration → Landing page, with link safety checks and a reset to the standard page. Not yet: images and logos, ratings, a fee/checkout, custom domains |
| Waiting room for live classes | Done (API + UI + tests, e2e) | A per-class setting (on by default when scheduling): learners press Join class, wait, and the host admits them one by one or all at once, or declines. They come in by themselves when admitted; a room token is not handed out until then. |
| Enrolling learners | Done (API + UI + tests, e2e) | Enroll learners page and the course Enrollment tab: search people, enroll several at once (waitlist and prerequisites respected), see who is enrolled, remove. |
| LiveKit classroom | Done (API + UI + tests, e2e against a real LiveKit server) | Per organization: a LiveKit server address, API key and secret (secret encrypted, never returned). Classes are held in a room inside the app: camera, microphone, screen sharing; tokens are made per person at join time (teachers are room admins); attendance is taken on join and leave. Class recordings: the host starts and stops recording in the app (everyone in the room is shown that it is being recorded); LiveKit's recording service (Egress) makes one MP4 of the whole room, which is brought into the video library as a class recording, converted to streaming qualities and transcribable like any video. The file goes to a shared folder or straight into the S3 bucket. Not yet: webhook-based attendance, hand-raise/mute-others controls, separate tracks per person, automatic start/stop with the class |
| Video library | Done (API + UI + tests) | Course videos: upload (progress bar, size limit configurable via `Videos:MaxMegabytes`), link external videos, signed short-lived playback links (object-storage links or signed API stream with range requests), resume, watch progress and analytics, add to a lesson, delete with file cleanup, storage usage. Class recordings from LiveKit arrive here automatically. Not yet: recordings of Jitsi/Zoom classes (they are attached by link) |
| Video streaming (HLS) and posters | Done (API + UI + tests, e2e with real FFmpeg and playback in Chrome) | With `Videos:Processing:Enabled`, uploads go through a background worker that runs FFmpeg (installed, or in a container via `Videos:Ffmpeg:DockerImage`) to make HLS in several qualities (1080/720/480/360/240p, only those no taller than the video) behind a master playlist, a poster, and to read the length. Playback uses hls.js with a quality picker (Auto or a fixed quality; iPhone Safari plays HLS itself) and a signed playlist whose every piece carries the token, falling back to the original file. The transcript is offered as captions in the player. Failed conversions show a reason and can be retried; older uploads (single playlist) keep playing and can be converted again. Not yet: serving pieces straight from object storage or a CDN, captions for videos played from object-storage links, several caption languages |
| Video AI: transcripts, search, summaries, questions | Done (API + UI + tests, e2e with a pretend AI service) | Per organization: built-in (nothing leaves the system) or any OpenAI-compatible service (key stored encrypted). Transcripts are pasted (WebVTT/SRT) or made from the sound; learners search what is said and jump to the moment, read along, and use summaries and practice questions that staff publish after checking them. Published practice questions can be turned into a graded multiple-choice quiz in the course's assessments (draft or published; points, attempts and time limit chosen). Not yet: translating transcripts, a local speech-to-text container, keeping explanations and the video moment on the quiz questions |
| Capacity, waitlist and promotion | Done | FIFO promotion on withdrawal and capacity increase, manual promote, withdraw; also fixed a bug that let a withdrawn learner re-enroll past capacity |
| Prerequisites and drip rules | Done | Course prerequisites (must be completed, loop-checked); per-module release after N days, on a date, or after finishing an earlier module; enforced in the player, progress and content |
| Instructors, Learners | Reuse Access control | Filter of the Users tab; dedicated profile views come with M4 |

## Order of work
M0 -> M1 and M2 in parallel (frontend and backend) -> M3 -> M4 -> M5 -> M6. MVP for a first pilot school is M0-M4.

## Open decisions
1. First pilot customer and institution type.
2. Whether to keep the PWA-only mobile strategy.
3. Default payment/email providers to ship adapters for first.
4. AI provider and data-handling policy for learner data (children's data is restricted).
5. Cloud vendor (AWS, Azure or GCP).

Estimates assume one or two developers and are rough.
