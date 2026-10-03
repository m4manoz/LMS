namespace Lms.Api.Domain.Learning;

public sealed class Subject
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? ParentSubjectId { get; set; }
    public string? Description { get; set; }
    public int DisplayOrder { get; set; }
}
