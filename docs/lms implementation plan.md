# LMS Implementation Plan

## 1. Architecture decision

Build the LMS as a tenant-aware modular monolith first, following the existing Membership Management System rather than starting with independent microservices.

This gives the LMS:

- One deployable ASP.NET Core API with feature boundaries that can be split later.
- Shared authentication, tenancy, authorization, persistence, migrations, operations, and testing patterns.
- A React web application that works responsively as a PWA before adding a separate native mobile application.
- Clear provider seams for AI, video, notifications, payments, storage, and live-class integrations.

Do not introduce Redis, Elasticsearch, a vector database, custom WebRTC infrastructure, VR, or a separate microservice fleet in the first release. Add them only when measured requirements justify the operational cost.

## 2. Technology baseline

Reuse the technologies and conventions already used by Membership Management:

| Area | LMS choice |
| --- | --- |
| Backend | ASP.NET Core on .NET 10 |
| API style | Minimal APIs grouped by feature |
| Frontend | React 19, TypeScript, Vite, React Router |
| Database | PostgreSQL with Entity Framework Core 10 |
| Local development/tests | EF Core InMemory provider where appropriate; PostgreSQL integration tests for relational behavior |
| Authentication | JWT bearer tokens with tenant claims and refresh/session controls |
| Authorization | Tenant-scoped permission policies and role claims |
| Tenancy | Tenant slug/subdomain resolution, tenant claim validation, global query filters, and explicit tenant/branch-style scope checks |
| Background work | Hosted workers with idempotent processing and retry state |
| Files | Storage-provider abstraction with upload/download sessions; object storage in production |
| Payments | Provider abstraction; reuse invoice/payment/refund/reconciliation patterns |
| Notifications | Template, preference, queue, retry, dead-letter, and provider-adapter pattern |
| Operations | Correlation IDs, rate limiting, liveness/readiness checks, protected metrics, migrations, backup and recovery runbooks |
| Quality | Feature-focused API tests, frontend type/build checks, migration checks, UAT and production-readiness gates |

The reference implementation is in `C:\D\1. Kalash\1. Products\MemberManagement`, especially `src/Mms.Api`, `src/Mms.Web`, and `tests/Mms.Api.Tests`.

### Recommended additions beyond the membership stack

Use the following specialized technologies only where they solve a concrete LMS problem:

| Need | Recommendation | When to add |
| --- | --- | --- |
| Search | PostgreSQL full-text search first; add `pgvector` to PostgreSQL for grounded AI retrieval | Full-text search in the catalog phase; vector retrieval in the AI phase |
| Realtime application events | ASP.NET Core SignalR for chat, notifications, presence, and collaborative UI state | When those workflows are implemented |
| Live audio/video | LiveKit Cloud first, with the option to self-host LiveKit later | Live-class phase |
| AI orchestration | A provider-neutral .NET adapter and durable database-backed AI jobs; use a separate Python worker only for specialized ML/data science | AI phase; Python only after a measured need |
| Files and media | S3-compatible object storage, MinIO locally, managed object storage in production, plus CDN delivery | Content-authoring phase |
| Observability | OpenTelemetry for traces, metrics, and logs, alongside the existing health/metrics endpoints | Foundation phase |
| Mobile | Responsive PWA first; React Native/Expo only if offline or device capabilities exceed the PWA | After real mobile usage is measured |

This is the recommended stack, not a requirement to add every tool immediately. PostgreSQL should remain the system of record. `pgvector` is a good fit because it keeps embeddings beside tenant-filterable course content; it should not become a separate vector platform until scale requires one. LiveKit should handle media transport rather than the LMS API implementing WebRTC itself.

## 3. Target solution structure

