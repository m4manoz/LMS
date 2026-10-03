using System.Net.Http.Json;
using System.Text.Json;
using Lms.Api.Infrastructure.Persistence;
using Lms.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lms.Api.Tests;

/// <summary>Builds tenants, people and courses for scenario tests so each test reads as the story it tells.</summary>
public sealed class TestWorld(LmsApiFactory factory)
{
    public sealed record Person(string Name, string Email, Guid Id, HttpClient Client);
    public sealed record CourseInfo(Guid Id, string Code, string Title, IReadOnlyList<Guid> ModuleIds, IReadOnlyList<IReadOnlyList<Guid>> LessonIds);
    public sealed record Tenant(string Slug, HttpClient Admin, Guid AdminId, string AdminEmail);

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public async Task<Tenant> NewTenantAsync()
    {
        var (slug, email, token) = await factory.ProvisionTenantWithAdminAsync();
        var admin = factory.CreateTenantClient(slug, token);
        var me = await ReadAsync(await admin.GetAsync("/api/v1/tenant/me"));
        return new Tenant(slug, admin, Guid.Parse(me.GetProperty("userId").GetString()!), email);
    }

    public Task<Person> AddLearnerAsync(Tenant tenant, string name) => AddPersonAsync(tenant, name, "LEARNER");

    /// <summary>Creates a signed-in person with any system role (LEARNER, TEACHER, GUARDIAN ...).</summary>
    public async Task<Person> AddPersonAsync(Tenant tenant, string name, string roleCode)
    {
        var email = $"{name.ToLowerInvariant()}@{tenant.Slug}.test";
        (await tenant.Admin.PostAsJsonAsync("/api/v1/tenant/users", new { email, displayName = name, password = LmsApiFactory.AdminPassword, roleCode })).EnsureSuccessStatusCode();
        var client = factory.CreateTenantClient(tenant.Slug, await factory.LoginAsync(tenant.Slug, email, LmsApiFactory.AdminPassword));
        var me = await ReadAsync(await client.GetAsync("/api/v1/tenant/me"));
        return new Person(name, email, Guid.Parse(me.GetProperty("userId").GetString()!), client);
    }

    /// <summary>Creates a course with the given number of modules and lessons per module, optionally published.</summary>
    public async Task<CourseInfo> NewCourseAsync(Tenant tenant, string code, int? capacity = null, int modules = 1, int lessonsPerModule = 1, bool publish = true)
    {
        var created = await ReadAsync(await tenant.Admin.PostAsJsonAsync("/api/v1/tenant/courses", new { code, title = $"Course {code}", capacity }));
        var courseId = created.GetProperty("course").GetProperty("id").GetGuid();
        var moduleIds = new List<Guid>();
        var lessonIds = new List<IReadOnlyList<Guid>>();
        for (var m = 1; m <= modules; m++)
        {
            var module = await ReadAsync(await tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{courseId}/modules", new { title = $"Module {m}" }));
            var moduleId = module.GetProperty("id").GetGuid();
            moduleIds.Add(moduleId);
            var lessons = new List<Guid>();
            for (var l = 1; l <= lessonsPerModule; l++)
            {
                var lesson = await ReadAsync(await tenant.Admin.PostAsJsonAsync($"/api/v1/tenant/courses/{courseId}/modules/{moduleId}/lessons", new { title = $"Lesson {m}.{l}", contentHtml = $"Body {m}.{l}" }));
                lessons.Add(lesson.GetProperty("id").GetGuid());
            }
            lessonIds.Add(lessons);
        }
        if (publish)
        {
            (await tenant.Admin.PostAsync($"/api/v1/tenant/courses/{courseId}/submit-review", null)).EnsureSuccessStatusCode();
            (await tenant.Admin.PostAsync($"/api/v1/tenant/courses/{courseId}/publish", null)).EnsureSuccessStatusCode();
        }
        return new CourseInfo(courseId, code, $"Course {code}", moduleIds, lessonIds);
    }

    public static Task<HttpResponseMessage> EnrollAsync(Person person, CourseInfo course) => person.Client.PostAsync($"/api/v1/tenant/courses/{course.Id}/enroll", null);

    public static async Task CompleteLessonAsync(Person person, CourseInfo course, Guid lessonId)
    {
        var response = await person.Client.PostAsJsonAsync($"/api/v1/tenant/courses/{course.Id}/learning/lessons/{lessonId}/progress", new { status = "Completed", idempotencyKey = Guid.NewGuid().ToString() });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Finishes every lesson, which completes the course for that learner.</summary>
    public static async Task CompleteCourseAsync(Person person, CourseInfo course)
    {
        foreach (var lessonId in course.LessonIds.SelectMany(item => item)) await CompleteLessonAsync(person, course, lessonId);
    }

    public static async Task<string> EnrollmentStatusAsync(Person person, CourseInfo course)
    {
        var list = await ReadAsync(await person.Client.GetAsync("/api/v1/tenant/enrollments"));
        var match = list.EnumerateArray().FirstOrDefault(item => item.GetProperty("courseId").GetGuid() == course.Id);
        return match.ValueKind == JsonValueKind.Undefined ? "None" : match.GetProperty("status").GetString()!;
    }

    public static async Task<JsonElement> InboxAsync(Person person, string? template = null)
    {
        var all = await ReadAsync(await person.Client.GetAsync("/api/v1/tenant/notifications"));
        return template is null ? all : JsonSerializer.SerializeToElement(all.EnumerateArray().Where(item => item.GetProperty("templateCode").GetString() == template).ToList());
    }

    /// <summary>Runs code against the database inside the given tenant, for setting up states that need the clock (like "enrolled three days ago").</summary>
    public async Task WithDbAsync(string slug, Func<LmsDbContext, Task> action)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LmsDbContext>();
        var tenant = db.Tenants.IgnoreQueryFilters().Single(item => item.Slug == slug);
        ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Set(tenant.Id, tenant.Slug);
        await action(db);
    }
}
