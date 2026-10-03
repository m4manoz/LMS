namespace Lms.Api.Domain.Videos;

public enum VideoType
{
    /// <summary>A file someone uploaded.</summary>
    Uploaded = 1,
    /// <summary>A recording of a live class.</summary>
    LiveRecording = 2,
    /// <summary>A link to a video that lives elsewhere (YouTube, Vimeo, an institution's own site).</summary>
    External = 3
}

public enum VideoStatus
{
    Uploading = 1,
    /// <summary>Being converted to streaming segments and a poster image. Learners cannot see it until it is Ready.</summary>
    Processing = 2,
    Ready = 3,
    Failed = 4
}

/// <summary>A video in an organization's library. Always belongs to a course, which decides who may watch it.</summary>
public sealed class Video
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    /// <summary>The lesson it was last attached to, if any.</summary>
    public Guid? LessonId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public VideoType Type { get; set; }
    public VideoStatus Status { get; set; }
    public string? StatusMessage { get; set; }
    /// <summary>The stored file, for uploaded videos.</summary>
    public Guid? ContentAssetId { get; set; }
    /// <summary>The address, for external videos.</summary>
    public string? ExternalUrl { get; set; }
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public int? DurationSeconds { get; set; }
    /// <summary>How many HLS segments were made. Null means there is no streaming version and the original file is played.</summary>
    public int? HlsSegmentCount { get; set; }
    /// <summary>The qualities made, best first, as "720:12,360:12" (height:pieces). Empty for videos converted before there were several qualities.</summary>
    public string? HlsLayout { get; set; }
    /// <summary>Whether a poster image (poster.jpg) was made.</summary>
    public bool HasPoster { get; set; }
    public int ProcessingAttempts { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>How far one person has got in one video. Drives "resume", progress chips and the analytics.</summary>
public sealed class VideoWatch
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VideoId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Where they last were, so playback can resume there.</summary>
    public int LastPositionSeconds { get; set; }
    /// <summary>The furthest point reached.</summary>
    public int MaxPositionSeconds { get; set; }
    /// <summary>Seconds actually played, which can exceed the length when sections are replayed.</summary>
    public int WatchedSeconds { get; set; }
    public int Plays { get; set; }
    public bool Completed { get; set; }
    public DateTimeOffset FirstWatchedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
