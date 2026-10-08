# Learning Management System

This project follows the implementation plan in [docs/lms implementation plan.md](docs/lms%20implementation%20plan.md).

## Implemented phases

### Phase 0 foundation

The initial foundation includes:

- ASP.NET Core on .NET 10 with Minimal APIs
- PostgreSQL and EF Core support, with an InMemory local default
- Tenant provisioning protected by `X-Platform-Key`
- Tenant resolution from `X-Tenant-Slug` or a tenant subdomain
- Tenant claim validation middleware
- JWT validation and a tenant-scoped context endpoint
- Correlation IDs and health endpoints
- React 19 + TypeScript + Vite frontend shell
- Tenant-scoped login, refresh-token rotation, and current-user endpoint
- Default tenant roles and permission claims
- Tenant user creation with learner, teacher, and guardian profiles
- Guardian-to-learner relationships
- Global AD/BS calendar preference with calendar-enabled date fields

### Phase 2 course catalog and authoring

- Tenant-scoped `Course`, `CourseVersion`, module, chapter, lesson, topic, activity, category, tag, and asset models.
- Course lifecycle: draft, in review, published, and archived.
- Permission-controlled catalog, authoring, review, publication, and archive endpoints.
- Course outline authoring for modules and lessons.
- Local content-asset storage abstraction with metadata, SHA-256, and 100 MB upload limit.
- React course workspace with AD/BS availability date fields and permission-aware workflow actions.
- PostgreSQL migration: `Phase2CourseCatalog`.

### Phase 3 enrollment, delivery, and progress

- Self-service and administrator enrollment with active, invited, waitlisted, completed, suspended, and withdrawn states.
- Learner course player with module/lesson navigation and resume position.
- Immutable progress events plus current lesson-progress projections.
- Automatic course completion projection and progress percentage.
- Learner bookmarks and private lesson notes.
- Permission-controlled enrollment and progress APIs.
- PostgreSQL migration: `Phase3EnrollmentProgress`.

### Phase 4 assessments and grading

- Tenant-scoped question banks, typed questions, and assessment versions.
- Multiple choice, multiple response, true/false, short answer, essay, and file-upload question types.
- Assessment draft/published/archived lifecycle.
- Attempt limits, learner answers, submission state, automatic objective grading, and teacher review.
- Learner answer-key protection and grade visibility controls.
- Teacher/admin attempt review and manual grade finalization.
- React assessment authoring, learner attempt, and grading workspace.
- PostgreSQL migration: `Phase4AssessmentsGrading`.

### Phase 5 notifications, certificates, and reporting

- In-app notification templates, preferences, durable queue, retries, dead-letter state, and hosted dispatcher.
- Enrollment, course completion, assessment grading, and certificate notifications.
- Certificate templates, issuance, revocation, transcript entries, and public verification paths.
- Tenant-scoped overview/progress reports and enrollment/grade CSV exports.
- React operations workspace for notifications, certificates, metrics, and exports.
- PostgreSQL migration: `Phase5NotificationsCertificatesReports`.

### Phase 6 AI services with human control

- Provider-neutral `IAiProvider` seam with a deterministic local provider for development and tests.
- Durable tenant-scoped AI jobs with queued, processing, completed, failed, and dead-letter states.
- Hosted worker with bounded retries, queue protection, prompt/content length limits, and PII redaction.
- Grounding restricted to published course lessons, with stored lesson citations for every generated output.
- Lesson summaries, question drafts, flashcard drafts, translation drafts, and tutor explanations.
- Teacher/admin approve, reject, review, and regenerate actions; AI outputs never publish automatically.
- React AI workspace with status, citations, review history, and provider/model visibility.
- PostgreSQL migration: `Phase6AiServices`.

### Phase 7 live classes, menus, and security/RBAC

- Role-aware enterprise navigation split into Workspace, Classroom, Learning, Assessment, AI Workspace, Communication, Analytics, and Administration sections with expandable submenus.
- Calendar is a Workspace destination while system configuration is under Administration; instructors manage content through the Course Catalog and Administration > Courses.
- Live class scheduling through a provider adapter, local meeting metadata, join/leave attendance, announcements, chat, polls, hand-raise, recording consent, and recording lifecycle processing.
- Tenant-scoped event streaming refreshes collaboration data without a frontend realtime dependency; recordings use bounded retries and configurable retention expiry.
- Tenant-scoped custom role creation/update, user role assignment, permission catalog, API authorization policies, and security audit events.
- PostgreSQL migrations: `Phase7LiveClassesSecurityRbac` and `Phase7LiveClassHardening`.

