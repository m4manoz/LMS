export type MenuItem = {
  id: string;
  label: string;
  icon: string;
  permissions?: string[];
  roles?: string[];
};
export type MenuSubmenu = { label: string; items: MenuItem[] };
export type MenuGroup = { label: string; submenus: MenuSubmenu[] };

/**
 * Ordered from what everyone uses every day (workspace, learning, classroom, assessment) to what staff do
 * (teaching, AI, communication) to what administrators do (analytics, administration).
 * Each destination appears once; a group or submenu with nothing the person may open is hidden.
 */
export const menuGroups: MenuGroup[] = [
  {
    label: "Workspace",
    submenus: [
      {
        label: "Overview",
        items: [
          { id: "dashboard", label: "Dashboard", icon: "⌂" },
          { id: "calendar", label: "Calendar", icon: "📅" },
          { id: "my-tasks", label: "My tasks", icon: "📌" },
          { id: "notifications", label: "Notifications", icon: "🔔", permissions: ["notification.read"] },
        ],
      },
    ],
  },
  {
    label: "Learning",
    submenus: [
      {
        label: "Courses",
        items: [
          { id: "learning", label: "My learning", icon: "🎓", permissions: ["enrollment.read"] },
          { id: "course-catalog", label: "Course catalog", icon: "📚", permissions: ["course.read"] },
          { id: "learning-paths", label: "Learning paths", icon: "🧭", permissions: ["enrollment.read"] },
          { id: "invitations", label: "Invitations", icon: "✉", permissions: ["enrollment.read"] },
          { id: "certificates", label: "Certificates", icon: "📜", permissions: ["certificate.read"] },
        ],
      },
      {
        label: "Study tools",
        items: [
          { id: "video-library", label: "Video library", icon: "🎬", permissions: ["course.read"] },
          { id: "resources", label: "Learning resources", icon: "📁", permissions: ["course.read"] },
          { id: "offline-learning", label: "Offline learning", icon: "📲", permissions: ["enrollment.read"] },
          { id: "virtual-labs", label: "Virtual labs", icon: "🧪", permissions: ["virtuallab.read"] },
          { id: "recommendations", label: "Recommendations", icon: "✨", permissions: ["recommendation.read"] },
          { id: "gamification", label: "Achievements", icon: "🏆", permissions: ["gamification.read"] },
        ],
      },
    ],
  },
  {
    label: "Classroom",
    submenus: [
      {
        label: "Live learning",
        items: [
          { id: "live", label: "Live classes", icon: "🎥", permissions: ["liveclass.read"] },
          { id: "recordings", label: "Recordings", icon: "📹", permissions: ["liveclass.read"] },
          { id: "attendance", label: "Attendance", icon: "📝", permissions: ["attendance.read"] },
          { id: "discussions", label: "Class chat", icon: "💬", permissions: ["collaboration.read"] },
        ],
      },
    ],
  },
  {
    label: "Assessment",
    submenus: [
      {
        label: "Take and review",
        items: [
          { id: "quizzes", label: "Quizzes", icon: "📝", permissions: ["assessment.read"] },
          { id: "assignments", label: "Assignments", icon: "📄", permissions: ["assessment.read"] },
          { id: "exams", label: "Exams", icon: "🎯", permissions: ["assessment.read"] },
          { id: "grades", label: "Gradebook", icon: "📊", permissions: ["grade.read"] },
          { id: "progress", label: "Progress", icon: "📈", permissions: ["progress.read"] },
        ],
      },
    ],
  },
  {
    label: "Teaching",
    submenus: [
      {
        label: "Course design",
        items: [
          { id: "admin-courses", label: "Course authoring", icon: "📚", permissions: ["course.manage"] },
          { id: "categories", label: "Course categories", icon: "🏷", permissions: ["course.manage"] },
          { id: "question-bank", label: "Assessment authoring", icon: "🗂", permissions: ["assessment.manage"] },
        ],
      },
      {
        label: "Classes and learners",
        items: [
          { id: "enroll-learners", label: "Enroll learners", icon: "➕", permissions: ["enrollment.manage"] },
          { id: "applications", label: "Applications", icon: "📝", permissions: ["enrollment.manage"] },
          { id: "schedule-class", label: "Schedule class", icon: "🗓", permissions: ["liveclass.manage"] },
          { id: "cohorts", label: "Cohorts", icon: "👥", permissions: ["enrollment.manage"] },
          { id: "learners", label: "Learners", icon: "🎓", permissions: ["user.read", "enrollment.manage"] },
          { id: "instructors", label: "Instructors", icon: "🧑‍🏫", permissions: ["user.read", "enrollment.manage"] },
        ],
      },
    ],
  },
  {
    label: "AI Workspace",
    submenus: [
      {
        label: "For learners",
        items: [
          { id: "ai-tutor", label: "AI tutor", icon: "🤖", permissions: ["ai.use"] },
          { id: "ai-assistant", label: "AI assistant", icon: "💬", permissions: ["ai.use"] },
        ],
      },
      {
        label: "For course teams",
        items: [
          { id: "ai-builder", label: "AI course builder", icon: "✨", permissions: ["ai.manage"] },
          { id: "ai-planner", label: "AI lesson planner", icon: "🧠", permissions: ["ai.manage"] },
          { id: "ai-questions", label: "AI question generator", icon: "📑", permissions: ["ai.manage"] },
        ],
      },
    ],
  },
  {
    label: "Communication",
    submenus: [
      {
        label: "Talk",
        items: [
          { id: "messages", label: "Messages", icon: "✉", permissions: ["collaboration.read"] },
          { id: "forums", label: "Forums", icon: "👥", permissions: ["collaboration.read"] },
          { id: "announcements", label: "Announcements", icon: "📢", permissions: ["notification.read"] },
        ],
      },
    ],
  },
  {
    label: "Analytics",
    submenus: [
      {
        label: "Insights",
        items: [
          { id: "reports", label: "Reports", icon: "📊", permissions: ["report.read"] },
          { id: "learning-analytics", label: "Learning analytics", icon: "📈", permissions: ["report.read"] },
          { id: "performance", label: "Performance dashboard", icon: "📉", permissions: ["progress.read"] },
          { id: "attendance-reports", label: "Attendance reports", icon: "📋", permissions: ["attendance.read"] },
        ],
      },
    ],
  },
  {
    label: "Administration",
    submenus: [
      {
        label: "People and access",
        items: [
          { id: "users", label: "Users", icon: "👥", permissions: ["user.read"] },
          { id: "security", label: "Roles and permissions", icon: "🔑", permissions: ["role.read"] },
          { id: "landing-page", label: "Landing page", icon: "🌐", permissions: ["tenant.manage"] },
        ],
      },
      {
        label: "Organization",
        items: [
          { id: "organizations", label: "Organizations", icon: "🏫", permissions: ["tenant.manage"] },
          { id: "settings", label: "System settings", icon: "⚙", permissions: ["tenant.manage"] },
          { id: "integrations", label: "Integrations", icon: "🔌", permissions: ["tenant.manage"] },
        ],
      },
      {
        label: "Monitoring",
        items: [
          { id: "operations", label: "Operations", icon: "🛠", permissions: ["report.read"] },
          { id: "audit-logs", label: "Audit logs", icon: "🔎", permissions: ["security.read"] },
        ],
      },
    ],
  },
];

/** Older links and bookmarks still work: these ids open the same page as their replacement. */
export const menuAliases: Record<string, string> = {
  "communication-notifications": "notifications",
};

export function canViewMenuItem(item: MenuItem, role: string, permissions: string[]) {
  if (item.roles?.includes(role)) return true
  return !item.permissions || item.permissions.some((permission) => permissions.includes(permission))
}

export function menuLabel(id: string) {
  const target = menuAliases[id] ?? id
  return menuGroups.flatMap((group) => group.submenus.flatMap((submenu) => submenu.items)).find((item) => item.id === target)?.label ?? 'This module'
}
