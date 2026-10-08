import { lazy, Suspense, useEffect, useRef, useState } from "react";
import { Check, Circle, ExternalLink, Film, Hand, LogIn, LogOut, PhoneOff, Plus, RefreshCw, Send, ShieldCheck, ShieldOff, Square, X, XCircle } from "lucide-react";
import SidePanel from "@/components/SidePanel";
import { EmptyState, ErrorBanner, Field, FormActions, FormLayout, FormSection, ListRow, NoticeBanner, PageHeader, RowList } from "@/components/form";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { ApiError, apiRequest, getStoredSession } from "../lib/api";
import { useAuth } from "../lib/auth";
import type { LiveKitJoin } from "@/components/LiveClassRoom";

const LiveClassRoom = lazy(() => import("@/components/LiveClassRoom"));

type Session = {
  id: string;
  courseId: string | null;
  courseTitle: string | null;
  title: string;
  description: string | null;
  provider: string;
  joinUrl: string;
  hostUrl: string;
  startAtUtc: string;
  endAtUtc: string;
  status: string;
  requireApproval?: boolean;
  autoRecord?: boolean;
};
type JoinRequest = { userId: string; userName: string; status: string; requestedAtUtc: string };
type Announcement = {
  id: string;
  authorName: string;
  body: string;
  isPinned: boolean;
  createdAtUtc: string;
};
type ChatMessage = {
  id: string;
  userName: string;
  message: string;
  createdAtUtc: string;
};
type PollOption = { index: number; option: string; votes: number };
type Poll = {
  id: string;
  question: string;
  options: string[];
  isOpen: boolean;
  results: PollOption[];
};
type Attendance = {
  id: string;
  userId: string;
  userName: string;
  status: string;
  joinedAtUtc: string;
  leftAtUtc: string | null;
  durationSeconds: number;
};
type Recording = {
  id: string;
  sessionId: string;
  provider: string;
  providerRecordingId: string;
  recordingUrl: string | null;
  status: string;
  attemptCount: number;
  maxAttempts: number;
  lastError: string | null;
  requestedAtUtc: string;
  availableAtUtc: string | null;
  retainUntilUtc: string | null;
  videoId?: string | null;
};

/** What each Classroom menu item is about. The same classes are behind all of them, but each shows its own page and opens just its own part of a class. */
type TrackRecording = { id: string; userId: string; displayName: string; status: string; durationSeconds: number | null; sizeBytes: number; lastError: string | null; downloadUrl: string | null };
type HandRaise = { id: string; userId: string; userName: string; status: string; raisedAtUtc: string };

export const classroomViews = {
  overview: { title: "Live classes", description: "Join sessions, collaborate and review attendance and recordings.", empty: "No classes scheduled yet.", open: "View details", openLabel: "View details for", search: "Search classes", list: "Live classes" },
  recording: { title: "Class recordings", description: "Start, attach and find the recording of each class. Finished recordings are watched in the video library.", empty: "No classes yet, so there is nothing to record.", open: "Open recording", openLabel: "Open recording of", search: "Search classes", list: "Classes with recordings" },
  attendance: { title: "Class attendance", description: "Who joined each class, when they came and how long they stayed.", empty: "No classes yet, so there is no attendance.", open: "View attendance", openLabel: "View attendance for", search: "Search classes", list: "Classes with attendance" },
  chat: { title: "Class chat", description: "The conversation of each class: read what was said and take part.", empty: "No classes yet, so there is no chat.", open: "Open chat", openLabel: "Open chat of", search: "Search classes", list: "Class chats" },
} as const;

/** The name of the tool a class is held in, as people know it. */
export const providerName = (provider: string) => ({ livekit: "In this app", jitsi: "Jitsi", manual: "Meeting link", local: "Placeholder" } as Record<string, string>)[provider.toLowerCase()] ?? provider;