### Phases 8–12 hardening and advanced learning

- **Phase 8:** multi-role RBAC, protected request metrics, assessment time limits, capacity-aware enrollment with waitlist.
- **Phase 9–10:** recommendations, gamification, virtual labs (signed launch tokens), PWA shell and offline manifests.
- **Phase 11:** device-bound encrypted offline packages, signed lab webhooks, telemetry summaries.
- **Phase 12:** secret store abstraction, durable Data Protection keys, retention worker, operations dashboard, backup/restore rehearsal (`scripts/backup-restore.ps1`) and release gates (`scripts/phase12-release-gates.ps1`, needs `LMS_GATE_PASSWORD`).

In Production the API refuses to start with the default signing key, provisioning key or database password; supply them via environment variables or the secret store. See `docs/lms implementation plan.md` for the Phase 13 roadmap.

## Run the API

```powershell
dotnet run --project src/Lms.Api/Lms.Api.csproj --urls http://localhost:5106
```

## Run the frontend

```powershell
Set-Location src/Lms.Web
npm install
npm run dev
```

Open `http://localhost:5173`.

## Provision a local tenant

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri http://localhost:5106/api/v1/platform/tenants `
  -Headers @{ 'X-Platform-Key' = 'local-development-only-change-me' } `
  -ContentType 'application/json' `
  -Body '{"name":"Acme Academy","slug":"acme"}'
```

### Manage organizations (platform console)

The platform operator manages every organization from `http://localhost:5173/#/platform`. It asks for the platform key (`Platform:ProvisioningKey`, kept in that browser tab only) and lists organizations with their status, people and courses. From there you can create an organization with its first administrator, open one to see its administrators and website addresses, rename it, and suspend, activate or archive it. Suspending or archiving keeps all data, stops the organization resolving, closes its public page and signs everyone out; activating brings it back.

The same actions are available to scripts, all with the `X-Platform-Key` header:

- `GET /api/v1/platform/tenants?q=&status=` — list (status: Active, Suspended, Archived)
- `GET /api/v1/platform/tenants/{slug}` — details with counts, administrators and addresses
- `PUT /api/v1/platform/tenants/{slug}` — rename (`{ "name": "..." }`; the short name never changes)
- `POST /api/v1/platform/tenants/{slug}/suspend`, `/activate`, `/archive`

Each change is recorded in the organization's security audit log. Not yet: deleting an organization, per-organization plans or limits, and signing the operator in with an account instead of the key.

### Browser tests

`cd srcLms.Web ; npm run e2e` runs the Playwright smoke tests in Chromium (first time only: `npx playwright install chromium`). They start their own API (in-memory database on port 5299, built into `srcLms.Web.e2e`) and web server (port 5273), so they never touch your development database or a running dev API. They cover sign-in, categories, authoring a course through review, publishing and a second version, a learner's view, password reset, and open every menu page checking for script and server errors. A failed run leaves screenshots and a trace in `srcLms.Web	est-results`.

### Demo data

With the API running, `.scriptsseed-demo-data.ps1` creates the `acme` organization and administrator if they are missing, six course categories and nine courses in every authoring state (published, published with a new version in progress, in review, complete drafts, an incomplete draft, and a draft with no outline). It is safe to run again; existing items are skipped. Local development only.

The frontend provides the authenticated shell, calendar-enabled academic date fields, course authoring, enrollment, learner delivery, assessment, and operations workspaces.

## Phase 1 identity endpoints

- `POST /api/v1/auth/login` — tenant-scoped login
- `POST /api/v1/auth/refresh` — rotate a refresh session
- `POST /api/v1/platform/tenants/{slug}/bootstrap-admin` — create the first tenant administrator
- `GET /api/v1/tenant/me` — current authenticated user and permissions
- `GET /api/v1/tenant/roles` — list tenant roles
- `GET /api/v1/tenant/users` — list tenant users
- `POST /api/v1/tenant/users` — create a user and optional learner/teacher/guardian profile
- `POST /api/v1/tenant/guardian-links` — link a guardian to a learner

## Phase 2 course endpoints

