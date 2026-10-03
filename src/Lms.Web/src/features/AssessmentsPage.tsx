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

type Course = { id: string; code: string; title: string; status: string };
type Assessment = {
  id: string;
  courseId: string;
  title: string;
  instructions?: string | null;
  status: string;
  timeLimitMinutes?: number | null;
  attemptLimit: number;
  questionCount: number;
};
type Question = {
  id: string;
  type: string;
  prompt: string;
  options: string[];
  correctAnswers: string[];
  displayOrder: number;
  points: number;
};
type AssessmentDetail = { assessment: Assessment; questions: Question[] };
type AttemptQuestion = {
  id: string;
  type: string;
  prompt: string;
  options: string[];
  correctAnswers: string[];
  displayOrder: number;
  points: number;
  answers: string[];
  scorePoints: number;
  isCorrect?: boolean | null;
  feedback?: string | null;
  text?: string | null;
};
type Attempt = {
  attempt: {
    id: string;
    learnerUserId: string;
    attemptNumber: number;
    status: string;
    scorePoints: number;
    possiblePoints: number;
    percentage?: number | null;
  };
  assessmentTitle: string;
  instructions?: string | null;
  questions: AttemptQuestion[];
  teacherFeedback?: string | null;
};
type AttemptSummary = {
  id: string;
  learnerUserId: string;
  attemptNumber: number;
  status: string;
  scorePoints: number;
  possiblePoints: number;
  percentage?: number | null;
};

const choiceTypes = ["MultipleChoice", "MultipleResponse", "TrueFalse"];