```text
LMS/
├── src/
│   ├── Lms.Api/
│   │   ├── Domain/
│   │   │   ├── Tenants/
│   │   │   ├── Identity/
│   │   │   ├── Learning/
│   │   │   ├── Content/
│   │   │   ├── Assessments/
│   │   │   ├── Enrollments/
│   │   │   ├── Progress/
│   │   │   ├── Certificates/
│   │   │   ├── Notifications/
│   │   │   └── Analytics/
│   │   ├── Features/
│   │   │   ├── Identity/
│   │   │   ├── Courses/
│   │   │   ├── Content/
│   │   │   ├── Enrollments/
│   │   │   ├── Assessments/
│   │   │   ├── Progress/
│   │   │   ├── Certificates/
│   │   │   ├── Notifications/
│   │   │   ├── Reports/
│   │   │   └── Ai/
│   │   ├── Infrastructure/
│   │   │   ├── Persistence/
│   │   │   ├── Tenancy/
│   │   │   ├── Security/
│   │   │   ├── Storage/
│   │   │   ├── Integrations/
│   │   │   ├── Observability/
│   │   │   └── Operations/
│   │   └── Program.cs
│   └── Lms.Web/
│       ├── src/components/
│       ├── src/features/
│       ├── src/lib/
│       └── src/App.tsx
├── tests/Lms.Api.Tests/
├── docs/
└── scripts/
```

Keep feature endpoints, request/response records, permission constants, and application services together where practical. Keep cross-cutting concerns in `Infrastructure`, matching the membership-management layout.

## 4. Delivery phases

### Phase 0 — Foundation and product decisions

Status: implemented baseline. The API has tenant-aware PostgreSQL/EF Core configuration, JWT/refresh sessions, tenant claim validation, correlation IDs, health/readiness endpoints, migrations, and global fixed-window rate limiting. A dedicated automated test project, protected metrics, backup/restore automation, and production observability remain release gates.

- Confirm the first market: school, university, corporate training, or a generic tenant platform.
- Define tenant, organization, academic year/term, branch/campus, timezone, locale, and data-retention rules.
- Create the .NET 10 API and React/TypeScript/Vite frontend using the membership-management build conventions.
- Establish PostgreSQL configuration, EF Core migrations, InMemory test setup, and environment-specific settings.
- Add the initial permission catalog and seed roles for platform admin, tenant admin, teacher, learner, parent, content editor, finance operator, and support operator.

Exit criteria: a tenant can be provisioned, an administrator can sign in, permissions are enforced, and tenant data cannot cross boundaries.

### Phase 1 — Identity, people, and organization structure

- Reuse tenant resolution from `X-Tenant-Slug` or subdomain.
- Add learner, teacher, parent/guardian, staff, and organization profiles linked to tenant users.
- Add optional campus/branch and class/cohort scope, using the existing branch-access pattern where useful.
- Add invitations, password reset, MFA readiness, active/inactive status, and audit history.
- Add parent-to-learner relationships with explicit consent and visibility rules.

Exit criteria: each user sees only the people, classes, and learning records allowed by tenant and role scope.

### Phase 2 — Course catalog and authoring

Status: implemented baseline. The current increment includes the tenant-scoped catalog model, draft/review/published/archived workflow, module/lesson authoring, local storage provider, permissions, PostgreSQL migration, and AD/BS availability date fields. The remaining enhancements in this phase are richer content blocks, full chapter/topic/activity editors, categories/tags/prerequisites metadata, object-storage upload sessions, and immutable revision editing for already-published courses.

- Implement `Course → Module → Chapter → Lesson → Topic → Activity` as tenant-scoped entities.
- Support draft, review, published, archived, and versioned content states.
- Add authoring permissions and approval workflow before publication.
- Add content blocks for text, image, PDF, link, video, audio, code, embed, and downloadable resources.
- Use the storage-session abstraction for file upload/download and retain metadata in PostgreSQL.
- Add course categories, tags, prerequisites, difficulty, language, estimated time, and accessibility metadata.

Exit criteria: a teacher can create, review, publish, update, and retire a course without breaking learner progress history.

### Phase 3 — Enrollment, delivery, and progress

Status: implemented baseline. The current increment includes self-service and administrator enrollment, capacity-aware self-enrollment with waitlisting, learner course delivery, resumable lesson progress, immutable progress events, completion projection, bookmarks, private notes, permission policies, and the Phase 3 PostgreSQL migration. Remaining hardening includes invitations with acceptance tokens, waitlist promotion, cohorts, prerequisite/drip rules, richer offline caching, and dedicated tenant-isolation/integration test projects.

