import { useEffect, useState, type ReactNode } from "react";
import AppShell from "./components/AppShell";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Select } from "@/components/ui/select";
import CalendarDateField from "./components/CalendarDateField";
import AdvancedLearningPage from "./features/AdvancedLearningPage";
import AiWorkspacePage from "./features/AiWorkspacePage";
import AssessmentsPage from "./features/AssessmentsPage";
import AnnouncementsPage from "./features/AnnouncementsPage";
import AssignmentsPage from "./features/AssignmentsPage";
import CalendarPage from "./features/CalendarPage";
import CategoriesPage from "./features/CategoriesPage";
import CohortsPage from "./features/CohortsPage";
import InvitationsPage from "./features/InvitationsPage";
import IntegrationsPage from "./features/IntegrationsPage";
import MessagesPage from "./features/MessagesPage";
import NotificationsPage from "./features/NotificationsPage";
import MyTasksPage from "./features/MyTasksPage";
import ForumsPage from "./features/ForumsPage";
import GradebookPage from "./features/GradebookPage";
import LearningPathsPage from "./features/LearningPathsPage";
import ResourcesPage from "./features/ResourcesPage";
import VideoLibraryPage from "./features/VideoLibraryPage";
import CoursesPage from "./features/CoursesPage";
import EnrollLearnersPage from "./features/EnrollLearnersPage";
import ApplicationsPage from "./features/ApplicationsPage";
import LandingContentPage from "./features/LandingContentPage";
import DashboardPage from "./features/DashboardPage";
import LearningPage from "./features/LearningPage";
import LiveClassesPage from "./features/LiveClassesPage";
import JoinWithInvitationPage from "./features/JoinWithInvitationPage";
import LandingPage from "./features/LandingPage";
import LoginPage from "./features/LoginPage";
import { getSite, type SiteInfo } from "./lib/publicApi";
import PasswordResetPage from "./features/PasswordResetPage";
import OperationsPage from "./features/OperationsPage";
import RbacPage from "./features/RbacPage";
import ReportsCertificatesPage from "./features/ReportsCertificatesPage";
import { useAuth } from "./lib/auth";
import { useCalendarSettings } from "./lib/calendarSettings";
import { parseInviteHash, parseResetHash, type InviteLink } from "./lib/inviteLink";
import { clearLiveLink, parseLiveLink, type LiveLink } from "./lib/liveLink";
import { menuAliases, menuLabel } from "./lib/navigation";