- `GET /api/v1/tenant/courses` and `GET /api/v1/tenant/courses/{id}` — catalog and outline; learners see published courses only
- `POST /api/v1/tenant/courses` and `PUT /api/v1/tenant/courses/{id}` — create/update draft metadata
- `POST /api/v1/tenant/courses/{id}/modules` — add a module
- `POST /api/v1/tenant/courses/{id}/modules/{moduleId}/lessons` — add a lesson
- `POST /api/v1/tenant/courses/{id}/submit-review` — submit a draft for review
- `POST /api/v1/tenant/courses/{id}/publish` — publish an approved version
- `POST /api/v1/tenant/courses/{id}/archive` — retire a course
- `POST /api/v1/tenant/courses/{id}/assets` and `GET .../assets/{assetId}` — upload/download content assets

When PostgreSQL is enabled, apply the checked-in migration with `Database:ApplyMigrations=true` during a controlled deployment.

## Phase 3 learning endpoints

- `GET /api/v1/tenant/enrollments` — list the current learner's enrollments or all enrollments for enrollment managers
- `POST /api/v1/tenant/enrollments` — administrator enrollment
- `POST /api/v1/tenant/courses/{id}/enroll` — self-enrollment in a published course
- `GET /api/v1/tenant/courses/{id}/learning` — resumable learner course player
- `POST /api/v1/tenant/courses/{id}/learning/lessons/{lessonId}/progress` — save position or complete a lesson
- `GET/POST /api/v1/tenant/courses/{id}/learning/bookmarks` — learner bookmarks
- `GET/POST/PUT /api/v1/tenant/courses/{id}/learning/notes` — private learner notes

## Phase 4 assessment endpoints

- `GET /api/v1/tenant/courses/{id}/assessments` — list published learner assessments or all author assessments
- `POST /api/v1/tenant/courses/{id}/assessments` — create an assessment draft
- `POST /api/v1/tenant/assessments/{id}/questions` — add a typed question
- `POST /api/v1/tenant/assessments/{id}/publish` — publish an assessment
- `POST /api/v1/tenant/assessments/{id}/attempts` — start a learner attempt
- `PUT /api/v1/tenant/assessment-attempts/{id}/answers/{questionId}` — save an answer
- `POST /api/v1/tenant/assessment-attempts/{id}/submit` — submit and automatically grade objective questions
- `GET /api/v1/tenant/assessments/{id}/attempts` and `POST /api/v1/tenant/assessment-attempts/{id}/grade` — teacher review and grading (graders only; a learner can open only their own attempt)

### Assessment depth

- **Edit:** `PUT /assessments/{id}` (title, limits, shuffle settings); `PUT` and `DELETE /assessments/{id}/questions/{questionId}` while the assessment is a draft or a new version is being edited.
- **Versions:** a published assessment is changed through `POST /assessments/{id}/versions` (a copy of its questions). Learners keep the live version until `POST /assessments/{id}/publish` makes the copy live; `DELETE /assessments/{id}/versions/draft` discards it. An attempt stays on the version it started on, and the assessment keeps its identity, so gradebook entries do not move.
- **Pools and shuffling:** a question's `pool` name groups alternatives; `PUT /assessments/{id}/pools/{name}` sets how many each attempt draws (questions in a pool must be worth the same points). `shuffleQuestions` and `shuffleOptions` mix the order per learner. Each attempt stores the questions and option order it was dealt, so reopening it shows the same thing.
- **Rubrics:** `GET`/`POST /courses/{id}/rubrics`, `PUT`/`DELETE /rubrics/{id}`. An essay or file question with a `rubricId` is worth the rubric's total. Grade it with `POST …/grade` and `answers: [{ questionId, criterionScores: [{ criterionId, points }], feedback }]` (plain questions take `scorePoints`); sending only `scorePoints` still grades the whole attempt with one total. A rubric that questions use cannot be changed or deleted; copy it instead.
- **Accommodations:** `GET /courses/{id}/accommodations`, `PUT`/`DELETE /courses/{id}/accommodations/{learnerUserId}` (`extraTimePercent` 0–200, `extraAttempts` 0–10, a staff-only `note`). They apply to every assessment in the course, are fixed into each attempt when it starts, and are audited.
- **File answers:** `POST`/`DELETE`/`GET /assessment-attempts/{id}/answers/{questionId}/file` for file-upload questions (25 MB, executable and web-page types refused). The learner and graders can download; nobody else can.
- **Short answers** now count as correct when the typed text matches any one accepted answer.