export default function AssessmentsPage({ initialTab = "overview" }: { initialTab?: "overview" | "questions" | "grading" }) {
  const { session } = useAuth();
  const [courses, setCourses] = useState<Course[]>([]);
  const [courseId, setCourseId] = useState("");
  const [assessments, setAssessments] = useState<Assessment[]>([]);
  const [selectedAssessment, setSelectedAssessment] =
    useState<AssessmentDetail | null>(null);
  const [creating, setCreating] = useState(false);
  const [attempt, setAttempt] = useState<Attempt | null>(null);
  const [attempts, setAttempts] = useState<AttemptSummary[]>([]);
  const [answers, setAnswers] = useState<Record<string, string>>({});
  const [title, setTitle] = useState("");
  const [instructions, setInstructions] = useState("");
  const [timeLimitMinutes, setTimeLimitMinutes] = useState("");
  const [attemptLimit, setAttemptLimit] = useState("1");
  const [questionType, setQuestionType] = useState("MultipleChoice");
  const [prompt, setPrompt] = useState("");
  const [options, setOptions] = useState("");
  const [correctAnswers, setCorrectAnswers] = useState("");
  const [points, setPoints] = useState("1");
  const [gradeScore, setGradeScore] = useState("");
  const [gradeFeedback, setGradeFeedback] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [tab, setTab] = useState<string>(initialTab);
  const canManage = session?.permissions.includes("assessment.manage") ?? false;
  const canAttempt =
    session?.permissions.includes("assessment.attempt") ?? false;
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
      const published = result.filter(
        (course) => course.status === "Published",
      );
      setCourses(published);
      if (!courseId && published.length > 0) setCourseId(published[0].id);
    } catch (exception) {
      setError(readError(exception, "Unable to load courses."));
    }
  }

  async function loadAssessments(nextCourseId: string) {
    try {
      setError(null);
      setAssessments(
        await apiRequest<Assessment[]>(
          `/api/v1/tenant/courses/${nextCourseId}/assessments`,
        ),
      );
    } catch (exception) {
      setError(readError(exception, "Unable to load assessments."));
    }
  }

  async function openAssessment(assessmentId: string) {
    setBusy(true);
    setError(null);
    try {
      const detail = await apiRequest<AssessmentDetail>(
        `/api/v1/tenant/assessments/${assessmentId}`,
      );
      setSelectedAssessment(detail);
      setAttempt(null);
      if (canGrade)
        setAttempts(
          await apiRequest<AttemptSummary[]>(
            `/api/v1/tenant/assessments/${assessmentId}/attempts`,
          ),
        );
    } catch (exception) {
      setError(readError(exception, "Unable to open assessment."));
    } finally {
      setBusy(false);
    }
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
        }),
      });
      setTitle("");
      setInstructions("");
      setTimeLimitMinutes("");
      await loadAssessments(courseId);
      setCreating(false);
      setNotice("Draft assessment created. Open it to add questions.");
    } catch (exception) {
      setError(readError(exception, "Unable to create assessment."));
    } finally {
      setBusy(false);
    }
  }

  async function addQuestion(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!selectedAssessment) return;
    // Client-side validation
    const trimmedPrompt = (prompt ?? "").trim();
    const parsedOptions = splitCsv(options);
    const parsedCorrect = splitCsv(correctAnswers);
    const pts = Number(points);
    if (!trimmedPrompt) return setFormError("Question prompt is required.");
    if (choiceTypes.includes(questionType) && parsedOptions.length < 2)
      return setFormError(
        "At least two options are required for choice questions.",
      );
    if (choiceTypes.includes(questionType) && parsedCorrect.length === 0)
      return setFormError(
        "Please indicate the correct answer(s) for choice questions.",
      );
    if (!Number.isFinite(pts) || pts <= 0)
      return setFormError("Points must be a positive number.");
    setBusy(true);
    setError(null);
    setFormError(null);
    try {
      await apiRequest(
        `/api/v1/tenant/assessments/${selectedAssessment.assessment.id}/questions`,
        {
          method: "POST",
          body: JSON.stringify({
            type: questionType,
            prompt: trimmedPrompt,
            options: parsedOptions,
            correctAnswers: parsedCorrect,
            points: pts,
          }),
        },
      );
      setPrompt("");
      setOptions("");
      setCorrectAnswers("");
      setPoints("1");
      await openAssessment(selectedAssessment.assessment.id);
      await loadAssessments(courseId);
    } catch (exception) {
      setError(readError(exception, "Unable to add question."));
    } finally {
      setBusy(false);
    }
  }

  async function publishAssessment() {
    if (!selectedAssessment) return;
    setBusy(true);
    setError(null);
    try {
      await apiRequest(
        `/api/v1/tenant/assessments/${selectedAssessment.assessment.id}/publish`,
        { method: "POST" },
      );
      await openAssessment(selectedAssessment.assessment.id);
      await loadAssessments(courseId);
    } catch (exception) {
      setError(readError(exception, "Unable to publish assessment."));
    } finally {
      setBusy(false);
    }
  }

  async function startAttempt() {
    if (!selectedAssessment) return;
    setBusy(true);
    setError(null);
    try {
      const next = await apiRequest<Attempt>(
        `/api/v1/tenant/assessments/${selectedAssessment.assessment.id}/attempts`,
        { method: "POST" },
      );
      setAttempt(next);
      setAnswers(
        Object.fromEntries(
          next.questions.map((question) => [
            question.id,
            question.type === "Essay"
              ? question.text || ""
              : question.answers.join(", "),
          ]),
        ),
      );
    } catch (exception) {
      setError(readError(exception, "Unable to start attempt."));
    } finally {
      setBusy(false);
    }
  }

  async function submitAttempt() {
    if (!attempt) return;
    setBusy(true);
    setError(null);
    try {
      for (const question of attempt.questions) {
        const value = answers[question.id] || "";
        await apiRequest(
          `/api/v1/tenant/assessment-attempts/${attempt.attempt.id}/answers/${question.id}`,
          {
            method: "PUT",
            body: JSON.stringify({
              answers:
                question.type === "Essay" || question.type === "ShortAnswer"
                  ? []
                  : splitCsv(value),
              text:
                question.type === "Essay" || question.type === "ShortAnswer"
                  ? value
                  : null,
            }),
          },
        );
      }
      const result = await apiRequest<Attempt>(
        `/api/v1/tenant/assessment-attempts/${attempt.attempt.id}/submit`,
        { method: "POST" },
      );
      setAttempt(result);
    } catch (exception) {
      setError(readError(exception, "Unable to submit attempt."));
    } finally {
      setBusy(false);
    }
  }

  async function gradeAttempt(item: AttemptSummary) {
    const score = Number(gradeScore);
    if (!gradeScore.trim() || !Number.isFinite(score) || score < 0 || score > item.possiblePoints)
      return setError(`Score must be a number from 0 to ${item.possiblePoints}.`);
    setBusy(true);
    setError(null);
    try {
      await apiRequest(
        `/api/v1/tenant/assessment-attempts/${item.id}/grade`,
        {
          method: "POST",
          body: JSON.stringify({
            scorePoints: Number(gradeScore),
            feedback: gradeFeedback,
          }),
        },
      );
      if (selectedAssessment)
        setAttempts(
          await apiRequest<AttemptSummary[]>(
            `/api/v1/tenant/assessments/${selectedAssessment.assessment.id}/attempts`,
          ),
        );
      setGradeScore("");
      setGradeFeedback("");
    } catch (exception) {
      setError(readError(exception, "Unable to grade attempt."));
    } finally {
      setBusy(false);
    }
  }

  const selectedCourse = courses.find((course) => course.id === courseId);
  const effectiveTab =
    (tab === "grading" && !canGrade) || (tab === "questions" && !canManage) ? "overview" : tab;
  const showTabs = canManage || canGrade;
  const detail = selectedAssessment;
  const isChoice = choiceTypes.includes(questionType);

  const overviewView = !detail ? null : attempt ? (
    <AttemptPanel attempt={attempt} answers={answers} setAnswers={setAnswers} busy={busy} canAttempt={canAttempt} onSubmit={() => void submitAttempt()} />
  ) : (
    <Card>
      <CardHeader>
        <div className="flex items-start justify-between gap-3">
          <div>
            <p className="text-xs text-muted-foreground">
              {detail.questions.length} questions
              {detail.assessment.timeLimitMinutes ? ` · ${detail.assessment.timeLimitMinutes} min` : ""}
              {` · ${detail.assessment.attemptLimit} attempt${detail.assessment.attemptLimit === 1 ? "" : "s"}`}
            </p>
            <CardTitle className="text-xl">{detail.assessment.title}</CardTitle>
          </div>
          <Badge>{detail.assessment.status}</Badge>
        </div>
        <CardDescription>
          {detail.assessment.instructions || "Complete this assessment and submit your answers for grading."}
        </CardDescription>
      </CardHeader>
      {detail.assessment.status === "Published" && canAttempt ? (
        <CardContent>
          <Button disabled={busy} onClick={() => void startAttempt()}>
            Start attempt
          </Button>
        </CardContent>
      ) : null}
    </Card>
  );

  const questionsView = !detail ? null : (
    <div className="flex flex-col gap-4">
      <Card>
        <CardHeader>
          <div className="flex items-start justify-between gap-3">
            <CardTitle>{detail.assessment.title}</CardTitle>
            <Badge>{detail.assessment.status}</Badge>
          </div>
          <CardDescription>
            {detail.questions.length} question{detail.questions.length === 1 ? "" : "s"}. Publish the assessment once it is complete.
          </CardDescription>
        </CardHeader>
        {detail.assessment.status === "Draft" ? (
          <CardContent>
            <Button disabled={busy || detail.questions.length === 0} onClick={() => void publishAssessment()}>
              Publish assessment
            </Button>
          </CardContent>
        ) : null}
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Questions</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col gap-3">
          {detail.questions.length === 0 ? (
            <EmptyState>Add questions before publishing.</EmptyState>
          ) : (
            detail.questions.map((question) => (
              <div key={question.id} className="rounded-md border border-border p-3 text-sm">
                <div className="mb-1 flex items-center justify-between text-xs text-muted-foreground">
                  <span>Question {question.displayOrder}</span>
                  <span>
                    {question.type} · {question.points} point{question.points === 1 ? "" : "s"}
                  </span>
                </div>
                <strong>{question.prompt}</strong>
                {question.options.length > 0 ? <p className="text-muted-foreground">Options: {question.options.join(" · ")}</p> : null}
                {question.correctAnswers.length > 0 ? <small className="text-primary">Correct: {question.correctAnswers.join(", ")}</small> : null}
              </div>
            ))
          )}
        </CardContent>
      </Card>
      {detail.assessment.status === "Draft" ? (
        <Card>
          <CardHeader>
            <CardTitle>Add question</CardTitle>
          </CardHeader>
          <CardContent>
            <FormLayout onSubmit={addQuestion}>
              <ErrorBanner message={formError} />
              <FormSection title="Question">
                <Field id="question-type" label="Type" required>
                  <Select id="question-type" value={questionType} onChange={(e) => setQuestionType(e.target.value)}>
                    <option>MultipleChoice</option>
                    <option>MultipleResponse</option>
                    <option>TrueFalse</option>
                    <option>ShortAnswer</option>
                    <option>Essay</option>
                    <option>FileUpload</option>
                  </Select>
                </Field>
                <Field id="question-prompt" label="Prompt" required>
                  <Textarea id="question-prompt" value={prompt} onChange={(e) => setPrompt(e.target.value)} rows={3} placeholder="Question prompt" />
                </Field>
                <Field id="question-points" label="Points" required className="max-w-xs">
                  <Input id="question-points" value={points} onChange={(e) => setPoints(e.target.value)} type="number" min="1" max="100" />
                </Field>
              </FormSection>
              <FormSection title="Answers" description="Needed for choice questions. Separate entries with commas.">
                <div className="grid gap-3 sm:grid-cols-2">
                  <Field id="question-options" label="Options" required={isChoice}>
                    <Input id="question-options" value={options} onChange={(e) => setOptions(e.target.value)} placeholder="Comma separated" />
                  </Field>
                  <Field id="question-correct" label="Correct answers" required={isChoice}>
                    <Input id="question-correct" value={correctAnswers} onChange={(e) => setCorrectAnswers(e.target.value)} placeholder="Comma separated" />
                  </Field>
                </div>
              </FormSection>
              <FormActions busy={busy} busyLabel="Adding…" submitLabel="Add question" />
            </FormLayout>
          </CardContent>
        </Card>
      ) : null}
    </div>
  );

  const gradingView = !detail ? null : (
    <Card>
      <CardHeader>
        <CardTitle>Teacher review</CardTitle>
        <CardDescription>Attempts for {detail.assessment.title}.</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        {attempts.length === 0 ? (
          <EmptyState>No learner attempts yet.</EmptyState>
        ) : (
          attempts.map((item) => (
            <div key={item.id} className="flex flex-col gap-2 rounded-md border border-border p-3 text-sm md:flex-row md:items-center md:justify-between">
              <span>
                <strong className="block">Attempt {item.attemptNumber}</strong>
                <small className="text-muted-foreground">
                  {item.status} · {item.scorePoints}/{item.possiblePoints}
                  {item.percentage != null ? ` · ${item.percentage}%` : ""}
                </small>
              </span>
              {item.status === "Submitted" ? (
                <div className="flex flex-wrap gap-2">
                  <Input className="w-24" aria-label="Score" value={gradeScore} onChange={(e) => setGradeScore(e.target.value)} type="number" min="0" max={item.possiblePoints} placeholder="Score" />
                  <Input className="min-w-40 flex-1" aria-label="Feedback" value={gradeFeedback} onChange={(e) => setGradeFeedback(e.target.value)} placeholder="Feedback" />
                  <Button variant="secondary" disabled={busy} onClick={() => void gradeAttempt(item)}>
                    Grade
                  </Button>
                </div>
              ) : null}
            </div>
          ))
        )}
      </CardContent>
    </Card>
  );

  const panelOpen = creating || detail !== null;

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
              <Button onClick={openNew}>
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
                  <small className="text-muted-foreground">{assessment.questionCount} question{assessment.questionCount === 1 ? "" : "s"}</small>
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
              {canManage ? <TabsContent value="questions">{questionsView}</TabsContent> : null}
              {canGrade ? <TabsContent value="grading">{gradingView}</TabsContent> : null}
            </Tabs>
          ) : (
            overviewView
          )}
        </div>
      </SidePanel>
    </section>
  );
}

