import { useEffect, useMemo, useState } from "react";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import CourseLiveClasses from "./CourseLiveClasses";
import LessonBlocks from "@/components/LessonBlocks";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Progress } from "@/components/ui/progress";
import { Textarea } from "@/components/ui/textarea";
import { cn } from "@/lib/utils";
import { ApiError, apiRequest } from "../lib/api";
import { useAuth } from "../lib/auth";

type Course = {
  id: string;
  code: string;
  title: string;
  description?: string | null;
  status: string;
};
type Enrollment = {
  id: string;
  courseId: string;
  courseCode: string;
  courseTitle: string;
  learnerUserId: string;
  status: string;
  source: string;
  progressPercent: number;
  currentLessonId?: string | null;
  startDateAd?: string | null;
  endDateAd?: string | null;
  lastAccessedAtUtc?: string | null;
};
type PlayerLesson = {
  id: string;
  title: string;
  summary?: string | null;
  contentHtml?: string | null;
  displayOrder: number;
  status: string;
  positionSeconds: number;
  lastViewedAtUtc?: string | null;
  completedAtUtc?: string | null;
};
type PlayerModule = {
  id: string;
  title: string;
  description?: string | null;
  displayOrder: number;
  lessons: PlayerLesson[];
  locked?: boolean;
  lockReason?: string | null;
  unlocksAtUtc?: string | null;
};
type Player = {
  course: {
    id: string;
    code: string;
    title: string;
    description?: string | null;
  };
  enrollment: Enrollment;
  modules: PlayerModule[];
};
type Bookmark = {
  id: string;
  lessonId: string;
  title?: string | null;
  note?: string | null;
  positionSeconds: number;
  createdAtUtc: string;
};
type Note = {
  id: string;
  lessonId: string;
  content: string;
  createdAtUtc: string;
  updatedAtUtc: string;
};