export default function LiveClassesPage({ initialView = "sessions", initialTab = "overview", initialSessionId }: { initialView?: "sessions" | "schedule"; initialTab?: string; initialSessionId?: string }) {
  const { session } = useAuth();
  const canManage = session?.permissions.includes("liveclass.manage") ?? false;
  const canCollaborate =
    session?.permissions.includes("collaboration.manage") ?? false;
  const canAttendance =
    session?.permissions.includes("attendance.read") ?? false;
  const [sessions, setSessions] = useState<Session[]>([]);
  const [room, setRoom] = useState<LiveKitJoin | null>(null);
  const [hands, setHands] = useState<HandRaise[]>([]);
  /** A Jitsi class shown inside the page. Other tools (Zoom, Meet, Teams) cannot be shown in a page and open in their own tab. */
  const [embedded, setEmbedded] = useState<string | null>(null);
  const [selected, setSelected] = useState<Session | null>(null);
  const [announcements, setAnnouncements] = useState<Announcement[]>([]);
  const [chat, setChat] = useState<ChatMessage[]>([]);
  const [polls, setPolls] = useState<Poll[]>([]);
  const [attendance, setAttendance] = useState<Attendance[]>([]);
  const [recording, setRecording] = useState<Recording | null>(null);
  const [consentGranted, setConsentGranted] = useState(false);
  const [chatMessage, setChatMessage] = useState("");
  const [announcement, setAnnouncement] = useState("");
  const [pollQuestion, setPollQuestion] = useState("");
  const [pollOptions, setPollOptions] = useState("Yes\nNo");
  const [title, setTitle] = useState("Weekly live class");
  const [description, setDescription] = useState("");
  const [courseId, setCourseId] = useState("");
  const [meetingUrl, setMeetingUrl] = useState("");
  const [waitingRoom, setWaitingRoom] = useState(true);
  const [autoRecord, setAutoRecord] = useState(false);
  /** Each person's own recording of a class held in LiveKit (when the server is set up for it). */
  const [tracks, setTracks] = useState<TrackRecording[]>([]);
  /** The learner has asked to come in and is waiting for the host. */
  const [waiting, setWaiting] = useState(false);
  const [requests, setRequests] = useState<JoinRequest[]>([]);
  const [recordingLink, setRecordingLink] = useState("");
  // What the organization holds classes in decides whether a meeting link is asked for and whether we can record.
  const [providerInfo, setProviderInfo] = useState<{ provider: string; requiresMeetingLink: boolean; canRecord: boolean } | null>(null);
  const [courses, setCourses] = useState<{ id: string; code: string; title: string }[]>([]);
  const [start, setStart] = useState(
    toLocalInput(new Date(Date.now() + 3600000)),
  );
  const [end, setEnd] = useState(toLocalInput(new Date(Date.now() + 7200000)));
  const [busy, setBusy] = useState(false);
  const [scheduling, setScheduling] = useState(initialView === "schedule" && canManage);
  const [search, setSearch] = useState("");
  const [sessionTab, setSessionTab] = useState(initialTab);
  const [error, setError] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  // A join or recording link names one class; it is opened once, when the list first arrives.
  const linkHandled = useRef(false);

  async function loadSessions() {
    try {
      const items = await apiRequest<Session[]>(
        "/api/v1/tenant/live-classes/sessions",
      );
      setSessions(items);
      if (initialSessionId && !linkHandled.current) {
        linkHandled.current = true;
        const linked = items.find((item) => item.id === initialSessionId);
        if (linked) openDetails(linked);
        else setError("That class is not available to you, or it no longer exists.");
      }
      if (selected) {
        const refreshed = items.find((item) => item.id === selected.id);
        if (refreshed) await selectSession(refreshed);
      }
    } catch (exception) {
      setError(readError(exception, "Unable to load live classes."));
    }
  }

  async function selectSession(item: Session) {
    setSelected(item);
    setConsentGranted(false);
    setRecording(null);
    setError(null);
    try {
      const recordingRequest = apiRequest<Recording>(
        `/api/v1/tenant/live-classes/sessions/${item.id}/recording`,
      ).catch((exception) => {
        if (exception instanceof ApiError && exception.status === 404)
          return null;
        throw exception;
      });
      const requests: Promise<unknown>[] = [
        apiRequest<Announcement[]>(
          `/api/v1/tenant/live-classes/sessions/${item.id}/announcements`,
        ),
        apiRequest<ChatMessage[]>(
          `/api/v1/tenant/live-classes/sessions/${item.id}/chat`,
        ),
        apiRequest<Poll[]>(
          `/api/v1/tenant/live-classes/sessions/${item.id}/polls`,
        ),
        recordingRequest,
      ];
      if (canAttendance)
        requests.push(
          apiRequest<Attendance[]>(
            `/api/v1/tenant/live-classes/sessions/${item.id}/attendance`,
          ),
        );
      const values = await Promise.all(requests);
      setAnnouncements(values[0] as Announcement[]);
      setChat((values[1] as ChatMessage[]).reverse());
      setPolls(values[2] as Poll[]);
      setRecording(values[3] as Recording | null);
      if (canAttendance) setAttendance(values[4] as Attendance[]);
      void loadHands(item.id);
    } catch (exception) {
      setError(
        readError(exception, "Unable to load session collaboration data."),
      );
    }
  }

  useEffect(() => {
    if (!canManage) return;
    apiRequest<{ provider: string; requiresMeetingLink: boolean; canRecord: boolean }>("/api/v1/tenant/live-classes/provider")
      .then((info) => setProviderInfo(info && typeof info === "object" && "provider" in info ? info : null))
      .catch(() => setProviderInfo(null));
  }, [canManage]);

  // The courses a class can belong to: the ones learners can actually be enrolled in.
  useEffect(() => {
    if (!canManage) return;
    apiRequest<{ id: string; code: string; title: string; status: string }[]>("/api/v1/tenant/courses")
      .then((list) => setCourses(Array.isArray(list) ? list.filter((course) => course.status === "Published") : []))
      .catch(() => setCourses([]));
  }, [canManage]);

  useEffect(() => {
    void loadSessions();
  }, []);

  // People join while the class is open, so the list is fetched afresh each time the tab is opened.
  useEffect(() => {
    if (sessionTab !== "attendance" || !selected || !canAttendance) return;
    apiRequest<Attendance[]>(`/api/v1/tenant/live-classes/sessions/${selected.id}/attendance`)
      .then((items) => setAttendance(items))
      .catch(() => undefined);
  }, [sessionTab, selected?.id, canAttendance]);

  useEffect(() => {
    if (!selected) return;
    const controller = new AbortController();
    const stored = getStoredSession();
    const streamEvents = async () => {
      try {
        const response = await fetch(
          `/api/v1/tenant/live-classes/sessions/${selected.id}/events`,
          {
            headers: {
              Authorization: `Bearer ${stored?.accessToken ?? ""}`,
              "X-Tenant-Slug": stored?.tenant.slug ?? "",
            },
            signal: controller.signal,
          },
        );
        if (!response.ok || !response.body) return;
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";
        while (!controller.signal.aborted) {
          const result = await reader.read();
          if (result.done) break;
          buffer += decoder.decode(result.value, { stream: true });
          const frames = buffer.split("\n\n");
          buffer = frames.pop() ?? "";
          for (const frame of frames)
            if (frame.includes("event: refresh")) await selectSession(selected);
        }
      } catch (exception) {
        if (
          !(
            exception instanceof DOMException && exception.name === "AbortError"
          )
        )
          return;
      }
    };
    void streamEvents();
    return () => controller.abort();
  }, [selected?.id]);

  function openSchedule() {
    setSelected(null);
    setTitle("Weekly live class");
    setDescription("");
    setCourseId("");
    setMeetingUrl("");
    setStart(toLocalInput(new Date(Date.now() + 3600000)));
    setEnd(toLocalInput(new Date(Date.now() + 7200000)));
    setProblem(null);
    setNotice(null);
    setScheduling(true);
  }

  function openDetails(item: Session) {
    setScheduling(false);
    setNotice(null);
    setAnnouncements([]);
    setChat([]);
    setPolls([]);
    setAttendance([]);
    void selectSession(item);
  }

  function closeDetails() {
    setSelected(null);
    setError(null);
    setNotice(null);
    setRoom(null);       // closing the panel leaves the class room or meeting
    setWaiting(false);
    setRequests([]);
    setEmbedded(null);
  }

  async function createSession(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!title.trim()) return setProblem("Enter a title for the class.");
    const startDate = new Date(start);
    const endDate = new Date(end);
    if (!start || !end || Number.isNaN(startDate.getTime()) || Number.isNaN(endDate.getTime()))
      return setProblem("Enter a start and an end time.");
    if (endDate.getTime() <= startDate.getTime())
      return setProblem("The class must end after it starts.");
    if (providerInfo?.requiresMeetingLink && !/^https:\/\/\S+$/i.test(meetingUrl.trim()))
      return setProblem("Paste the meeting link (an https address) for this class.");
    setBusy(true);
    setError(null);
    setProblem(null);
    try {
      await apiRequest("/api/v1/tenant/live-classes/sessions", {
        method: "POST",
        body: JSON.stringify({
          title,
          description,
          courseId: courseId || null,
          startAtUtc: startDate.toISOString(),
          endAtUtc: endDate.toISOString(),
          requireApproval: waitingRoom,
          ...(autoRecord && courseId && providerInfo?.provider.toLowerCase() === "livekit" ? { autoRecord: true } : {}),
          ...(providerInfo?.requiresMeetingLink ? { meetingUrl: meetingUrl.trim() } : {}),
        }),
      });
      setScheduling(false);
      setNotice(`Class “${title.trim()}” scheduled.`);
      await loadSessions();
    } catch (exception) {
      setProblem(readError(exception, "Unable to schedule the class."));
    } finally {
      setBusy(false);
    }
  }

  async function join() {
    if (!selected) return;
    setBusy(true);
    try {
      const result = await apiRequest<{ status?: string; meetingUrl: string; liveKit: LiveKitJoin | null }>(
        `/api/v1/tenant/live-classes/sessions/${selected.id}/join`,
        { method: "POST" },
      );
      // A class with a waiting room: the person has asked to come in and now waits for the host.
      if (result.status === "Waiting") { setWaiting(true); return; }
      setWaiting(false);
      if (result.liveKit) setRoom(result.liveKit);
      else if (selected.provider.toLowerCase() === "jitsi" && result.meetingUrl.toLowerCase().startsWith("https://")) setEmbedded(result.meetingUrl);
      else window.open(result.meetingUrl, "_blank", "noopener,noreferrer");
      await loadSessions();
    } catch (exception) {
      setError(readError(exception, "Unable to join the class."));
    } finally {
      setBusy(false);
    }
  }
  /** Ends the class: nobody can join afterwards, and it stops counting as something to do. */
  async function closeClass() {
    if (!selected) return;
    if (!window.confirm(`Close "${selected.title}"? Nobody will be able to join it afterwards.`)) return;
    setBusy(true);
    setError(null);
    try {
      const result = await apiRequest<{ status: string }>(`/api/v1/tenant/live-classes/sessions/${selected.id}/close`, { method: "POST" });
      setSelected({ ...selected, status: result.status });
      setNotice("Class closed.");
      await loadSessions();
    } catch (exception) {
      setError(readError(exception, "Unable to close the class."));
    } finally {
      setBusy(false);
    }
  }
  async function leave() {
    if (!selected) return;
    setBusy(true);
    try {
      await apiRequest(
        `/api/v1/tenant/live-classes/sessions/${selected.id}/leave`,
        { method: "POST" },
      );
    } catch (exception) {
      setError(readError(exception, "Unable to record departure."));
    } finally {
      setBusy(false);
    }
  }
  async function sendAnnouncement(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!selected) return;
    const body = (announcement ?? "").trim();
    if (!body) return setError("Announcement cannot be empty.");
    setBusy(true);
    setError(null);
    try {
      await apiRequest(
        `/api/v1/tenant/live-classes/sessions/${selected.id}/announcements`,
        { method: "POST", body: JSON.stringify({ body, isPinned: false }) },
      );
      setAnnouncement("");
      await selectSession(selected);
    } catch (exception) {
      setError(readError(exception, "Unable to publish the announcement."));
    } finally {
      setBusy(false);
    }
  }

  async function sendChat(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!selected) return;
    const message = (chatMessage ?? "").trim();
    if (!message) return setError("Message cannot be empty.");
    setBusy(true);
    setError(null);
    try {
      await apiRequest(
        `/api/v1/tenant/live-classes/sessions/${selected.id}/chat`,
        { method: "POST", body: JSON.stringify({ message }) },
      );
      setChatMessage("");
      await selectSession(selected);
    } catch (exception) {
      setError(readError(exception, "Unable to send the message."));
    } finally {
      setBusy(false);
    }
  }
  /** Whose hands are up in this class. Everyone in the class can see them. */
  async function loadHands(sessionId: string) {
    try {
      const list = await apiRequest<HandRaise[]>(`/api/v1/tenant/live-classes/sessions/${sessionId}/hand-raises`);
      setHands(Array.isArray(list) ? list : []);
    } catch { setHands([]); }
  }
  const myHandUp = hands.some((item) => item.userId === session?.user.id);
  async function toggleHand() {
    if (!selected) return;
    setBusy(true);
    setError(null);
    try {
      await apiRequest(`/api/v1/tenant/live-classes/sessions/${selected.id}/hand-raise`, { method: "POST", body: JSON.stringify({ raised: !myHandUp }) });
      await loadHands(selected.id);
    } catch (exception) {
      setError(readError(exception, myHandUp ? "Unable to lower your hand." : "Unable to raise your hand."));
    } finally {
      setBusy(false);
    }
  }
  /** The host or staff put someone's hand down, usually after answering them. */
  async function lowerHand(userId: string) {
    if (!selected) return;
    try {
      await apiRequest(`/api/v1/tenant/live-classes/sessions/${selected.id}/hand-raises/${userId}/lower`, { method: "POST" });
      await loadHands(selected.id);
    } catch (exception) { setError(readError(exception, "Unable to lower the hand.")); }
  }
  async function createPoll(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!selected) return;
    if (!pollQuestion.trim()) return setError("Enter a poll question.");
    if (pollOptions.split("\n").filter((line) => line.trim()).length < 2)
      return setError("Enter at least two poll options, one per line.");
    setBusy(true);
    setError(null);
    try {
      await apiRequest(
        `/api/v1/tenant/live-classes/sessions/${selected.id}/polls`,
        {
          method: "POST",
          body: JSON.stringify({
            question: pollQuestion,
            options: pollOptions.split("\n"),
          }),
        },
      );
      setPollQuestion("");
      await selectSession(selected);
    } catch (exception) {
      setError(readError(exception, "Unable to create the poll."));
    } finally {
      setBusy(false);
    }
  }
  async function vote(pollId: string, optionIndex: number) {
    if (!selected) return;
    try {
      await apiRequest(
        `/api/v1/tenant/live-classes/sessions/${selected.id}/polls/${pollId}/vote`,
        { method: "POST", body: JSON.stringify({ optionIndex }) },
      );
      await selectSession(selected);
    } catch (exception) {
      setError(readError(exception, "Unable to submit the poll vote."));
    }
  }
  async function setConsent(granted: boolean) {
    if (!selected) return;
    setBusy(true);
    try {
      await apiRequest(
        `/api/v1/tenant/live-classes/sessions/${selected.id}/consent`,
        { method: "POST", body: JSON.stringify({ granted }) },
      );
      setConsentGranted(granted);
    } catch (exception) {
      setError(readError(exception, "Unable to save recording consent."));
    } finally {
      setBusy(false);
    }
  }
  async function requestRecording() {
    if (!selected) return;
    setBusy(true);
    try {
      const result = await apiRequest<Recording>(
        `/api/v1/tenant/live-classes/sessions/${selected.id}/recording/request`,
        { method: "POST" },
      );
      setRecording(result);
    } catch (exception) {
      setError(readError(exception, "Unable to request the recording."));
    } finally {
      setBusy(false);
    }
  }
  /** A recording made in another tool (Zoom, Meet, Teams, Jitsi) is attached by its link. */
  async function attachRecordingLink(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!selected) return;
    if (!/^https:\/\/\S+$/i.test(recordingLink.trim())) return setError("Paste the recording's link (an https address).");
    setBusy(true);
    setError(null);
    try {
      const result = await apiRequest<Recording>(`/api/v1/tenant/live-classes/sessions/${selected.id}/recording/link`, { method: "POST", body: JSON.stringify({ url: recordingLink.trim() }) });
      setRecording(result);
      setRecordingLink("");
      setNotice("Recording link attached.");
    } catch (exception) {
      setError(readError(exception, "Unable to attach the recording link."));
    } finally {
      setBusy(false);
    }
  }
  /** Starting and stopping the recording of a class held in LiveKit (the file then arrives in the video library). */
  async function controlLiveRecording(action: "start" | "stop") {
    if (!selected) return;
    setBusy(true);
    setError(null);
    try {
      setRecording(await apiRequest<Recording>(`/api/v1/tenant/live-classes/sessions/${selected.id}/recording/${action}`, { method: "POST" }));
    } catch (exception) {
      setError(readError(exception, action === "start" ? "Unable to start the recording." : "Unable to stop the recording."));
    } finally {
      setBusy(false);
    }
  }
  async function retryRecording() {
    if (!selected) return;
    setBusy(true);
    try {
      const result = await apiRequest<Recording>(
        `/api/v1/tenant/live-classes/sessions/${selected.id}/recording/retry`,
        { method: "POST" },
      );
      setRecording(result);
    } catch (exception) {
      setError(readError(exception, "Unable to retry the recording."));
    } finally {
      setBusy(false);
    }
  }

  const inCall = room !== null || embedded !== null;
  // While waiting to be let in, keep asking where things stand; when admitted, come in without another click.
  useEffect(() => {
    if (!waiting || !selected) return;
    const id = selected.id;
    let current = true;
    const timer = window.setInterval(() => {
      apiRequest<{ status: string; closed?: boolean }>(`/api/v1/tenant/live-classes/sessions/${id}/join-status`)
        .then((result) => {
          if (!current || !result) return;
          if (result.closed) { setWaiting(false); setError("This class has ended."); }
          else if (result.status === "Declined") { setWaiting(false); setError("The host did not let you into this class."); }
          else if (result.status === "Admitted" || result.status === "NotRequired") { setWaiting(false); void join(); }
        })
        .catch(() => undefined);
    }, 3000);
    return () => { current = false; window.clearInterval(timer); };
  }, [waiting, selected?.id]);

  // The host sees who is waiting, and the list follows the live updates and a slow check.
  async function loadRequests(sessionId: string) {
    try {
      const list = await apiRequest<JoinRequest[]>(`/api/v1/tenant/live-classes/sessions/${sessionId}/join-requests`);
      setRequests(Array.isArray(list) ? list : []);
    } catch { setRequests([]); }
  }
  useEffect(() => {
    if (!selected || !canManage || !selected.requireApproval) { setRequests([]); return; }
    const id = selected.id;
    void loadRequests(id);
    const timer = window.setInterval(() => { void loadRequests(id); }, 4000);
    return () => window.clearInterval(timer);
  }, [selected?.id, selected?.requireApproval, canManage]);
  async function decide(userId: string, action: "admit" | "decline") {
    if (!selected) return;
    try {
      await apiRequest(`/api/v1/tenant/live-classes/sessions/${selected.id}/join-requests/${userId}/${action}`, { method: "POST" });
      await loadRequests(selected.id);
    } catch (exception) { setError(readError(exception, action === "admit" ? "Unable to let the person in." : "Unable to decline.")); }
  }
  async function admitAll() {
    if (!selected) return;
    try {
      await apiRequest(`/api/v1/tenant/live-classes/sessions/${selected.id}/join-requests/admit-all`, { method: "POST" });
      await loadRequests(selected.id);
    } catch (exception) { setError(readError(exception, "Unable to let everyone in.")); }
  }
  const isLiveKit = (selected?.provider ?? "").toLowerCase() === "livekit";
  /** The host switches off one person's microphone (or everyone's but their own). People can switch theirs back on. */
  async function muteInRoom(userId: string | null) {
    if (!selected) return;
    setError(null);
    try {
      const result = await apiRequest<{ muted: number }>(`/api/v1/tenant/live-classes/sessions/${selected.id}/${userId ? `mute/${userId}` : "mute-all"}`, { method: "POST" });
      setNotice(result.muted === 0 ? "Nobody had a microphone on." : result.muted === 1 ? "1 microphone switched off." : `${result.muted} microphones switched off.`);
    } catch (exception) {
      setError(readError(exception, "Unable to switch microphones off."));
    }
  }
  const tracksWanted = canManage && isLiveKit && !!selected && recording !== null;
  const recordingState = recording?.status;
  useEffect(() => {
    if (!tracksWanted || !selected) { setTracks([]); return; }
    let current = true;
    apiRequest<TrackRecording[]>(`/api/v1/tenant/live-classes/sessions/${selected.id}/recording/tracks`).then((list) => { if (current && Array.isArray(list)) setTracks(list); }).catch(() => { if (current) setTracks([]); });
    return () => { current = false; };
  }, [tracksWanted, selected?.id, recordingState]);
  // A recording being made or saved finishes by itself: check back until it does.
  const recordingBusy = isLiveKit && (recording?.status === "Recording" || recording?.status === "Processing");
  useEffect(() => {
    if (!recordingBusy || !selected) return;
    const id = selected.id;
    const timer = window.setInterval(() => {
      apiRequest<Recording>(`/api/v1/tenant/live-classes/sessions/${id}/recording`).then((value) => { if (value && typeof value === "object") setRecording(value); }).catch(() => undefined);
    }, 5000);
    return () => window.clearInterval(timer);
  }, [recordingBusy, selected?.id]);  

  const needle = search.trim().toLowerCase();
  // A class that is Completed or Cancelled can no longer be joined.
  // Only the built-in provider records itself; classes held elsewhere are recorded there and attached by link.
  const recordsHere = (selected?.provider ?? "local").toLowerCase() === "local";
  const joinable = selected !== null && (selected.status === "Scheduled" || selected.status === "Live");
  const view = classroomViews[(initialTab in classroomViews ? initialTab : "overview") as keyof typeof classroomViews];
  const focused = view !== classroomViews.overview;
  // The focused pages leave out cancelled classes: nothing is recorded, attended or said in them.
  const relevant = (item: Session) => !focused || item.status !== "Cancelled";
  const shown = sessions.filter((item) => relevant(item) && (!needle || item.title.toLowerCase().includes(needle) || (item.courseTitle ?? "").toLowerCase().includes(needle)));

  return (
    <section className="flex flex-col gap-4 text-foreground">
      <PageHeader
        title={view.title}
        description={view.description}
        actions={
          <>
            <Button variant="outline" onClick={() => void loadSessions()}><RefreshCw className="mr-1.5 h-4 w-4" aria-hidden />Refresh</Button>
            {canManage && !focused ? <Button onClick={openSchedule}><Plus className="mr-1 h-4 w-4" aria-hidden />Schedule class</Button> : null}
          </>
        }
      />
      {!selected ? <ErrorBanner message={error} /> : null}
      {!selected ? <NoticeBanner message={notice} /> : null}

      <Input className="max-w-sm" type="search" placeholder={view.search} aria-label={view.search} value={search} onChange={(event) => setSearch(event.target.value)} />

      {sessions.length === 0 ? <EmptyState>{classroomViews.overview.empty}</EmptyState> : shown.length === 0 && !needle ? <EmptyState>{view.empty}</EmptyState> : shown.length === 0 ? <EmptyState>No classes match.</EmptyState> : (
        <RowList label={view.list}>
          {shown.map((item) => (
            <ListRow key={item.id} selected={item.id === selected?.id} columns="sm:grid-cols-[minmax(0,1fr)_auto] md:grid-cols-[minmax(0,2fr)_110px_minmax(0,1.5fr)_auto]">
              <div className="min-w-0">
                <strong className="block truncate">{item.title}</strong>
                <small className="text-muted-foreground">{item.courseTitle ?? item.description ?? "Live learning session."}</small>
              </div>
              <div className="flex flex-wrap gap-1.5"><Badge variant={item.status === "Live" ? "default" : "secondary"}>{item.status}</Badge><Badge variant="outline" title="The tool this class was created with. Changing the setting later does not move existing classes.">{providerName(item.provider)}</Badge></div>
              <div className="hidden text-muted-foreground md:block"><small className="block">Starts</small>{formatDate(item.startAtUtc)}</div>
              <div className="flex justify-end">
                <Button type="button" size="sm" variant="secondary" aria-label={`${view.openLabel} ${item.title}`} aria-expanded={item.id === selected?.id} onClick={() => openDetails(item)}>{view.open}</Button>
              </div>
            </ListRow>
          ))}
        </RowList>
      )}

      <SidePanel open={scheduling} label="Schedule a class" onClose={() => setScheduling(false)}>
        <FormLayout onSubmit={createSession}>
          <ErrorBanner message={problem} />
          <FormSection title="About the class" description="The class is created through the configured live-class provider.">
            <Field id="live-title" label="Title" required><Input id="live-title" value={title} onChange={(e) => setTitle(e.target.value)} /></Field>
            <Field id="live-course" label="Course" hint="Learners enrolled in this course will see the class. Leave empty for a class only staff can see.">
              <Select id="live-course" value={courseId} onChange={(e) => setCourseId(e.target.value)}>
                <option value="">No course (staff only)</option>
                {courses.map((course) => <option key={course.id} value={course.id}>{course.code} · {course.title}</option>)}
              </Select>
            </Field>
            {providerInfo?.requiresMeetingLink ? (
              <Field id="live-link" label="Meeting link" required hint="Create the meeting in Zoom, Google Meet or Teams, then paste its link here. Learners are sent to it.">
                <Input id="live-link" type="url" placeholder="https://" value={meetingUrl} onChange={(e) => setMeetingUrl(e.target.value)} />
              </Field>
            ) : null}
            <Field id="live-description" label="Description"><Textarea id="live-description" value={description} onChange={(e) => setDescription(e.target.value)} rows={2} /></Field>
          </FormSection>
          <FormSection title="Who comes in">
            <label className="flex items-start gap-2 text-sm">
              <input type="checkbox" className="mt-1" checked={waitingRoom} onChange={(e) => setWaitingRoom(e.target.checked)} />
              <span><strong className="block">Learners wait to be let in</strong><span className="text-muted-foreground">Learners ask to join and you let them in (one by one, or all at once). Turn it off to let enrolled learners straight in.</span></span>
            </label>
          </FormSection>
          {providerInfo?.provider.toLowerCase() === "livekit" ? (
            <FormSection title="Recording">
              <label className="flex items-start gap-2 text-sm">
                <input type="checkbox" className="mt-1" checked={autoRecord && !!courseId} disabled={!courseId} onChange={(e) => setAutoRecord(e.target.checked)} />
                <span><strong className="block">Record this class automatically</strong><span className="text-muted-foreground">{courseId ? "Recording starts when the first person is in the room and stops when the class ends. Choosing this is your agreement to the class being recorded." : "Choose a course first: the recording is saved in its video library."}</span></span>
              </label>
            </FormSection>
          ) : null}
          <FormSection title="When">
            <div className="grid gap-3 sm:grid-cols-2">
              <Field id="live-start" label="Starts" required><Input id="live-start" type="datetime-local" value={start} onChange={(e) => setStart(e.target.value)} /></Field>
              <Field id="live-end" label="Ends" required><Input id="live-end" type="datetime-local" value={end} onChange={(e) => setEnd(e.target.value)} /></Field>
            </div>
          </FormSection>
          <FormActions busy={busy} submitLabel="Schedule class" busyLabel="Scheduling…" onCancel={() => setScheduling(false)} />
        </FormLayout>
      </SidePanel>

      <SidePanel open={selected !== null} label={selected?.title ?? "Live class"} onClose={closeDetails} wide={inCall}>
        {selected ? (
          <div className={inCall ? "grid gap-4 lg:grid-cols-[minmax(0,1fr)_24rem]" : "flex flex-col gap-4"} data-testid="class-layout">
            {/* In a class the video takes the screen and the tabs (chat, polls...) sit beside it; otherwise everything stacks. */}
            <div className={inCall ? "flex min-w-0 flex-col gap-3" : "contents"}>
            <ErrorBanner message={error} />
            <NoticeBanner message={notice} />
            {room ? (
              <Suspense fallback={<p className="text-sm text-muted-foreground">Loading the class room…</p>}>
                <LiveClassRoom join={room} raised={hands.map((item) => item.userId)} onMute={canManage ? muteInRoom : undefined} displayName={session?.user.displayName ?? "You"} onLeave={() => { setRoom(null); void leave(); }} />
              </Suspense>
            ) : null}
            {requests.length > 0 ? (
              <section aria-label="Waiting to join" className="flex flex-col gap-2 rounded-md border border-yellow-500/50 bg-yellow-500/5 p-3">
                <div className="flex items-center justify-between gap-2">
                  <strong className="text-sm">Waiting to join ({requests.length})</strong>
                  <Button type="button" size="sm" onClick={() => void admitAll()}><Check className="mr-1.5 h-4 w-4" aria-hidden />Admit all</Button>
                </div>
                <ul className="flex flex-col gap-1.5">
                  {requests.map((item) => (
                    <li key={item.userId} className="flex items-center justify-between gap-2 text-sm">
                      <span>{item.userName}<small className="ml-2 text-muted-foreground">{formatDate(item.requestedAtUtc)}</small></span>
                      <span className="flex gap-1.5">
                        <Button type="button" size="sm" variant="secondary" aria-label={`Let ${item.userName} in`} onClick={() => void decide(item.userId, "admit")}><Check className="mr-1 h-4 w-4" aria-hidden />Admit</Button>
                        <Button type="button" size="sm" variant="outline" aria-label={`Decline ${item.userName}`} onClick={() => void decide(item.userId, "decline")}><X className="mr-1 h-4 w-4" aria-hidden />Decline</Button>
                      </span>
                    </li>
                  ))}
                </ul>
              </section>
            ) : null}
            {waiting ? (
              <section aria-label="Waiting for the host" role="status" className="flex flex-col items-start gap-2 rounded-md border border-border p-4 text-sm">
                <strong>Waiting for the host to let you in…</strong>
                <span className="text-muted-foreground">You will join automatically as soon as you are let in. You can leave this screen open.</span>
                <Button type="button" variant="outline" size="sm" onClick={() => setWaiting(false)}>Cancel</Button>
              </section>
            ) : null}
            {embedded ? (
              <section aria-label="Jitsi meeting" className="flex flex-col gap-2">
                <iframe title={`${selected.title} (Jitsi)`} src={embedded} className="h-[calc(100vh-14rem)] min-h-[22rem] w-full rounded-md border border-border bg-black" allow="camera; microphone; display-capture; fullscreen; autoplay; clipboard-write" referrerPolicy="strict-origin-when-cross-origin" />
                <div className="flex flex-wrap items-center gap-2">
                  <Button type="button" variant="outline" size="sm" asChild><a href={embedded} target="_blank" rel="noreferrer noopener"><ExternalLink className="mr-1.5 h-4 w-4" aria-hidden />Open in a new tab</a></Button>
                  <Button type="button" variant="softDestructive" size="sm" onClick={() => { setEmbedded(null); void leave(); }}><PhoneOff className="mr-1.5 h-4 w-4" aria-hidden />Leave class</Button>
                  <small className="text-muted-foreground">Jitsi has its own chat inside the meeting. The Chat tab below is this system's class chat.</small>
                </div>
              </section>
            ) : null}
            </div>
            <div className={inCall ? "min-w-0 lg:self-start" : "contents"}>
            <Tabs value={sessionTab} onValueChange={setSessionTab}>
              {focused ? null : <TabsList>
                <TabsTrigger value="overview">Overview</TabsTrigger>
                <TabsTrigger value="announcements">Announcements</TabsTrigger>
                <TabsTrigger value="polls">Polls</TabsTrigger>
                <TabsTrigger value="chat">Chat</TabsTrigger>
                {canAttendance ? <TabsTrigger value="attendance">Attendance</TabsTrigger> : null}
                <TabsTrigger value="recording">Recording</TabsTrigger>
              </TabsList>}

              <TabsContent value="overview">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div className="flex flex-col gap-1">
                    <div><Badge>{selected.status}</Badge></div>
                    <h3 className="text-xl font-semibold">{selected.title}</h3>
                    <p className="text-sm text-muted-foreground">{selected.description || "Live learning session."}</p>
                    <small className="text-muted-foreground">{selected.provider === "manual" ? "Opens the meeting link set by the teacher." : selected.provider === "jitsi" ? "Opens in Jitsi Meet." : selected.provider === "livekit" ? "Held in the class room inside this app." : "Placeholder class room (no video)."}</small>
                    {joinable ? null : <p className="text-sm text-muted-foreground">This class has ended, so it can no longer be joined.</p>}
                    {selected.requireApproval ? <small className="text-muted-foreground">{canManage ? "This class has a waiting room: you let learners in." : "This class has a waiting room: the host lets you in after you press Join class."}</small> : null}
                    <small className="text-muted-foreground">
                      {formatDate(selected.startAtUtc)} – {formatDate(selected.endAtUtc)}
                    </small>
                  </div>
                  <div className="flex gap-2">
                    {joinable && !room && !embedded && !waiting ? <Button disabled={busy} onClick={() => void join()}><LogIn className="mr-1.5 h-4 w-4" aria-hidden />Join class</Button> : null}
                    {joinable ? <Button variant="outline" disabled={busy} onClick={() => void leave()}><LogOut className="mr-1.5 h-4 w-4" aria-hidden />Leave</Button> : null}
                    {joinable && canManage ? <Button variant="softDestructive" disabled={busy} onClick={() => void closeClass()}><XCircle className="mr-1.5 h-4 w-4" aria-hidden />Close class</Button> : null}
                  </div>
                </div>
              </TabsContent>

              <TabsContent value="announcements">
                {announcements.length === 0 ? <EmptyState>No announcements yet.</EmptyState> : (
                  <RowList label="Announcements">
                    {announcements.map((item) => (
                      <ListRow key={item.id}>
                        <div className="min-w-0">
                          <strong>{item.authorName}</strong>
                          <p>{item.body}</p>
                          <small className="text-muted-foreground">{formatDate(item.createdAtUtc)}</small>
                        </div>
                      </ListRow>
                    ))}
                  </RowList>
                )}
                {canManage ? (
                  <FormLayout onSubmit={sendAnnouncement}>
                    <FormSection title="New announcement">
                      <Field id="live-announcement" label="Announcement" required>
                        <Textarea id="live-announcement" value={announcement} onChange={(e) => setAnnouncement(e.target.value)} placeholder="Share an announcement" rows={2} />
                      </Field>
                    </FormSection>
                    <FormActions busy={busy} submitLabel="Post" busyLabel="Posting…" />
                  </FormLayout>
                ) : null}
              </TabsContent>

              <TabsContent value="polls">
                {polls.length === 0 ? <EmptyState>No polls yet.</EmptyState> : null}
                {polls.map((poll) => (
                  <div key={poll.id} className="flex flex-col gap-2">
                    <strong className="text-sm">{poll.question}</strong>
                    {poll.results.map((option) => (
                      <Button
                        key={option.index}
                        variant="outline"
                        className="justify-between"
                        disabled={!poll.isOpen || busy}
                        onClick={() => void vote(poll.id, option.index)}
                      >
                        <span>{option.option}</span>
                        <small className="text-muted-foreground">{option.votes} votes</small>
                      </Button>
                    ))}
                  </div>
                ))}
                {canManage ? (
                  <FormLayout onSubmit={createPoll}>
                    <FormSection title="New poll">
                      <Field id="live-poll-question" label="Poll question" required>
                        <Input id="live-poll-question" value={pollQuestion} onChange={(e) => setPollQuestion(e.target.value)} placeholder="Poll question" />
                      </Field>
                      <Field id="live-poll-options" label="Poll options, one per line" required>
                        <Textarea id="live-poll-options" value={pollOptions} onChange={(e) => setPollOptions(e.target.value)} rows={3} />
                      </Field>
                    </FormSection>
                    <FormActions busy={busy} submitLabel="Create poll" busyLabel="Creating…" />
                  </FormLayout>
                ) : null}
              </TabsContent>

              <TabsContent value="chat">
                <div className="flex items-center justify-between">
                  <h3 className="text-base font-semibold">Class chat</h3>
                  <Badge variant="outline">Live</Badge>
                </div>
                <div className="flex max-h-72 flex-col gap-2 overflow-y-auto rounded-md border border-border p-3">
                  {chat.length === 0 ? <p className="text-sm text-muted-foreground">No messages yet.</p> : null}
                  {chat.map((item) => (
                    <div key={item.id} className="text-sm">
                      <strong>{item.userName}</strong> <span>{item.message}</span>
                      <small className="block text-muted-foreground">{formatDate(item.createdAtUtc)}</small>
                    </div>
                  ))}
                </div>
                {canCollaborate ? (
                  <form className="flex items-end gap-2" onSubmit={sendChat}>
                    <div className="flex flex-1 flex-col gap-1.5">
                      <Label htmlFor="live-chat-message" className="sr-only">Chat message</Label>
                      <Input id="live-chat-message" value={chatMessage} onChange={(e) => setChatMessage(e.target.value)} placeholder="Write to the class" />
                    </div>
                    <Button variant="secondary" type="submit" disabled={busy}><Send className="mr-1.5 h-4 w-4" aria-hidden />Send</Button>
                  </form>
                ) : null}
                <div className="flex flex-col gap-2">
                  <div>
                    <Button variant={myHandUp ? "secondary" : "outline"} aria-pressed={myHandUp} disabled={busy} onClick={() => void toggleHand()}><Hand className="mr-1.5 h-4 w-4" aria-hidden />{myHandUp ? "Lower hand" : "Raise hand"}</Button>
                  </div>
                  {hands.length > 0 ? (
                    <section aria-label="Raised hands" className="flex flex-col gap-1.5 rounded-md border border-border p-2">
                      <small className="font-medium text-muted-foreground">Raised hands ({hands.length})</small>
                      <ul className="flex flex-col gap-1">
                        {hands.map((item) => (
                          <li key={item.id} className="flex items-center justify-between gap-2 text-sm">
                            <span className="flex items-center gap-1.5"><Hand className="h-3.5 w-3.5 text-yellow-500" aria-hidden />{item.userId === session?.user.id ? "You" : item.userName}<small className="text-muted-foreground">{formatDate(item.raisedAtUtc)}</small></span>
                            {canManage && item.userId !== session?.user.id ? <Button type="button" size="sm" variant="outline" aria-label={`Lower the hand of ${item.userName}`} onClick={() => void lowerHand(item.userId)}>Lower</Button> : null}
                          </li>
                        ))}
                      </ul>
                    </section>
                  ) : null}
                </div>
              </TabsContent>

              {canAttendance ? (
                <TabsContent value="attendance">
                  {attendance.length === 0 ? <EmptyState>No attendance recorded yet.</EmptyState> : (
                    <RowList label="Attendance">
                      {attendance.map((item) => (
                        <ListRow key={item.id} columns="sm:grid-cols-[minmax(0,1fr)_auto]">
                          <div className="min-w-0">
                            <strong className="block truncate">{item.userName}</strong>
                            <small className="text-muted-foreground">{formatDate(item.joinedAtUtc)}</small>
                          </div>
                          <div><Badge>{item.status}</Badge></div>
                        </ListRow>
                      ))}
                    </RowList>
                  )}
                </TabsContent>
              ) : null}

              <TabsContent value="recording">
                <div className="flex items-center justify-between">
                  <h3 className="text-base font-semibold">Recording and consent</h3>
                  <Badge variant="outline">{recording?.status ?? "Not requested"}</Badge>
                </div>
                <p className="text-sm text-muted-foreground">Recordings are retained according to the tenant policy and expire automatically.</p>
                <div className="flex flex-wrap gap-2">
                  <Button variant="secondary" disabled={busy} onClick={() => void setConsent(!consentGranted)}>
                    {consentGranted ? <ShieldOff className="mr-1.5 h-4 w-4" aria-hidden /> : <ShieldCheck className="mr-1.5 h-4 w-4" aria-hidden />}{consentGranted ? "Withdraw my consent" : "Give recording consent"}
                  </Button>
                  {canManage && recordsHere ? <Button disabled={busy} onClick={() => void requestRecording()}>Request recording</Button> : null}
                  {canManage && isLiveKit && joinable && recording?.status !== "Recording" && recording?.status !== "Processing" && recording?.status !== "Available" ? <Button disabled={busy} onClick={() => void controlLiveRecording("start")}><Circle className="mr-1.5 h-4 w-4" aria-hidden fill="currentColor" />{recording?.status === "Failed" ? "Start recording again" : "Start recording"}</Button> : null}
                  {canManage && isLiveKit && recording?.status === "Recording" ? <Button variant="softDestructive" disabled={busy} onClick={() => void controlLiveRecording("stop")}><Square className="mr-1.5 h-4 w-4" aria-hidden fill="currentColor" />Stop recording</Button> : null}
                  {canManage && !isLiveKit && recording?.status === "Failed" ? <Button variant="outline" disabled={busy} onClick={() => void retryRecording()}>Retry</Button> : null}
                  {recording?.recordingUrl ? (
                    <Button variant="outline" asChild>
                      <a href={recording.recordingUrl} target="_blank" rel="noreferrer"><Film className="mr-1.5 h-4 w-4" aria-hidden />Open recording</a>
                    </Button>
                  ) : null}
                </div>
                {isLiveKit ? (
                  <div className="flex flex-col gap-1 text-sm" data-testid="live-recording-state">
                    {selected.autoRecord ? <p className="text-muted-foreground">This class records itself: recording starts when the first person is in the room and stops when the class ends.</p> : null}
                    {recording?.status === "Recording" ? <p role="status" className="font-medium text-destructive">Recording now. Everyone in the class room is told. Stop it when the class is over.</p> : null}
                    {recording?.status === "Processing" ? <p role="status" className="text-muted-foreground">The recording has stopped and is being saved to the video library…</p> : null}
                    {recording?.status === "Available" && recording.videoId ? <p className="text-muted-foreground">Saved to the video library as a class recording. Enrolled learners can watch it once it is ready; it also gets streaming pieces and can be transcribed.</p> : null}
                    {!recording || recording.status === "Failed" ? <p className="text-muted-foreground">Recording saves everyone's video and sound as one video in the library. The host must have given consent first, and the class must be linked to a course.</p> : null}
                  </div>
                ) : null}
                {tracks.length > 0 ? (
                  <section aria-label="Recordings of each person" className="flex flex-col gap-1.5 rounded-md border border-border p-3 text-sm">
                    <strong>Each person on their own</strong>
                    <ul className="flex flex-col gap-1">
                      {tracks.map((item) => (
                        <li key={item.id} className="flex flex-wrap items-center justify-between gap-2">
                          <span>{item.displayName}{item.durationSeconds ? <small className="ml-2 text-muted-foreground">{Math.floor(item.durationSeconds / 60)}:{String(item.durationSeconds % 60).padStart(2, "0")}</small> : null}</span>
                          <span className="flex items-center gap-2">
                            <Badge variant="outline">{item.status}</Badge>
                            {item.downloadUrl ? <a className="text-primary hover:underline" href={item.downloadUrl} target="_blank" rel="noreferrer" aria-label={`Open the recording of ${item.displayName}`}>Open</a> : null}
                            {item.lastError ? <small className="text-destructive">{item.lastError}</small> : null}
                          </span>
                        </li>
                      ))}
                    </ul>
                  </section>
                ) : null}
                {!recordsHere && !(isLiveKit && (recording?.status === "Recording" || recording?.status === "Processing" || (recording?.status === "Available" && recording.videoId))) ? (
                  <div className="flex flex-col gap-3 rounded-md border border-border p-3 text-sm">
                    <p className="text-muted-foreground">{isLiveKit ? "Or, if the class was recorded another way, attach the recording's link here." : `This class is held in ${selected.provider === "jitsi" ? "Jitsi" : "another tool"}, so the recording is made there. Once it is ready, attach its link here.`}</p>
                    {canManage ? (
                      <form className="flex flex-wrap items-end gap-2" onSubmit={attachRecordingLink}>
                        <Field id="live-recording-link" label="Recording link" className="min-w-64 flex-1">
                          <Input id="live-recording-link" type="url" placeholder="https://" value={recordingLink} onChange={(e) => setRecordingLink(e.target.value)} />
                        </Field>
                        <Button type="submit" variant="secondary" disabled={busy}>Attach recording link</Button>
                      </form>
                    ) : null}
                  </div>
                ) : null}
                {recording?.retainUntilUtc ? (
                  <small className="text-muted-foreground">
                    Retained until {formatDate(recording.retainUntilUtc)} · attempt {recording.attemptCount}/{recording.maxAttempts}
                  </small>
                ) : null}
                {recording?.lastError ? <ErrorBanner message={recording.lastError} /> : null}
              </TabsContent>
            </Tabs>
            </div>
          </div>
        ) : null}
      </SidePanel>
    </section>
  );
}

function toLocalInput(value: Date) {
  const offset = value.getTimezoneOffset();
  return new Date(value.getTime() - offset * 60000).toISOString().slice(0, 16);
}
function formatDate(value: string) {
  return new Date(value).toLocaleString();
}
function readError(exception: unknown, fallback: string) {
  return exception instanceof ApiError ? exception.message : fallback;
}