- Add self-enrollment, administrator enrollment, invitations, waitlist promotion, cohorts, and enrollment dates.
- Build the learner course player with resume position, completion rules, bookmarks, notes, and discussion links.
- Track lesson/activity attempts and immutable progress events, then maintain a current progress projection for fast dashboards.
- Add deadlines, drip release, prerequisites, and course completion rules.
- Implement responsive web/PWA behavior first, including installability and safe offline caching of metadata.

Exit criteria: a learner can enroll, resume a course, complete activities, and see accurate progress from multiple devices.

### Phase 4 — Assessments and grading

Status: implemented baseline. The current increment includes question banks, typed questions, assessment publishing, attempt limits, learner answers, automatic objective grading, teacher review, manual grade finalization, permission boundaries, and the Phase 4 PostgreSQL migration. Remaining hardening includes rubric criteria, assessment version branching, time-limit enforcement, randomization/question pools, accommodations, and real file-upload answer handling.

- Implement MCQ, multiple response, true/false, fill-in-the-blank, matching, short answer, essay, file upload, and code submission types.
- Separate question bank, assessment version, attempt, answer, rubric, grade, feedback, and review records.
- Support automatic grading and teacher review with explicit grade finalization.
- Add time limits, attempt limits, randomized questions, question pools, accommodations, and manual overrides.
- Keep proctoring as an integration boundary; do not build surveillance infrastructure in the first release.

Exit criteria: a teacher can publish an assessment, a learner can submit an attempt, and grades/audit history remain reproducible.

### Phase 5 — Notifications, certificates, and reporting

Status: implemented baseline. The current increment includes durable in-app notification templates, preferences, queue/retry/dead-letter state, hosted dispatch, enrollment/completion/grading/certificate events, certificate issuance/revocation/public verification, transcript entries, overview reports, CSV exports, and the Phase 5 PostgreSQL migration. Remaining hardening includes external email/SMS providers, richer certificate rendering/QR images, scheduled deadline reminders, configurable report builders, and dedicated worker integration tests.

- Reuse the membership-management notification queue, templates, preferences, retry, dead-letter, and provider-routing patterns.
- Trigger notifications for enrollment, assignment, grading, deadlines, announcements, certificates, and account events.
- Add certificate templates, issuance, revocation, QR verification, digital badge metadata, and transcript exports.
- Add tenant-scoped reports for enrollment, completion, attendance, assessment performance, engagement, and teacher workload.
- Export CSV first; add richer BI integration only after report definitions stabilize.

Exit criteria: operational users can monitor learning activity and learners can verify earned certificates publicly.

### Phase 6 — AI services with human control

Implementation status: the initial human-in-the-loop slice is implemented. It includes a provider-neutral `IAiProvider`, deterministic local provider, durable tenant-scoped jobs, hosted retries/dead-letter handling, published-course grounding, PII redaction, lesson citations, review history, approve/reject/regenerate endpoints, and the React AI workspace. External model adapters, provider budget accounting, evaluation datasets, and production content filters remain hardening work.

- Introduce an `IAiProvider`/provider-router abstraction implemented through typed HTTP clients.
- Store prompts, model/provider, input references, output, safety result, cost metadata, and reviewer status for every durable AI job.
- Start with low-risk features: lesson summarization, quiz draft generation, flash cards, translation drafts, and tutor explanations grounded in published course content.
- Add retrieval only for approved tenant content; enforce tenant/course visibility before building context.
- Keep teacher approval mandatory for generated assessments, lesson materials, grades, recommendations, and parent-facing messages.
- Process long-running generation asynchronously through a durable job table and hosted worker.
- Add budget limits, rate limits, PII redaction, prompt-injection defenses, content filters, evaluation datasets, and an audit trail.

Exit criteria: AI assists a teacher or learner without becoming the system of record for grades, policy, or published content.

### Phase 7 — Live classes and collaboration