function AttemptPanel({
  attempt,
  answers,
  setAnswers,
  busy,
  canAttempt,
  onSubmit,
}: {
  attempt: Attempt;
  answers: Record<string, string>;
  setAnswers: React.Dispatch<React.SetStateAction<Record<string, string>>>;
  busy: boolean;
  canAttempt: boolean;
  onSubmit: () => void;
}) {
  const editable = canAttempt && attempt.attempt.status === "InProgress";
  const setAnswer = (id: string, value: string) => setAnswers((current) => ({ ...current, [id]: value }));
  return (
    <Card>
      <CardHeader>
        <div className="flex items-start justify-between gap-3">
          <div>
            <p className="text-xs text-muted-foreground">Attempt {attempt.attempt.attemptNumber}</p>
            <CardTitle className="text-xl">{attempt.assessmentTitle}</CardTitle>
          </div>
          <Badge>{attempt.attempt.status}</Badge>
        </div>
        <CardDescription>Answer each question, then submit your attempt.</CardDescription>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {attempt.attempt.status === "Graded" ? (
          <div className="rounded-md bg-muted p-4">
            <strong className="text-2xl">{attempt.attempt.percentage}%</strong>
            <span className="ml-2 text-sm text-muted-foreground">
              {attempt.attempt.scorePoints} of {attempt.attempt.possiblePoints} points
            </span>
          </div>
        ) : null}
        {attempt.questions.map((question) => (
          <div key={question.id} className="flex flex-col gap-2">
            <Label htmlFor={`answer-${question.id}`} className="flex flex-col items-start gap-0.5">
              <strong>
                {question.displayOrder}. {question.prompt}
              </strong>
              <small className="font-normal text-muted-foreground">
                {question.points} point{question.points === 1 ? "" : "s"}
              </small>
            </Label>
            {question.options.length > 0 ? (
              <Select id={`answer-${question.id}`} value={answers[question.id] || ""} onChange={(e) => setAnswer(question.id, e.target.value)} disabled={!editable}>
                <option value="">Choose an answer</option>
                {question.options.map((option) => (
                  <option value={option} key={option}>
                    {option}
                  </option>
                ))}
              </Select>
            ) : (
              <Textarea id={`answer-${question.id}`} value={answers[question.id] || ""} onChange={(e) => setAnswer(question.id, e.target.value)} rows={4} disabled={!editable} placeholder="Your answer" />
            )}
          </div>
        ))}
        {editable ? (
          <div>
            <Button disabled={busy} onClick={onSubmit}>
              Submit attempt
            </Button>
          </div>
        ) : null}
        {attempt.teacherFeedback ? (
          <p className="rounded-md border border-border px-3 py-2 text-sm">Teacher feedback: {attempt.teacherFeedback}</p>
        ) : null}
      </CardContent>
    </Card>
  );
}

function splitCsv(value: string) {
  return value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean);
}
function readError(exception: unknown, fallback: string) {
  return exception instanceof ApiError ? exception.message : fallback;
}