export default function App() {
  const { session, logout } = useAuth();
  const { mode, setMode } = useCalendarSettings();
  const [academicStart, setAcademicStart] = useState("");
  // An invitation link opens the sign-up page for people without an account, or the Invitations page for people who are signed in.
  const [inviteLink, setInviteLink] = useState<InviteLink | null>(() => parseInviteHash(window.location.hash));
  const [joining, setJoining] = useState(() => inviteLink !== null);
  const [resetLink, setResetLink] = useState<InviteLink | null>(() => parseResetHash(window.location.hash));
  const [resetting, setResetting] = useState(() => resetLink !== null);
  // Visitors who are not signed in start on the landing page; "Log in" opens the sign-in form, with the organization filled in if they typed it.
  // Which kind of website this is: one organization's own (its address says which) or the shared portal. Either way an organization is always known before signing in.
  const [site, setSite] = useState<SiteInfo | null>(null);
  useEffect(() => {
    let current = true;
    getSite(window.location.host).then((found) => current && setSite(found)).catch(() => current && setSite({ mode: "portal", organization: null }));
    return () => { current = false; };
  }, []);
  const [landing, setLanding] = useState(true);
  const [organization, setOrganization] = useState<string | undefined>(undefined);
  // A class link from the live-class provider (?liveSession=<id> or ?recording=<id>) opens that class once the person is signed in.
  const [liveLink, setLiveLink] = useState<LiveLink | null>(() => parseLiveLink(window.location.search));
  const liveMenu = (link: LiveLink) => (link.recording ? "recordings" : "live");
  const [activeMenu, setActiveMenu] = useState(() => (session && liveLink ? liveMenu(liveLink) : session && inviteLink ? "invitations" : "dashboard"));
  useEffect(() => {
    if (liveLink) clearLiveLink();
  }, []);
  useEffect(() => {
    // Signed in after arriving on a class link: go to the class.
    if (session && liveLink && activeMenu === "dashboard") setActiveMenu(liveMenu(liveLink));
  }, [session]);
  // From a course: open one of its classes on the Live classes page.
  const openClass = (sessionId: string) => {
    setLiveLink({ sessionId, recording: false });
    setActiveMenu("live");
  };
  const openMenu = (id: string) => {
    setActiveMenu(menuAliases[id] ?? id); // old ids open their replacement
    setLiveLink(null); // the class link has done its job once the person moves on
  };

  if (!session) {
    if (!site) return <div role="status" className="flex min-h-screen items-center justify-center text-muted-foreground">Loading…</div>;
    const fixed = site.organization ?? undefined;
    if (resetting) return <PasswordResetPage link={resetLink} onBack={() => { setResetting(false); setResetLink(null); }} />;
    if (joining) return <JoinWithInvitationPage link={inviteLink} onBack={() => { setJoining(false); setInviteLink(null); }} />;
    if (landing) return <LandingPage fixed={fixed} onSignIn={(slug) => { setOrganization(slug); setLanding(false); }} onJoin={() => setJoining(true)} />;
    return <LoginPage locked={fixed} initialTenant={organization} onBack={() => setLanding(true)} onJoin={() => setJoining(true)} onForgot={() => setResetting(true)} />;
  }

  return (
    <AppShell
      session={session}
      activeMenu={activeMenu}
      onNavigate={openMenu}
      onLogout={() => { logout(); setLanding(true); setOrganization(undefined); }}
      calendarMode={mode}
      onCalendarModeChange={setMode}
    >
      <div className="mx-auto max-w-6xl" key={activeMenu}>
        {renderActivePage(activeMenu, session, mode, setMode, academicStart, setAcademicStart, openMenu, liveLink, openClass)}
      </div>
    </AppShell>
  );
}

function renderActivePage(
  activeMenu: string,
  session: NonNullable<ReturnType<typeof useAuth>["session"]>,
  mode: "AD" | "BS",
  setMode: (value: "AD" | "BS") => void,
  academicStart: string,
  setAcademicStart: (value: string) => void,
  onNavigate: (id: string) => void,
  liveLink: LiveLink | null = null,
  onOpenClass?: (sessionId: string) => void,
): ReactNode {
  if (activeMenu === "dashboard")
    return <DashboardPage session={session} onNavigate={onNavigate} />;
  if (activeMenu === "calendar") return <CalendarPage onNavigate={onNavigate} />;
  if (activeMenu === "my-tasks") return <MyTasksPage onNavigate={onNavigate} />;
  if (activeMenu === "settings")
    return (
      <SettingsPanel
        mode={mode}
        setMode={setMode}
        academicStart={academicStart}
        setAcademicStart={setAcademicStart}
      />
    );
  if (activeMenu === "course-catalog") return <CoursesPage mode="catalog" onOpenClass={onOpenClass} />;
  if (activeMenu === "admin-courses") return <CoursesPage mode="authoring" onOpenClass={onOpenClass} />;
  if (activeMenu === "learning") return <LearningPage onOpenClass={onOpenClass} />;
  if (activeMenu === "learning-paths") return <LearningPathsPage />;
  if (activeMenu === "resources") return <ResourcesPage />;
  if (activeMenu === "categories") return <CategoriesPage />;
  if (activeMenu === "forums") return <ForumsPage />;
  if (activeMenu === "invitations") return <InvitationsPage />;
  if (activeMenu === "cohorts") return <CohortsPage />;
  if (activeMenu === "messages") return <MessagesPage />;
  if (activeMenu === "announcements") return <AnnouncementsPage />;
  if (
    activeMenu === "recommendations" ||
    activeMenu === "gamification" ||
    activeMenu === "virtual-labs" ||
    activeMenu === "offline-learning"
  )
    return (
      <AdvancedLearningPage
        focus={activeMenu === "offline-learning" ? "offline" : activeMenu}
      />
    );
  if (activeMenu === "assignments") return <AssignmentsPage />;
  if (["quizzes", "exams", "progress"].includes(activeMenu))
    return <AssessmentsPage initialTab="overview" />;
  if (activeMenu === "question-bank") return <AssessmentsPage initialTab="questions" />;
  if (activeMenu === "grades") return <GradebookPage />;
  if (
    [
      "ai-tutor",
      "ai-builder",
      "ai-planner",
      "ai-questions",
      "ai-assistant",
    ].includes(activeMenu)
  )
    return <AiWorkspacePage />;
  if (
    activeMenu === "notifications" ||
    activeMenu === "communication-notifications"
  )
    return <NotificationsPage onNavigate={onNavigate} />;
  if (activeMenu === "integrations") return <IntegrationsPage />;
  if (activeMenu === "certificates") return <ReportsCertificatesPage focus="certificates" />;
  if (
    [
      "reports",
      "learning-analytics",
      "performance",
      "attendance-reports",
    ].includes(activeMenu)
  )
    return <ReportsCertificatesPage focus="reports" />;
  if (activeMenu === "operations") return <OperationsPage />;
  if (activeMenu === "video-library") return <VideoLibraryPage />;
  if (activeMenu === "live") return <LiveClassesPage initialSessionId={liveLink?.sessionId} />;
  if (activeMenu === "enroll-learners") return <EnrollLearnersPage />;
  if (activeMenu === "applications") return <ApplicationsPage />;
  if (activeMenu === "landing-page") return <LandingContentPage />;
  if (activeMenu === "schedule-class") return <LiveClassesPage initialView="schedule" />;
  if (activeMenu === "recordings") return <LiveClassesPage initialTab="recording" initialSessionId={liveLink?.sessionId} />;
  if (activeMenu === "attendance") return <LiveClassesPage initialTab="attendance" />;
  if (activeMenu === "discussions") return <LiveClassesPage initialTab="chat" />;
  if (["users", "instructors", "learners"].includes(activeMenu)) return <RbacPage initialTab="users" />;
  if (activeMenu === "security") return <RbacPage initialTab="roles" />;
  if (activeMenu === "audit-logs") return <RbacPage initialTab="audit" />;
  return <ModulePlaceholder label={menuLabel(activeMenu)} />;
}

