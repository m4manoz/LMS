namespace Lms.Api.Domain.LiveClasses;

public enum LiveSessionStatus
{
    Scheduled = 1,
    Live = 2,
    Completed = 3,
    Cancelled = 4
}

public enum AttendanceStatus
{
    Present = 1,
    Left = 2,
    Excused = 3
}

public enum HandRaiseStatus
{
    Raised = 1,
    Lowered = 2
}

public enum RecordingStatus
{
    Requested = 1,
    Processing = 2,
    Available = 3,
    Failed = 4,
    Expired = 5,
    /// <summary>The class is being recorded right now (LiveKit).</summary>
    Recording = 6
}

public sealed class LiveClassSession
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? CourseId { get; set; }
    public Guid HostUserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderMeetingId { get; set; } = string.Empty;
    public string JoinUrl { get; set; } = string.Empty;
    public string HostUrl { get; set; } = string.Empty;
    public DateTimeOffset StartAtUtc { get; set; }
    public DateTimeOffset EndAtUtc { get; set; }
    public LiveSessionStatus Status { get; set; }
    /// <summary>A waiting room: people who are not the host or staff wait until the host lets them in.</summary>
    public bool RequireApproval { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public enum JoinRequestStatus
{
    Waiting = 1,
    Admitted = 2,
    Declined = 3
}

/// <summary>One person's request to enter a class that has a waiting room, and what the host decided.</summary>
public sealed class SessionJoinRequest
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public JoinRequestStatus Status { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
    public Guid? DecidedByUserId { get; set; }
}

public sealed class SessionAttendance
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public AttendanceStatus Status { get; set; }
    public DateTimeOffset JoinedAtUtc { get; set; }
    public DateTimeOffset? LeftAtUtc { get; set; }
    public int DurationSeconds { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class SessionAnnouncement
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SessionId { get; set; }
    public Guid AuthorUserId { get; set; }
    public string Body { get; set; } = string.Empty;
    public bool IsPinned { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class LivePoll
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SessionId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string Question { get; set; } = string.Empty;
    public string OptionsJson { get; set; } = "[]";
    public bool IsOpen { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
}

public sealed class LivePollResponse
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PollId { get; set; }
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public int OptionIndex { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class SessionHandRaise
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public HandRaiseStatus Status { get; set; }
    public DateTimeOffset RaisedAtUtc { get; set; }
    public DateTimeOffset? LoweredAtUtc { get; set; }
}

public sealed class SessionChatMessage
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

public sealed class SessionRecording
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SessionId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderRecordingId { get; set; } = string.Empty;
    public string? RecordingUrl { get; set; }
    /// <summary>Where the recording service was told to put the file: a file name in the shared folder, or the storage key in the bucket.</summary>
    public string? OutputKey { get; set; }
    /// <summary>The library video made from the recording, once it is ready.</summary>
    public Guid? VideoId { get; set; }
    public RecordingStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public string? LastError { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? AvailableAtUtc { get; set; }
    public DateTimeOffset? RetainUntilUtc { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
}

public sealed class SessionConsent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SessionId { get; set; }
    public Guid UserId { get; set; }
    public string ConsentType { get; set; } = "recording";
    public bool Granted { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public string? IpAddress { get; set; }
}
