import { useEffect, useState } from "react";
import { Plus } from "lucide-react";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, PageHeader, RowList } from "@/components/form";
import SidePanel from "@/components/SidePanel";
import { ApiError, apiRequest } from "../lib/api";
import { useAuth } from "../lib/auth";
import { availability, fromLocalInput, type Assessment, type AssessmentDetail, type Attempt, type Course } from "../lib/assessments";
import AssessmentAccommodationsPanel from "./AssessmentAccommodationsPanel";
import AssessmentAttemptPanel from "./AssessmentAttemptPanel";
import AssessmentGradingPanel from "./AssessmentGradingPanel";
import AssessmentQuestionsPanel from "./AssessmentQuestionsPanel";
import AssessmentRubricsPanel from "./AssessmentRubricsPanel";

export default function AssessmentsPage({ initialTab = "overview" }: { initialTab?: "overview" | "questions" | "grading" }) {
  const { session } = useAuth();
  const [courses, setCourses] = useState<Course[]>([]);
  const [courseId, setCourseId] = useState("");
  const [assessments, setAssessments] = useState<Assessment[]>([]);
  const [selectedAssessment, setSelectedAssessment] = useState<AssessmentDetail | null>(null);
  const [creating, setCreating] = useState(false);
  const [attempt, setAttempt] = useState<Attempt | null>(null);
  const [title, setTitle] = useState("");
  const [instructions, setInstructions] = useState("");
  const [timeLimitMinutes, setTimeLimitMinutes] = useState("");
  const [attemptLimit, setAttemptLimit] = useState("1");
  const [shuffleQuestions, setShuffleQuestions] = useState(false);
  const [shuffleOptions, setShuffleOptions] = useState(false);
  const [opensAt, setOpensAt] = useState("");
  const [dueAt, setDueAt] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [tab, setTab] = useState<string>(initialTab);
  const [area, setArea] = useState("assessments");
  const canManage = session?.permissions.includes("assessment.manage") ?? false;
  const canAttempt = session?.permissions.includes("assessment.attempt") ?? false;
  const canGrade = session?.permissions.includes("grade.manage") ?? false;

  useEffect(() => {
    void loadCourses();
  }, [session?.accessToken]);
  useEffect(() => {
    if (courseId) void loadAssessments(courseId);
  }, [courseId]);

  async function loadCourses() {
    try {
      const result = await apiRequest<Course[]>("/api/v1/tenant/courses");
      const published = result.filter((course) => course.status === "Published");
      setCourses(published);
      if (!courseId && published.length > 0) setCourseId(published[0].id);
    } catch (exception) {
      setError(readError(exception, "Unable to load courses."));
    }
  }

  async function loadAssessments(nextCourseId: string) {
    try {
      setError(null);
      setAssessments(await apiRequest<Assessment[]>(`/api/v1/tenant/courses/${nextCourseId}/assessments`));
    } catch (exception) {
      setError(readError(exception, "Unable to load assessments."));
    }
  }

  async function openAssessment(assessmentId: string) {
    setBusy(true);
    setError(null);
    try {
      const detail = await apiRequest<AssessmentDetail>(`/api/v1/tenant/assessments/${assessmentId}`);
      setSelectedAssessment(detail);
      setAttempt(null);
    } catch (exception) {
      setError(readError(exception, "Unable to open assessment."));
    } finally {
      setBusy(false);
    }
  }

  /** After the author changes something: show the assessment as it is now, and refresh the list's counts. */
  async function reloadSelected() {
    if (!selectedAssessment) return;
    await openAssessment(selectedAssessment.assessment.id);
    await loadAssessments(courseId);
  }

  function closePanel() {
    setCreating(false);
    setSelectedAssessment(null);
    setAttempt(null);
    setFormError(null);
    setError(null);
    setTab(initialTab);
  }

  function openNew() {
    setSelectedAssessment(null);
    setAttempt(null);
    setNotice(null);
    setError(null);
    setFormError(null);
    setCreating(true);
  }

  function choose(assessmentId: string) {
    setCreating(false);
    setNotice(null);
    setFormError(null);
    void openAssessment(assessmentId);
  }

  async function createAssessment(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!courseId) return setFormError("Choose a course first.");
    if (!title.trim()) return setFormError("Enter a title.");
    if (timeLimitMinutes.trim()) {
      const minutes = Number(timeLimitMinutes);
      if (!Number.isInteger(minutes) || minutes < 1)
        return setFormError("Time limit must be a whole number of minutes, 1 or more.");
    }
    const attemptsAllowed = Number(attemptLimit);
    if (!attemptLimit.trim() || !Number.isInteger(attemptsAllowed) || attemptsAllowed < 1 || attemptsAllowed > 20)
      return setFormError("Attempts allowed must be a whole number from 1 to 20.");
    const opens = fromLocalInput(opensAt);
    const due = fromLocalInput(dueAt);
    if (opens && due && new Date(due) <= new Date(opens)) return setFormError("The deadline must be after the opening time.");
    setBusy(true);
    setError(null);
    setFormError(null);
    try {
      await apiRequest(`/api/v1/tenant/courses/${courseId}/assessments`, {
        method: "POST",
        body: JSON.stringify({
          title,
          instructions,
          timeLimitMinutes: timeLimitMinutes ? Number(timeLimitMinutes) : null,
          attemptLimit: Number(attemptLimit),
          ...(shuffleQuestions ? { shuffleQuestions: true } : {}),
          ...(shuffleOptions ? { shuffleOptions: true } : {}),
          ...(opens ? { opensAtUtc: opens } : {}),
          ...(due ? { dueAtUtc: due } : {}),
        }),
      });
      setTitle("");
      setInstructions("");
      setTimeLimitMinutes("");
      setShuffleQuestions(false);
      setShuffleOptions(false);
      setOpensAt("");
      setDueAt("");
      await loadAssessments(courseId);
      setCreating(false);
      setNotice("Draft assessment created. Open it to add questions.");
    } catch (exception) {
      setError(readError(exception, "Unable to create assessment."));
    } finally {
      setBusy(false);
    }
  }

  async function startAttempt() {
    if (!selectedAssessment) return;
    setBusy(true);
    setError(null);
    try {
      setAttempt(await apiRequest<Attempt>(`/api/v1/tenant/assessments/${selectedAssessment.assessment.id}/attempts`, { method: "POST" }));
    } catch (exception) {
      setError(readError(exception, "Unable to start attempt."));
    } finally {
      setBusy(false);
    }
  }

  const selectedCourse = courses.find((course) => course.id === courseId);
  const effectiveTab = (tab === "grading" && !canGrade) || (tab === "questions" && !canManage) ? "overview" : tab;
  const showTabs = canManage || canGrade;
  const detail = selectedAssessment;

  const minutes = detail ? (detail.effectiveTimeLimitMinutes ?? detail.assessment.timeLimitMinutes) : null;
  const attemptsAllowed = detail ? (detail.effectiveAttemptLimit ?? detail.assessment.attemptLimit) : 0;
  const attemptsLeft = detail?.attemptsUsed != null ? attemptsAllowed - detail.attemptsUsed : null;
  const when = detail ? availability(detail.assessment) : "open";
  const overviewView = !detail ? null : attempt ? (
    <AssessmentAttemptPanel key={attempt.attempt.id} attempt={attempt} canAttempt={canAttempt} onChange={setAttempt} />
  ) : (
    <Card>
      <CardHeader>
        <div className="flex items-start justify-between gap-3">
          <div>
            <p className="text-xs text-muted-foreground">
              {detail.assessment.questionCount} question{detail.assessment.questionCount === 1 ? "" : "s"}
              {minutes ? ` · ${minutes} min` : ""}
              {` · ${attemptsAllowed} attempt${attemptsAllowed === 1 ? "" : "s"}`}
            </p>
            <CardTitle className="text-xl">{detail.assessment.title}</CardTitle>
          </div>
          <Badge>{detail.assessment.status}</Badge>
        </div>
        <CardDescription>{detail.assessment.instructions || "Complete this assessment and submit your answers for grading."}</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        {detail.assessment.opensAtUtc || detail.assessment.dueAtUtc ? (
          <p className="text-sm text-muted-foreground" role="status">
            {when === "not-open" ? `Opens ${new Date(detail.assessment.opensAtUtc!).toLocaleString()}.` : when === "closed" ? `Closed ${new Date(detail.assessment.dueAtUtc!).toLocaleString()}.` : detail.assessment.dueAtUtc ? `Closes ${new Date(detail.assessment.dueAtUtc).toLocaleString()}.` : null}
            {when === "not-open" && detail.assessment.dueAtUtc ? ` Closes ${new Date(detail.assessment.dueAtUtc).toLocaleString()}.` : null}
          </p>
        ) : null}
        {detail.accommodation ? (
          <p className="rounded-md bg-muted px-3 py-2 text-sm">
            Your accommodation: {detail.accommodation.extraTimePercent > 0 ? `${detail.accommodation.extraTimePercent}% extra time` : ""}
            {detail.accommodation.extraTimePercent > 0 && detail.accommodation.extraAttempts > 0 ? " and " : ""}
            {detail.accommodation.extraAttempts > 0 ? `${detail.accommodation.extraAttempts} extra attempt${detail.accommodation.extraAttempts === 1 ? "" : "s"}` : ""}.
          </p>
        ) : null}
        {detail.assessment.status === "Published" && canAttempt ? (
          <div className="flex flex-wrap items-center gap-3">
            <Button disabled={busy || when !== "open" || (attemptsLeft !== null && attemptsLeft <= 0)} onClick={() => void startAttempt()}>
              Start attempt
            </Button>
            {attemptsLeft !== null ? <small className="text-muted-foreground">{attemptsLeft > 0 ? `${attemptsLeft} attempt${attemptsLeft === 1 ? "" : "s"} left` : "You have used every attempt."}</small> : null}
          </div>
        ) : null}
      </CardContent>
    </Card>
  );

  const panelOpen = creating || detail !== null;

  const assessmentsArea = (
    <>
      {assessments.length === 0 ? (
        <EmptyState>No assessments are available for this course.</EmptyState>
      ) : (
        <RowList label="Assessments">
          {assessments.map((assessment) => {
            const open = !creating && detail?.assessment.id === assessment.id;
            return (
              <ListRow key={assessment.id} selected={open} columns="sm:grid-cols-[minmax(0,1fr)_auto] md:grid-cols-[minmax(0,2fr)_110px_minmax(0,1fr)_minmax(0,1fr)_auto]">
                <div className="min-w-0">
                  <strong className="block truncate">{assessment.title}</strong>
                  <small className="text-muted-foreground">{assessment.questionCount} question{assessment.questionCount === 1 ? "" : "s"}{assessment.dueAtUtc ? ` · ${availability(assessment) === "closed" ? "closed" : "due"} ${new Date(assessment.dueAtUtc).toLocaleDateString()}` : ""}</small>
                </div>
                <div>
                  <Badge>{assessment.status}</Badge>
                </div>
                <div className="hidden text-muted-foreground md:block">
                  <small className="block">Time limit</small>
                  {assessment.timeLimitMinutes ? `${assessment.timeLimitMinutes} min` : "No limit"}
                </div>
                <div className="hidden text-muted-foreground md:block">
                  <small className="block">Attempts</small>
                  {assessment.attemptLimit}
                </div>
                <div className="flex justify-end">
                  <Button type="button" size="sm" variant="secondary" aria-label={`View details for ${assessment.title}`} aria-expanded={open} onClick={() => choose(assessment.id)}>
                    View details
                  </Button>
                </div>
              </ListRow>
            );
          })}
        </RowList>
      )}
    </>
  );

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader
        title="Assessments"
        description="Take assessments, author questions and grade submissions."
        actions={
          <>
            <span className="text-sm text-muted-foreground">
              {assessments.length} assessment{assessments.length === 1 ? "" : "s"}
            </span>
            {canManage ? (
              <Button onClick={() => { setArea("assessments"); openNew(); }}>
                <Plus className="mr-1 h-4 w-4" aria-hidden />
                New assessment
              </Button>
            ) : null}
          </>
        }
      />
      {panelOpen ? null : <ErrorBanner message={error} />}
      <NoticeBanner message={notice} />

      <div className="flex max-w-md flex-col gap-1.5">
        <Label htmlFor="assessment-course">Course</Label>
        <Select
          id="assessment-course"
          value={courseId}
          onChange={(event) => {
            setCourseId(event.target.value);
            setSelectedAssessment(null);
            setAttempt(null);
          }}
        >
          <option value="">Choose a published course</option>
          {courses.map((course) => (
            <option value={course.id} key={course.id}>
              {course.code} · {course.title}
            </option>
          ))}
        </Select>
      </div>

      {canManage ? (
        <Tabs value={area} onValueChange={setArea}>
          <TabsList>
            <TabsTrigger value="assessments">Assessments</TabsTrigger>
            <TabsTrigger value="rubrics">Rubrics</TabsTrigger>
            <TabsTrigger value="accommodations">Accommodations</TabsTrigger>
          </TabsList>
          <TabsContent value="assessments">{assessmentsArea}</TabsContent>
          <TabsContent value="rubrics">{courseId ? <AssessmentRubricsPanel courseId={courseId} /> : <EmptyState>Choose a course first.</EmptyState>}</TabsContent>
          <TabsContent value="accommodations">{courseId ? <AssessmentAccommodationsPanel courseId={courseId} /> : <EmptyState>Choose a course first.</EmptyState>}</TabsContent>
        </Tabs>
      ) : (
        assessmentsArea
      )}

      <SidePanel open={panelOpen} label={creating ? "New assessment" : "Assessment details"} onClose={closePanel}>
        <div className="flex min-w-0 flex-col gap-4">
          <ErrorBanner message={error} />
          {creating ? (
            <Card>
              <CardHeader>
                <CardTitle>New assessment</CardTitle>
                <CardDescription>
                  {selectedCourse ? `A draft will be created in ${selectedCourse.title}. Add questions once it exists.` : "Choose a course on the page first."}
                </CardDescription>
              </CardHeader>
              <CardContent>
                <FormLayout onSubmit={createAssessment}>
                  <ErrorBanner message={formError} />
                  <FormSection title="Basics">
                    <Field id="assessment-title" label="Title" required>
                      <Input id="assessment-title" value={title} onChange={(e) => setTitle(e.target.value)} disabled={!selectedCourse} />
                    </Field>
                    <Field id="assessment-instructions" label="Instructions">
                      <Textarea id="assessment-instructions" value={instructions} onChange={(e) => setInstructions(e.target.value)} rows={3} disabled={!selectedCourse} />
                    </Field>
                  </FormSection>
                  <FormSection title="Limits">
                    <div className="grid gap-3 sm:grid-cols-2">
                      <Field id="assessment-minutes" label="Time limit (minutes)" hint="Leave empty for no limit.">
                        <Input id="assessment-minutes" value={timeLimitMinutes} onChange={(e) => setTimeLimitMinutes(e.target.value)} type="number" min="1" placeholder="No limit" disabled={!selectedCourse} />
                      </Field>
                      <Field id="assessment-attempts" label="Attempts allowed" required>
                        <Input id="assessment-attempts" value={attemptLimit} onChange={(e) => setAttemptLimit(e.target.value)} type="number" min="1" max="20" disabled={!selectedCourse} />
                      </Field>
                    </div>
                  </FormSection>
                  <FormSection title="Dates" description="Optional. Learners can start only between these times; an attempt ends at the deadline.">
                    <div className="grid gap-3 sm:grid-cols-2">
                      <Field id="assessment-opens" label="Opens at">
                        <Input id="assessment-opens" type="datetime-local" value={opensAt} onChange={(e) => setOpensAt(e.target.value)} disabled={!selectedCourse} />
                      </Field>
                      <Field id="assessment-due" label="Deadline">
                        <Input id="assessment-due" type="datetime-local" value={dueAt} onChange={(e) => setDueAt(e.target.value)} disabled={!selectedCourse} />
                      </Field>
                    </div>
                  </FormSection>
                  <FormSection title="Order" description="Mixing the order makes it harder to copy from a neighbour.">
                    <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={shuffleQuestions} onChange={(e) => setShuffleQuestions(e.target.checked)} />Shuffle the questions for each learner</label>
                    <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={shuffleOptions} onChange={(e) => setShuffleOptions(e.target.checked)} />Shuffle the options of choice questions</label>
                  </FormSection>
                  <FormActions busy={busy} busyLabel="Creating…" submitLabel="Create draft" disabled={!selectedCourse} onCancel={closePanel} />
                </FormLayout>
              </CardContent>
            </Card>
          ) : !detail ? null : showTabs ? (
            <Tabs value={effectiveTab} onValueChange={setTab}>
              <TabsList>
                <TabsTrigger value="overview">Overview and attempt</TabsTrigger>
                {canManage ? <TabsTrigger value="questions">Questions</TabsTrigger> : null}
                {canGrade ? <TabsTrigger value="grading">Grading</TabsTrigger> : null}
              </TabsList>
              <TabsContent value="overview">{overviewView}</TabsContent>
              {canManage ? <TabsContent value="questions"><AssessmentQuestionsPanel key={detail.assessment.id} detail={detail} onChanged={reloadSelected} /></TabsContent> : null}
              {canGrade ? <TabsContent value="grading"><AssessmentGradingPanel assessmentId={detail.assessment.id} title={detail.assessment.title} /></TabsContent> : null}
            </Tabs>
          ) : (
            overviewView
          )}
        </div>
      </SidePanel>
    </section>
  );
}

function readError(exception: unknown, fallback: string) {
  return exception instanceof ApiError ? exception.message : fallback;
}