Implementation status: implemented baseline. The Phase 7 increment includes the permission-aware menu shell, tenant-scoped class sessions, a local provider adapter seam, join/leave attendance, announcements, chat, polls, hand-raise, custom RBAC roles, user role assignment, permission catalog, security audit events, an authenticated collaboration event stream, recording consent, recording retries, configurable retention expiry through a hosted worker, and explicit session-access checks on live-class operations. A managed video provider remains the production adapter to add.

- Add class schedules, attendance, announcements, chat, polls, hand raise, file sharing, and recordings metadata.
- Integrate a managed video provider through an adapter instead of implementing media transport in the LMS API.
- Persist provider meeting IDs, attendance imports, recording status, consent, retention dates, and failure/retry state.
- Add teacher-side AI assistance only after live-class privacy, consent, retention, and recording policies are approved.

Exit criteria: a class can be scheduled, joined through a provider, attendance can be reconciled, collaboration refreshes through an authenticated stream, and recordings require host consent and follow retention rules.

### Phase 8 — Release hardening and core completion

Implementation status: implemented baseline. This increment adds multi-role tenant RBAC with aggregated JWT permissions, guarded role revocation, protected request metrics, assessment time-limit enforcement with late-submission tracking, capacity-aware self-enrollment with waitlist responses, course capacity authoring, two Phase 8 PostgreSQL migrations, and verified API/frontend builds. Dedicated automated tenant-isolation tests, invitation/cohort workflows, waitlist promotion, rich content blocks, and production telemetry export remain release gates.

- Add isolated API integration tests for login, refresh, tenant claim validation, permission boundaries, multi-role permission aggregation, and tenant isolation.
- Add PostgreSQL coverage for migrations, unique membership constraints, time-limit persistence, query filters, and concurrency-sensitive enrollment workflows.
- Add invitation acceptance tokens, waitlist promotion, cohorts, prerequisite/drip rules, and enrollment notifications.
- Add rubric criteria, assessment versioning, question pools, accommodations, and durable content-block authoring backed by object storage.
- Export protected request metrics to the production observability stack and add backup/restore and recovery evidence.

Exit criteria: the core API has repeatable integration coverage, multi-role access is auditable, release metrics are protected, timed assessments are enforceable, and the remaining enrollment/content hardening work is explicitly tracked.

### Phase 9 — Advanced learning features

Implementation status: implemented baseline. The current increment adds tenant-scoped recommendation scoring from published course subjects, points/streaks/badges/leaderboard gamification, a virtual-lab provider catalog and launch integration seam, role permissions, the Phase 9 PostgreSQL migration, and the learner-facing advanced-learning navigation. Offline downloads, native mobile clients, and VR/AR integrations remain intentionally deferred.

- Adaptive recommendations based on progress and assessment evidence.
- Gamification with tenant-configurable points, badges, streaks, and leaderboards.
- Virtual labs through external simulation providers or isolated sandbox services.
- Harden recommendations with assessment evidence, explainability, dismissals, and experimentation metrics.
- Add offline video/book downloads with licensing, expiry, encryption, and device limits.
- Use a PWA first; add native mobile clients only if offline, push, or device requirements justify them.
- Add VR/AR modules only as independently deployable integrations with a defined device and content strategy.

Exit criteria: learners receive explainable recommendations, learning activity produces auditable achievement signals, and institutions can register approved simulation providers without coupling media transport to the LMS API.

### Phase 10 — Advanced learning hardening and device integrations

Implementation status: implemented baseline. This increment adds recommendation evidence from graded assessments, deterministic experiment variants, learner dismissals, explainability text, tenant-configurable gamification settings with a daily cap, administrative badge controls, signed ten-minute virtual-lab launch tokens, provider health checks, idempotent result callbacks, a PWA shell, and expiring enrolled-course offline manifests with asset caching. Full encrypted offline media, multi-device conflict reconciliation, provider webhooks, and native/VR clients remain release follow-ups.

