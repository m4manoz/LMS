namespace Lms.Api.Domain.Videos;

public static class VideoAiProviders
{
    /// <summary>Nothing leaves this system: summaries and questions are picked from the transcript itself. Transcripts are added by hand.</summary>
    public const string Local = "Local";
    /// <summary>Any service that speaks the OpenAI API (OpenAI, Groq, Azure OpenAI-style gateways, or a self-hosted Whisper/LLM server).</summary>
    public const string OpenAiCompatible = "OpenAiCompatible";

    public static readonly string[] All = [Local, OpenAiCompatible];
    public const string DefaultBaseUrl = "https://api.openai.com/v1";
    public const string DefaultTranscriptionModel = "whisper-1";
    public const string DefaultChatModel = "gpt-4o-mini";
}

/// <summary>Which service listens to an organization's videos and writes about them. One row per organization; no row means the local provider.</summary>
public sealed class VideoAiSettings
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Provider { get; set; } = VideoAiProviders.Local;
    public string? BaseUrl { get; set; }
    /// <summary>Data Protection payload of the API key. Never returned by the API.</summary>
    public string? ApiKeyProtected { get; set; }
    public string? TranscriptionModel { get; set; }
    public string? ChatModel { get; set; }
    /// <summary>Make a transcript for every uploaded video once it has been converted.</summary>
    public bool AutoTranscribe { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public enum TranscriptStatus
{
    Queued = 1,
    Processing = 2,
    Ready = 3,
    Failed = 4
}

/// <summary>The words spoken in a video. One per video; its lines are <see cref="VideoTranscriptSegment"/>s.</summary>
public sealed class VideoTranscript
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VideoId { get; set; }
    public TranscriptStatus Status { get; set; }
    /// <summary>"Manual" (pasted WebVTT or SRT) or "Generated" (speech-to-text).</summary>
    public string Source { get; set; } = "Manual";
    public string? Language { get; set; }
    public string? Provider { get; set; }
    public string? StatusMessage { get; set; }
    public int Attempts { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>One spoken line with the time it starts and ends. Search results point at these.</summary>
public sealed class VideoTranscriptSegment
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VideoId { get; set; }
    public int Index { get; set; }
    public int StartMs { get; set; }
    public int EndMs { get; set; }
    public string Text { get; set; } = string.Empty;
}

public enum VideoInsightKind
{
    Summary = 1,
    Questions = 2
}

/// <summary>A summary or a set of practice questions written from a transcript. A draft until staff publish it to learners.</summary>
public sealed class VideoInsight
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VideoId { get; set; }
    public VideoInsightKind Kind { get; set; }
    /// <summary>The summary text, or for questions a JSON array of <c>PracticeQuestion</c>.</summary>
    public string Content { get; set; } = string.Empty;
    public bool Published { get; set; }
    /// <summary>The graded quiz made from this set of questions, if one was.</summary>
    public Guid? QuizAssessmentId { get; set; }
    public string Provider { get; set; } = VideoAiProviders.Local;
    public string? Model { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