function CalendarModeSelect({ mode, setMode, id }: { mode: "AD" | "BS"; setMode: (value: "AD" | "BS") => void; id: string }) {
  return (
    <div className="flex max-w-xs flex-col gap-1.5">
      <Label htmlFor={id}>Calendar</Label>
      <Select id={id} value={mode} onChange={(event) => setMode(event.target.value as "AD" | "BS")}>
        <option value="AD">AD — English calendar</option>
        <option value="BS">BS — Nepali calendar</option>
      </Select>
    </div>
  );
}

function ModulePlaceholder({ label }: { label: string }) {
  return (
    <Card className="col-span-full">
      <CardHeader>
        <div className="flex items-center justify-between gap-3">
          <CardTitle className="text-2xl">{label}</CardTitle>
          <Badge variant="outline">Planned module</Badge>
        </div>
        <CardDescription>
          This module is in the navigation and is scheduled for implementation. Related capabilities are available through the connected workspace pages.
        </CardDescription>
      </CardHeader>
    </Card>
  );
}

function SettingsPanel({
  mode,
  setMode,
  academicStart,
  setAcademicStart,
}: {
  mode: "AD" | "BS";
  setMode: (value: "AD" | "BS") => void;
  academicStart: string;
  setAcademicStart: (value: string) => void;
}) {
  return (
    <Card className="col-span-full">
      <CardHeader>
        <CardTitle className="text-2xl">System settings</CardTitle>
        <CardDescription>
          Calendar mode, date conversion, and academic configuration belong to the platform configuration area rather than the tenant workspace.
        </CardDescription>
      </CardHeader>
      <CardContent className="flex max-w-xs flex-col gap-4">
        <CalendarModeSelect id="settings-calendar-mode" mode={mode} setMode={setMode} />
        <CalendarDateField
          id="academic-start"
          label="Academic year starts"
          value={academicStart}
          onChange={setAcademicStart}
          hint={academicStart ? `Canonical API value: ${academicStart} AD` : "Choose a date from the calendar."}
        />
      </CardContent>
    </Card>
  );
}