- Recommendation evidence, dismissals, explainability, and controlled variants are implemented.
- PWA shell registration, offline course manifests, 24-hour expiry, learner scoping, and approved asset caching are implemented; encrypted media/device registration/conflict sync are deferred.
- Virtual-lab launch security, provider health checks, signed launch tokens, and idempotent result callbacks are implemented.
- Tenant-configurable points, daily caps, and administrative badge management are implemented; broader event-rate anti-abuse policies remain a follow-up.
- Native mobile and VR/AR integrations remain telemetry-gated and are not started prematurely.

Exit criteria: advanced learning features are measurable, secure across devices and providers, and governed by tenant policy rather than hard-coded defaults.

### Phase 11 — Offline trust, provider operations, and device pilots

Implementation status: implemented baseline. This increment adds device-bound offline credentials with a three-device limit, 24-hour package licenses, revocation, AES-256-GCM encrypted package and asset envelopes, conflict-aware progress synchronization, signed virtual-lab webhooks, retry/dead-letter state, provider health history, result audit views, gamification hourly award limits with abuse reviews, and telemetry summaries for PWA adoption and provider reliability.

- Device registration, license expiry, revocation, encrypted package/asset envelopes, and conflict-aware sync are implemented.
- Provider HMAC webhooks, idempotent result processing, retry/dead-letter states, health history, and audit endpoints are implemented.
- Gamification hourly award limits, configurable tenant policy, and administrator review/resolve endpoints are implemented.
- Telemetry ingestion and a 30-day PWA/provider summary are implemented; native mobile and VR/AR decisions remain telemetry-gated.

Exit criteria: offline content is protected and reconcilable, external providers are observable and replay-safe, and mobile/immersive investments are supported by measured usage.

### Phase 12 — Production-scale trust and device pilots

Implementation status: implemented baseline. This increment adds a managed-secret abstraction with environment/reference resolution, durable Data Protection key-ring configuration and production startup validation, a reusable AES-256-GCM offline cipher, automated trust-boundary tests, operations/readiness dashboards, configurable alert thresholds, a daily retention worker, a safe PostgreSQL backup/restore rehearsal script and runbook, and telemetry-gated device-pilot criteria. Provider integrations can now reference secrets without persisting plaintext credentials; legacy protected values remain available for migration.

- Configure `SecretStore:Provider`, `SecretStore:Prefix`, and provider secret references through the deployment secret manager; rotate referenced secrets using the organization’s secret-manager procedure.
- Mount `DataProtection:KeyStoragePath` on durable shared storage in production and use a stable `DataProtection:ApplicationName` across API instances.
- Run `dotnet test tests\\Lms.Api.Tests\\Lms.Api.Tests.csproj` and the Phase 12 release-gate smoke script before deployment.
- Review `/api/v1/tenant/operations/summary` for offline devices, licenses, conflicts, dead-letter webhooks, provider health, telemetry, and pilot blockers.
- Run the backup/restore rehearsal and retain checksum, migration, row-count, tenant-isolation, and sign-off evidence.

Exit criteria: Phase 11 workflows have repeatable automated coverage, production-grade secret/key operations, measurable retention and recovery behavior, and a documented evidence-based device-pilot decision.

### Phase 13 — Production integrations and continuous assurance

Next implementation plan:

- Replace the environment-backed secret adapter with a selected managed provider connector and automated rotation/version rollout.
- Add PostgreSQL Testcontainers or an equivalent CI service for migration, constraint, tenant-isolation, and concurrency coverage.
- Export structured metrics and traces to OpenTelemetry/Prometheus-compatible monitoring with SLO dashboards and on-call routing.
- Automate scheduled backup restore rehearsals and evidence retention, including tenant-level recovery verification.
- Execute the approved PWA/native/VR pilot only after Phase 12 gates pass, then measure adoption, learning outcomes, support load, and rollback readiness.

## 5. Core data model

Every tenant-owned record should have `TenantId`, timestamps, and audit metadata where appropriate. Recommended first entities:

- `Tenant`, `AppUser`, `Role`, `Permission`, `UserRole`, `AuditLog`
- `LearnerProfile`, `TeacherProfile`, `GuardianRelationship`, `Campus`, `Class`, `Cohort`, `AcademicTerm`
- `Course`, `CourseVersion`, `Module`, `Chapter`, `Lesson`, `Topic`, `Activity`, `ContentAsset`, `CourseCategory`, `CourseTag`
- `Enrollment`, `EnrollmentAccess`, `ProgressEvent`, `ActivityProgress`, `Bookmark`, `LearnerNote`
- `QuestionBank`, `Question`, `Assessment`, `AssessmentVersion`, `AssessmentAttempt`, `Answer`, `Rubric`, `Grade`, `Feedback`
- `Assignment`, `Submission`, `AttendanceRecord`, `ClassSession`, `Announcement`
- `CertificateTemplate`, `Certificate`, `Badge`, `TranscriptEntry`
- `AiJob`, `AiConversation`, `AiCitation`, `AiReview`, `Recommendation`
- `NotificationTemplate`, `NotificationPreference`, `NotificationMessage`, `NotificationDelivery`

Use immutable/versioned records for published course content, assessment versions, attempts, grades, certificates, and AI outputs. Use current-state projections for dashboards and reporting performance.

## 6. API and frontend conventions

- Use `/api/v1/platform` only for platform-level operations and `/api/v1/tenant` for tenant operations.
- Require `X-Tenant-Slug` or a tenant subdomain for tenant requests and validate the tenant claim against the resolved tenant.
- Define read/manage/approve/review/export permissions per feature, following the membership-management policy naming style.
- Return validation problems and conflict responses consistently; use idempotency keys for enrollment, submissions, payments, certificate issuance, and AI jobs.
- Keep frontend pages feature-oriented: `Dashboard`, `Courses`, `CourseAuthoring`, `Assessments`, `Assignments`, `LiveClasses`, `AiTutor`, `Reports`, `Certificates`, and `Admin`.
- Use the existing tenant-aware API client/session pattern, protected routes, and permission-based navigation.

## 7. Operational and security requirements

- PostgreSQL migrations are the source of truth; never use automatic schema creation in production.
- Apply tenant query filters, but also keep explicit authorization and scope checks on sensitive endpoints.
- Encrypt secrets and provider keys outside source control; use durable shared Data Protection keys across API instances.
- Add correlation IDs, structured logging, rate limiting, health/live, health/ready, protected metrics, and release identity endpoints from the reference system.
- Make all workers idempotent, lease-based, retryable, observable, and safe to resume after process termination.
- Define retention and deletion policies for learner records, recordings, uploaded work, AI prompts/outputs, and audit logs before production.
- Treat children’s data, biometric/proctoring data, location data, and behavioral analytics as restricted data requiring explicit product and legal approval.

## 8. Quality gates

For each phase:

1. Add domain and endpoint tests using an isolated InMemory test factory.
2. Add PostgreSQL integration coverage for migrations, constraints, indexes, query filters, and concurrency-sensitive workflows.
3. Add tenant-isolation, wrong-tenant-token, permission, idempotency, and audit-history tests.
4. Add frontend TypeScript and Vite build checks.
5. Add UAT scenarios and evidence for the complete user workflow, not only individual endpoints.
6. Run readiness, security, integration, backup/restore, and recovery checks before release.

Initial release acceptance should cover:

- Tenant provisioning and secure login.
- Learner/teacher/guardian access boundaries.
- Course authoring and publication.
- Enrollment and progress across sessions.
- Assessment submission, grading, and feedback.
- Notifications, certificates, and core reports.
- Backup/restore and tenant-isolation verification.

## 9. Recommended MVP boundary

Include Phases 0–5 and a small, teacher-approved subset of Phase 6:

- Multi-tenant identity and role management.
- Course catalog and authoring.
- Learner enrollment and progress.
- Assignments and basic assessments.
- Notifications, certificates, and reports.
- AI summaries, quiz drafts, and grounded tutor explanations with audit and approval.

Defer live video, AI proctoring, automated grading of high-stakes work, virtual labs, marketplace, VR, native mobile apps, and advanced recommendations until the MVP has real usage data and operational baselines.
