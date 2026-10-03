namespace Lms.Api.Domain.Courses;

public enum BlockType
{
    Text = 1,
    Code = 2,
    Link = 3,
    /// <summary>A video or document shown inline from an allow-listed https host.</summary>
    Embed = 4,
    Image = 5,
    Pdf = 6,
    Video = 7,
    Audio = 8,
    /// <summary>Any file offered as a download; never rendered inline.</summary>
    Download = 9
}

/// <summary>One piece of lesson content. Lessons are an ordered list of blocks.</summary>
public sealed class LessonBlock
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CourseLessonId { get; set; }
    public BlockType Type { get; set; }
    public int DisplayOrder { get; set; }
    public string? Title { get; set; }
    /// <summary>Text body (Text) or source code (Code). Plain text; never interpreted as HTML.</summary>
    public string? Text { get; set; }
    public string? Language { get; set; }
    /// <summary>Target of a Link or Embed block.</summary>
    public string? Url { get; set; }
    public string? Caption { get; set; }
    /// <summary>The uploaded file behind Image, Pdf, Video, Audio and Download blocks.</summary>
    public Guid? ContentAssetId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