## Assignments, ratings and pictures

- **Assignments:** `POST /assignments` takes an optional `rubricId` (the assignment is then worth the rubric's total) and `isGroup`. Grade a rubric assignment with `criterionScores: [{ criterionId, points }]`. For group work, `GET`/`POST /assignments/{id}/groups`, `PUT`/`DELETE …/groups/{groupId}` (members must be enrolled, one group each; locked once the group has submitted). `GET /assignments/{id}/similarity?threshold=30` lists pairs of learners whose work shares a lot of wording (graders only).
- **Assessment deadlines:** `opensAtUtc` and `dueAtUtc` on an assessment; no attempt can start outside them and an attempt ends at the deadline. They appear in the learner's task feed and the calendar.
- **Invitations:** `POST /invitations/public/register` with an existing account's `password` (and no name) joins an organization with that account; `lookup` reports `hasAccount` and `isMember`.
- **Ratings:** `GET`/`PUT`/`DELETE /courses/{id}/rating` for the learner; `GET /courses/{id}/ratings` and `POST /ratings/{id}/hide|show` for staff. The public course card has `ratingAverage` and `ratingCount`; the public course has the distribution and reviews.
- **Landing pictures:** `GET`/`POST /tenant/landing/images`, `DELETE …/{id}`; the page content refers to them by `logoImageId`, `heroImageId` and each banner's `imageId`; they are served without sign-in from `GET /api/v1/public/{slug}/landing-images/{id}`.
- **Storage:** per-organization buckets (`Storage:S3:TenantBuckets`) and virus scanning (`Storage:VirusScan:*`) are described in `docs/storage.md`.

## Messages and forums

- **Messages** (`/api/v1/tenant/messages`): `POST /conversations/{id}/messages` (JSON) or `…/messages/upload` (multipart `file` and optional `body`) to send; `PUT` and `DELETE …/messages/{messageId}` to edit or delete your own (a moderator may delete anyone's in a course chat); `GET …/messages/{messageId}/attachment` to download.
- **Live updates:** `GET /messages/stream` is a server-sent-event stream. Each event is `{ type: message | edited | deleted, conversationId, messageId }` with no message text; fetch the messages through the normal calls. The connection ends after 5 minutes (the app reconnects) and sends a ping every 25 seconds. If you run behind a reverse proxy, turn response buffering off for this path (the server sends `X-Accel-Buffering: no`). The channel lives in one server's memory, so with several servers a person hears only about messages handled by the server they are connected to; the app polls as a fallback.
- **Forums** (`/api/v1/tenant/community`): `PUT /threads/{id}` and `PUT /replies/{id}` edit a post and keep the old wording; `GET …/history` shows those earlier versions to the author and moderators. `POST /threads/{id}/attachments` (multipart `file`, optional `replyId`; 5 per post), `GET` and `DELETE /attachments/{id}`.
- Attachments are limited to 25 MB and exclude executable and web-page file types.

## Phase 5 operations endpoints

- `GET /api/v1/tenant/notifications` and `POST /api/v1/tenant/notifications/{id}/read` — in-app notifications
- `GET/PUT /api/v1/tenant/notification-preferences` — per-user notification preferences
- `GET/POST /api/v1/tenant/certificate-templates` — certificate templates
- `GET /api/v1/tenant/certificates` and `POST /api/v1/tenant/enrollments/{id}/certificate` — certificates
- `POST /api/v1/tenant/certificates/{id}/revoke` — revoke a certificate
- `GET /api/v1/tenant/transcript` — transcript entries
- `GET /api/v1/public/certificates/{verificationCode}` — public certificate verification
- `GET /api/v1/tenant/reports/overview` and `/course-progress` — learning metrics
- `GET /api/v1/tenant/reports/enrollments.csv` and `/grades.csv` — CSV exports

## Phase 6 AI endpoints

- `GET /api/v1/tenant/ai/jobs` and `GET /api/v1/tenant/ai/jobs/{id}` — current user's jobs or all jobs for AI managers
- `POST /api/v1/tenant/ai/jobs` — queue a grounded AI draft for a published course
- `POST /api/v1/tenant/ai/outputs/{id}/reviews` — approve or reject an output with optional review notes
- `POST /api/v1/tenant/ai/outputs/{id}/regenerate` — queue a fresh draft from the same published source

AI output is a draft until an authorized teacher or administrator approves it. The local provider is intentionally deterministic; production model adapters should enforce organization-level provider keys, budgets, retention, and evaluation policies.

## Phase 7 live class and security endpoints

- `GET/POST /api/v1/tenant/live-classes/sessions` — list and schedule live sessions
- `POST /api/v1/tenant/live-classes/sessions/{id}/join` and `/leave` — join/leave and record attendance
- `GET /api/v1/tenant/live-classes/sessions/{id}/attendance` — attendance reconciliation
- `GET/POST .../announcements` and `GET/POST .../chat` — session collaboration
- `GET .../events` — authenticated tenant-scoped collaboration refresh stream
- `POST .../consent`, `GET .../recording`, `POST .../recording/request`, and `POST .../recording/retry` — consent-aware recording lifecycle
- `GET/POST .../polls`, `.../polls/{pollId}/vote`, and `.../hand-raise` — interactive session tools
- `POST /api/v1/tenant/roles`, `PUT /api/v1/tenant/roles/{id}` — manage custom roles
- `PUT /api/v1/tenant/users/{id}/role` — assign a tenant role
- `GET /api/v1/tenant/security/permissions` and `/audit-events` — permission catalog and security audit history

## The public front page

An organization is always known before anyone signs in. The app runs in one of two modes, decided by the address it is opened at (`GET /api/v1/public/site?host=...`):

- **Organization website.** The address belongs to one organization, so its landing page is the home page and sign-in never asks for the organization. An address belongs to an organization when the platform operator gives it one (`PUT /api/v1/platform/tenants/{slug}/domains` with `{ "host": "learn.school.edu" }`, `X-Platform-Key` required; `GET` lists and `DELETE .../domains/{host}` removes), or when it is a subdomain of the platform (`school.platform.com` is `school`; addresses listed in `Tenancy__PortalHosts`, and `www`, are excluded). A single-organization install can set `Public__DefaultTenantSlug=acme` so every address shows that organization. Point the custom domain’s DNS at the same app, with TLS. The demo seed script gives Acme `http://acme.localhost:5173` (browsers resolve `*.localhost` to your machine), while `http://localhost:5173` stays the shared portal that asks for an organization. An organization address is a small website: Home, Courses (a catalog with search, category and sort filters kept in the address), a page for each course, and About, at `#/`, `#/courses?category=…&q=…&sort=…`, `#/course/{id}` and `#/about`.
- **Shared portal.** Any other address is the portal: visitors type their organization to sign in, or to preview its courses (also `/?org=acme`); the browser remembers the last one.

Each organization has a public page with courses to browse and search, and an Apply form that needs no account.

Staff control the page under Administration → **Landing page** (headline, banners, course rows, reasons, numbers, stories, FAQ, footer) and handle applications under Courses → **Applications**: approving one sends a course invitation. Set `App__PublicUrl` so invitation links point at your site.

## Video library: uploads, study tools and organizing

- **Uploading in pieces.** A video is sent as pieces (`Videos:ChunkMegabytes`, default 8) and joined at the end, so a lost connection or a closed tab does not start the upload over: choosing the same file again carries on from the pieces already stored. A piece that fails is tried again a few times. The first piece shows what the file really is, so a file that is not a video is refused at once; the joined file is checked like any upload (virus scanner, bucket). Unfinished uploads are cleared after `Videos:UploadExpiryHours` (default 48). `Videos:MaxMegabytes` still limits one video and `Videos:QuotaMegabytes` (default none) limits what an organization may keep in total.
- **Watching.** Playback speed (remembered), picture in picture, and keys: space or K play and pause, J and L skip 10 seconds, the arrow keys 5, `<` and `>` change the speed, M mutes, F is full screen, 0–9 jump through the video. Staff give a video chapters (one line each, `2:30 The main idea`), shown to learners as an outline that follows playback. Learners keep notes and bookmarks at moments of a video; only they can see them.
- **In lessons.** A video block whose file is a library video plays in the library's player (streaming, captions, resume, chapters and notes) and counts as watched. In the lesson's content editor, "Complete this lesson when its videos have been watched" makes the lesson complete by itself, with course progress, completion and points following, once the learner has finished every library video in it. The setting belongs to the lesson's version like all content.
- **Organizing.** Tags (up to 10 a video), search that also looks in tags, filters for tag and state, five orders, select several and delete.

## Video streaming and AI

Uploaded videos can be converted for streaming (HLS pieces and a poster). Turn it on with FFmpeg installed, or run it in a container so nothing is installed:

```
Videos__Processing__Enabled=true
Videos__Ffmpeg__DockerImage=linuxserver/ffmpeg     # or Videos__Ffmpeg__Command=/path/to/ffmpeg
```

Without it, uploads are ready at once and play as the original file. Administrators choose, under Integrations → Video AI, whether transcripts, summaries and practice questions come from the built-in provider (nothing leaves the system; transcripts are pasted as WebVTT/SRT) or from an OpenAI-compatible service. Automatic transcripts need both that service and conversion (the sound is taken out with FFmpeg).

The browser tests convert a real video, so they need Docker (`E2E_NO_FFMPEG=1` turns that off and skips the streaming tests). Playing converted video in the browser test needs Google Chrome installed, because Playwright's own Chromium has no H.264 decoder; those tests skip without it.

## Recording LiveKit classes

LiveKit's recording service (Egress) records a class room as one MP4. This system starts and stops it and brings the file into the video library. It needs LiveKit with Redis and Egress; `scripts/livekit-dev.ps1` starts all three in Docker for development (`-Stop` removes them). Then tell the API where recordings go:

```
LiveKit__Egress__Enabled=true
LiveKit__Egress__Destination=Local                 # or S3
LiveKit__Egress__LocalDirectory=<folder shared with Egress>   # Local: where this server reads the files
LiveKit__Egress__ContainerPath=/out                # Local: the same folder as Egress sees it
# S3: uses Storage:S3 (bucket, keys, region); set LiveKit__Egress__S3ServiceUrl if Egress reaches the store by another address
```

The host gives recording consent, then presses Start recording in the class's Recording tab once someone is in the room (Stop recording ends it; closing the class stops it too). The class must be linked to a course. The browser test for this runs with `E2E_EGRESS=1` after `scripts/livekit-dev.ps1`, and is skipped otherwise.

### Classes that run themselves

- **Webhook.** Point LiveKit at `POST /api/v1/integrations/livekit/webhook` (LiveKit's `webhook:` setting, with the organization's API key; `scripts/livekit-dev.ps1` does this). Each call is checked against that organization's own LiveKit secret. LiveKit then tells this system who joined and left, so attendance and the class's Live status come from the room itself, and a finished recording is brought in the moment it ends instead of at the next check.
- **Mute others.** The host and staff see a mute button on everyone's picture and "Mute everyone else" in the room (`POST .../sessions/{id}/mute/{userId}` and `.../mute-all`). It switches microphones off; people can switch theirs on again. Raised hands are shown on pictures and in the class's Chat tab, where staff put them down.
- **Each person on their own.** With `LiveKit__Egress__SeparateTracks=true`, everyone in the room while the class is recorded (and anyone who arrives later) is also recorded on their own. Each finished track is saved as a course file; staff list them under the Recording tab (`GET .../recording/tracks`).
- **Start and stop with the class.** Scheduling a LiveKit class linked to a course offers "Record this class automatically": the host's agreement to recording is given there, recording starts when the first person is in the room and stops when the class ends. A background check (every 30 seconds; `LiveKit__Automation__WorkerEnabled=false` turns it off) puts a class live at its start time and closes it after its end plus `LiveKit__Automation__CloseGraceMinutes` (default 15): the recording is stopped, the room closed and anyone still marked present marked as having left.

## Try LiveKit classes locally

Start a development LiveKit server (keys `devkey` / `secret`):

```
docker run --rm -p 7880:7880 -p 7881:7881 -p 7882:7882/udp livekit/livekit-server --dev --bind 0.0.0.0 --node-ip 127.0.0.1
```

In Integrations → Live classes choose LiveKit. The form only accepts `wss://` addresses; for a local `ws://localhost:7880` server start the API with `Integrations__AllowInsecureLiveClassHosts=true` and save the settings through the API (`PUT /api/v1/tenant/integrations/live-classes` with `liveKitUrl`, `liveKitApiKey`, `liveKitApiSecret`). The browser test for LiveKit classes runs when a server is listening on port 7880 and is skipped otherwise.

## Calendar behavior

The frontend calendar selector supports:

- AD — native browser date calendar, with values stored as `YYYY-MM-DD`.
- BS — Nepali Bikram Sambat calendar grid with month navigation.

Both modes emit the equivalent AD value to the API. The calendar preference is stored locally and applies to shared date fields throughout the application.