export default function LearningPage({
  onOpenClass,
}: {
  onOpenClass?: (sessionId: string) => void;
}) {
  const { session } = useAuth();
  const [courses, setCourses] = useState<Course[]>([]);
  const [enrollments, setEnrollments] = useState<Enrollment[]>([]);
  const [player, setPlayer] = useState<Player | null>(null);
  const [selectedLessonId, setSelectedLessonId] = useState<string | null>(null);
  const [bookmarks, setBookmarks] = useState<Bookmark[]>([]);
  const [notes, setNotes] = useState<Note[]>([]);
  const [noteContent, setNoteContent] = useState("");
  const noteMax = 20000;
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [mainTab, setMainTab] = useState("courses");
  const [lessonTab, setLessonTab] = useState("lesson");

  useEffect(() => {
    void loadLearning();
  }, [session?.accessToken]);

  const selectedLesson = useMemo(
    () =>
      player?.modules
        .flatMap((module) => module.lessons)
        .find((lesson) => lesson.id === selectedLessonId) ?? null,
    [player, selectedLessonId],
  );
  const enrolledCourseIds = new Set(
    enrollments.map((enrollment) => enrollment.courseId),
  );

  async function loadLearning() {
    try {
      setError(null);
      const [courseResult, enrollmentResult] = await Promise.all([
        apiRequest<Course[]>("/api/v1/tenant/courses"),
        apiRequest<Enrollment[]>("/api/v1/tenant/enrollments"),
      ]);
      setCourses(courseResult);
      setEnrollments(enrollmentResult);
      if (
        player &&
        enrollmentResult.some(
          (enrollment) => enrollment.id === player.enrollment.id,
        )
      )
        await openPlayer(
          player.enrollment.courseId,
          player.enrollment.learnerUserId,
        );
    } catch (exception) {
      setError(readError(exception, "Unable to load learning data."));
    }
  }

  async function enroll(courseId: string) {
    setBusy(true);
    setError(null);
    try {
      await apiRequest(`/api/v1/tenant/courses/${courseId}/enroll`, {
        method: "POST",
      });
      await loadLearning();
      setMainTab("courses");
    } catch (exception) {
      setError(readError(exception, "Unable to enroll in the course."));
    } finally {
      setBusy(false);
    }
  }

  async function openPlayer(courseId: string, learnerUserId?: string) {
    setBusy(true);
    setError(null);
    try {
      const query = learnerUserId
        ? `?learnerUserId=${encodeURIComponent(learnerUserId)}`
        : "";
      const next = await apiRequest<Player>(
        `/api/v1/tenant/courses/${courseId}/learning${query}`,
      );
      setPlayer(next);
      const firstIncomplete = next.modules
        .flatMap((module) => (module.locked ? [] : module.lessons))
        .find((lesson) => lesson.status !== "Completed");
      setSelectedLessonId(
        next.enrollment.currentLessonId ||
          firstIncomplete?.id ||
          next.modules[0]?.lessons[0]?.id ||
          null,
      );
      await loadResources(courseId);
    } catch (exception) {
      setError(readError(exception, "Unable to open the course player."));
    } finally {
      setBusy(false);
    }
  }

  async function loadResources(courseId: string) {
    try {
      const [bookmarkResult, noteResult] = await Promise.all([
        apiRequest<Bookmark[]>(
          `/api/v1/tenant/courses/${courseId}/learning/bookmarks`,
        ),
        apiRequest<Note[]>(`/api/v1/tenant/courses/${courseId}/learning/notes`),
      ]);
      setBookmarks(bookmarkResult);
      setNotes(noteResult);
    } catch {
      setBookmarks([]);
      setNotes([]);
    }
  }

  async function updateProgress(status: "InProgress" | "Completed") {
    if (!player || !selectedLesson) return;
    setBusy(true);
    setError(null);
    try {
      const next = await apiRequest<Player>(
        `/api/v1/tenant/courses/${player.course.id}/learning/lessons/${selectedLesson.id}/progress`,
        {
          method: "POST",
          body: JSON.stringify({
            status,
            positionSeconds: selectedLesson.positionSeconds,
            idempotencyKey: crypto.randomUUID(),
          }),
        },
      );
      setPlayer(next);
      setEnrollments((current) =>
        current.map((item) =>
          item.id === next.enrollment.id ? next.enrollment : item,
        ),
      );
    } catch (exception) {
      setError(readError(exception, "Unable to save progress."));
    } finally {
      setBusy(false);
    }
  }

  async function createBookmark() {
    if (!player || !selectedLesson) return;
    setBusy(true);
    setError(null);
    try {
      await apiRequest(
        `/api/v1/tenant/courses/${player.course.id}/learning/bookmarks`,
        {
          method: "POST",
          body: JSON.stringify({
            lessonId: selectedLesson.id,
            title: selectedLesson.title,
            positionSeconds: selectedLesson.positionSeconds,
          }),
        },
      );
      await loadResources(player.course.id);
    } catch (exception) {
      setError(readError(exception, "Unable to save bookmark."));
    } finally {
      setBusy(false);
    }
  }

  async function createNote(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!player || !selectedLesson) return;
    const content = (noteContent ?? "").trim();
    if (!content) return setError("Note cannot be empty.");
    if (content.length > noteMax)
      return setError(`Note must be ${noteMax} characters or fewer.`);
    setBusy(true);
    setError(null);
    try {
      await apiRequest(
        `/api/v1/tenant/courses/${player.course.id}/learning/notes`,
        {
          method: "POST",
          body: JSON.stringify({ lessonId: selectedLesson.id, content }),
        },
      );
      setNoteContent("");
      await loadResources(player.course.id);
    } catch (exception) {
      setError(readError(exception, "Unable to save note."));
    } finally {
      setBusy(false);
    }
  }

  async function withdraw() {
    if (!player) return;
    if (
      !window.confirm(
        `Withdraw from ${player.course.title}? Your progress is kept if you enroll again.`,
      )
    )
      return;
    setBusy(true);
    setError(null);
    try {
      await apiRequest(
        `/api/v1/tenant/enrollments/${player.enrollment.id}/withdraw`,
        { method: "POST" },
      );
      setPlayer(null);
      setSelectedLessonId(null);
      await loadLearning();
    } catch (exception) {
      setError(readError(exception, "Unable to withdraw from the course."));
    } finally {
      setBusy(false);
    }
  }

  const lessonNotes = selectedLesson
    ? notes.filter((note) => note.lessonId === selectedLesson.id)
    : [];
  const lessonBookmarks = selectedLesson
    ? bookmarks.filter((bookmark) => bookmark.lessonId === selectedLesson.id)
    : [];
  const availableCourses = courses.filter(
    (course) => !enrolledCourseIds.has(course.id),
  );

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <div className="flex items-end justify-between">
        <div>
          <h2 className="text-2xl font-semibold">My learning</h2>
          <p className="text-sm text-muted-foreground">
            Resume your courses, track progress and enroll in new ones.
          </p>
        </div>
        <Badge variant="outline">
          {enrollments.length} enrollment{enrollments.length === 1 ? "" : "s"}
        </Badge>
      </div>
      {error ? (
        <div
          role="alert"
          className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
        >
          {error}
        </div>
      ) : null}

      <Tabs value={mainTab} onValueChange={setMainTab}>
        <TabsList>
          <TabsTrigger value="courses">My courses</TabsTrigger>
          <TabsTrigger value="browse">
            Browse and enroll ({availableCourses.length})
          </TabsTrigger>
        </TabsList>

        <TabsContent value="browse">
          <Card>
            <CardHeader>
              <CardTitle>Available courses</CardTitle>
              <CardDescription>
                Courses you are not enrolled in yet.
              </CardDescription>
            </CardHeader>
            <CardContent className="grid gap-3 sm:grid-cols-2">
              {availableCourses.length === 0 ? (
                <p className="text-sm text-muted-foreground">
                  No other courses are available.
                </p>
              ) : null}
              {availableCourses.map((course) => (
                <div
                  key={course.id}
                  className="flex items-center justify-between gap-2 rounded-md border border-border px-3 py-2 text-sm"
                >
                  <span className="min-w-0">
                    <strong className="block truncate">{course.title}</strong>
                    <small className="text-muted-foreground">
                      {course.code}
                    </small>
                  </span>
                  <Button
                    variant="soft"
                    size="sm"
                    disabled={busy}
                    onClick={() => void enroll(course.id)}
                  >
                    Enroll
                  </Button>
                </div>
              ))}
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="courses">
          <div className="grid gap-4 lg:grid-cols-[300px_minmax(0,1fr)]">
            <Card className="self-start">
              <CardHeader>
                <CardTitle>Enrolled courses</CardTitle>
              </CardHeader>
              <CardContent className="flex flex-col gap-2">
                {enrollments.length === 0 ? (
                  <p className="text-sm text-muted-foreground">
                    You are not enrolled in a course yet.
                  </p>
                ) : (
                  enrollments.map((enrollment) => (
                    <button
                      type="button"
                      key={enrollment.id}
                      onClick={() =>
                        void openPlayer(
                          enrollment.courseId,
                          enrollment.learnerUserId,
                        )
                      }
                      className={cn(
                        "flex flex-col gap-2 rounded-md border border-border px-3 py-2 text-left text-sm hover:bg-muted",
                        player?.enrollment.id === enrollment.id &&
                          "border-l-4 border-l-primary bg-muted",
                      )}
                    >
                      <span className="flex items-center justify-between gap-2">
                        <strong className="truncate">
                          {enrollment.courseTitle}
                        </strong>
                        <Badge>{enrollment.status}</Badge>
                      </span>
                      <Progress value={enrollment.progressPercent} />
                      <small className="text-muted-foreground">
                        {enrollment.progressPercent}% complete
                      </small>
                    </button>
                  ))
                )}
              </CardContent>
            </Card>

            {!player ? (
              <Card className="self-start">
                <CardHeader>
                  <CardTitle>Choose a course to continue learning</CardTitle>
                  <CardDescription>
                    Your lesson position and completion status follow you across
                    sessions.
                  </CardDescription>
                </CardHeader>
              </Card>
            ) : (
              <div className="flex flex-col gap-4">
                <Card>
                  <CardHeader>
                    <div className="flex items-start justify-between gap-3">
                      <div>
                        <p className="text-xs text-muted-foreground">
                          {player.course.code} ·{" "}
                          {player.enrollment.progressPercent}% complete
                        </p>
                        <CardTitle className="text-xl">
                          {player.course.title}
                        </CardTitle>
                      </div>
                      <div className="flex items-center gap-2">
                        <Badge>{player.enrollment.status}</Badge>
                        {player.enrollment.status === "Active" ? (
                          <Button
                            variant="softDestructive"
                            size="sm"
                            disabled={busy}
                            onClick={() => void withdraw()}
                          >
                            Withdraw
                          </Button>
                        ) : null}
                      </div>
                    </div>
                    <CardDescription>
                      {player.course.description ||
                        "Continue where you left off."}
                    </CardDescription>
                  </CardHeader>
                  <CardContent>
                    <Progress value={player.enrollment.progressPercent} />
                  </CardContent>
                </Card>

                <Card>
                  <CardHeader>
                    <CardTitle>Live classes</CardTitle>
                    <CardDescription>
                      The classes of this course. Open one to join it when it is
                      on.
                    </CardDescription>
                  </CardHeader>
                  <CardContent>
                    <CourseLiveClasses
                      key={player.course.id}
                      courseId={player.course.id}
                      onOpen={onOpenClass}
                    />
                  </CardContent>
                </Card>

                <div className="grid gap-4 xl:grid-cols-[220px_minmax(0,1fr)]">
                  <Card className="self-start">
                    <CardHeader>
                      <CardTitle>Lessons</CardTitle>
                    </CardHeader>
                    <CardContent>
                      <nav
                        aria-label="Course lessons"
                        className="flex flex-col gap-3"
                      >
                        {player.modules.map((module) => (
                          <div key={module.id} className="flex flex-col gap-1">
                            <strong className="text-sm">
                              {module.locked ? "🔒 " : ""}
                              {module.title}
                            </strong>
                            {module.locked ? (
                              <small className="text-muted-foreground">
                                {module.lockReason ?? "Not open yet."}
                              </small>
                            ) : null}
                            {module.lessons.map((lesson) => (
                              <button
                                type="button"
                                key={lesson.id}
                                onClick={() => setSelectedLessonId(lesson.id)}
                                disabled={module.locked}
                                aria-current={
                                  lesson.id === selectedLessonId
                                    ? "true"
                                    : undefined
                                }
                                className={cn(
                                  "flex items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm hover:bg-muted disabled:cursor-not-allowed disabled:opacity-50",
                                  lesson.id === selectedLessonId &&
                                    "bg-muted font-medium text-primary",
                                )}
                              >
                                <span aria-hidden className="w-4 text-center">
                                  {lesson.status === "Completed" ? "✓" : "○"}
                                </span>
                                {lesson.title}
                              </button>
                            ))}
                          </div>
                        ))}
                      </nav>
                    </CardContent>
                  </Card>

                  {selectedLesson ? (
                    <Tabs value={lessonTab} onValueChange={setLessonTab}>
                      <TabsList>
                        <TabsTrigger value="lesson">Lesson</TabsTrigger>
                        <TabsTrigger value="notes">
                          Notes ({lessonNotes.length})
                        </TabsTrigger>
                        <TabsTrigger value="bookmarks">
                          Bookmarks ({lessonBookmarks.length})
                        </TabsTrigger>
                      </TabsList>

                      <TabsContent value="lesson">
                        <Card>
                          <CardHeader>
                            <p className="text-xs text-muted-foreground">
                              Lesson {selectedLesson.displayOrder}
                            </p>
                            <CardTitle className="text-xl">
                              {selectedLesson.title}
                            </CardTitle>
                            <CardDescription>
                              {selectedLesson.summary ||
                                "Work through this lesson, then mark it complete when you are ready."}
                            </CardDescription>
                          </CardHeader>
                          <CardContent className="flex flex-col gap-4">
                            {selectedLesson.contentHtml ? (
                              <div className="whitespace-pre-wrap rounded-md border border-border p-4 text-sm">
                                {selectedLesson.contentHtml}
                              </div>
                            ) : null}
                            <LessonBlocks
                              key={selectedLesson.id}
                              courseId={player.course.id}
                              lessonId={selectedLesson.id}
                              emptyText={
                                selectedLesson.contentHtml
                                  ? undefined
                                  : "Lesson content will appear here when the author adds it."
                              }
                            />
                            <div className="flex flex-wrap gap-2">
                              <Button
                                variant="secondary"
                                disabled={busy}
                                onClick={() =>
                                  void updateProgress("InProgress")
                                }
                              >
                                Save position
                              </Button>
                              <Button
                                disabled={
                                  busy || selectedLesson.status === "Completed"
                                }
                                onClick={() => void updateProgress("Completed")}
                              >
                                {selectedLesson.status === "Completed"
                                  ? "Completed"
                                  : "Mark complete"}
                              </Button>
                            </div>
                          </CardContent>
                        </Card>
                      </TabsContent>

                      <TabsContent value="notes">
                        <Card>
                          <CardHeader>
                            <CardTitle>Private notes</CardTitle>
                            <CardDescription>
                              Only you can see notes for this lesson.
                            </CardDescription>
                          </CardHeader>
                          <CardContent className="flex flex-col gap-4">
                            <form
                              className="flex flex-col gap-2"
                              onSubmit={createNote}
                            >
                              <Label htmlFor="lesson-note">New note</Label>
                              <Textarea
                                id="lesson-note"
                                value={noteContent}
                                onChange={(event) =>
                                  setNoteContent(event.target.value)
                                }
                                rows={3}
                                maxLength={noteMax}
                                placeholder="Capture a thought about this lesson…"
                              />
                              <div className="flex items-center justify-between">
                                <small className="text-muted-foreground">
                                  {noteContent.length}/{noteMax} characters
                                </small>
                                <Button
                                  variant="secondary"
                                  type="submit"
                                  disabled={busy || !noteContent.trim()}
                                >
                                  Save note
                                </Button>
                              </div>
                            </form>
                            {lessonNotes.length === 0 ? (
                              <p className="text-sm text-muted-foreground">
                                No notes for this lesson yet.
                              </p>
                            ) : (
                              lessonNotes.map((note) => (
                                <p
                                  key={note.id}
                                  className="rounded-md bg-muted px-3 py-2 text-sm"
                                >
                                  {note.content}
                                </p>
                              ))
                            )}
                          </CardContent>
                        </Card>
                      </TabsContent>

                      <TabsContent value="bookmarks">
                        <Card>
                          <CardHeader>
                            <CardTitle>Bookmarks</CardTitle>
                            <CardDescription>
                              Save your current position so you can return to
                              it.
                            </CardDescription>
                          </CardHeader>
                          <CardContent className="flex flex-col gap-3">
                            <div>
                              <Button
                                variant="outline"
                                disabled={busy}
                                onClick={() => void createBookmark()}
                              >
                                Bookmark this lesson
                              </Button>
                            </div>
                            {lessonBookmarks.length === 0 ? (
                              <p className="text-sm text-muted-foreground">
                                No bookmarks for this lesson yet.
                              </p>
                            ) : (
                              lessonBookmarks.map((bookmark) => (
                                <p
                                  key={bookmark.id}
                                  className="rounded-md bg-muted px-3 py-2 text-sm"
                                >
                                  {bookmark.title || "Saved bookmark"} ·{" "}
                                  {bookmark.positionSeconds}s
                                </p>
                              ))
                            )}
                          </CardContent>
                        </Card>
                      </TabsContent>
                    </Tabs>
                  ) : (
                    <Card className="self-start">
                      <CardHeader>
                        <CardDescription>
                          This course does not have lessons yet.
                        </CardDescription>
                      </CardHeader>
                    </Card>
                  )}
                </div>
              </div>
            )}
          </div>
        </TabsContent>
      </Tabs>
    </section>
  );
}

function readError(exception: unknown, fallback: string) {
  return exception instanceof ApiError ? exception.message : fallback;
}
