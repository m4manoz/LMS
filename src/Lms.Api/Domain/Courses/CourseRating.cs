namespace Lms.Api.Domain.Courses;

/// <summary>
/// A learner's rating of a course: 1 to 5 stars and an optional written review. One per learner per course, changeable by them.
/// Staff can hide a rating that is abusive or spam; a hidden rating counts for nothing and is not shown publicly.
/// </summary>
public sealed class CourseRating
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseId { get; set; }
    public Guid LearnerUserId { get; set; }
    public int Stars { get; set; }
    public string? Review { get; set; }
    public bool IsHidden { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
