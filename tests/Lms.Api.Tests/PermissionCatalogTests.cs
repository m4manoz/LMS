using System.Net;
using System.Net.Http.Json;
using Lms.Api.Domain.Identity;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Permissions by module: the list must name every permission once, and the role screens read it from the API.</summary>
public sealed class PermissionCatalogTests(LmsApiFactory factory) : IClassFixture<LmsApiFactory>
{
    private readonly TestWorld world = new(factory);

    [Fact]
    public void Every_permission_is_in_the_catalog_exactly_once_and_nothing_else_is()
    {
        var codes = PermissionCatalog.All.Select(item => item.Code).ToArray();
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.Empty(LmsPermissions.All.Except(codes));
        Assert.Empty(codes.Except(LmsPermissions.All));
    }

    [Fact]
    public void Every_entry_has_a_name_a_description_and_a_module_whose_title_and_order_agree()
    {
        Assert.All(PermissionCatalog.All, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Label), item.Code);
            Assert.False(string.IsNullOrWhiteSpace(item.Description), item.Code);
            Assert.False(string.IsNullOrWhiteSpace(item.Module), item.Code);
        });
        foreach (var module in PermissionCatalog.All.GroupBy(item => item.Module))
        {
            Assert.Single(module.Select(item => item.ModuleTitle).Distinct());
            Assert.Single(module.Select(item => item.ModuleOrder).Distinct());
        }
        var orders = PermissionCatalog.All.GroupBy(item => item.Module).Select(group => group.First().ModuleOrder).ToArray();
        Assert.Equal(orders.Length, orders.Distinct().Count());                                             // each module has its own place in the list
    }

    [Fact]
    public void A_permission_is_filed_under_the_module_its_name_belongs_to()
    {
        string ModuleOf(string code) => PermissionCatalog.All.Single(item => item.Code == code).Module;
        Assert.Equal("courses", ModuleOf(LmsPermissions.CoursePublish));
        Assert.Equal("live", ModuleOf(LmsPermissions.AttendanceRead));
        Assert.Equal("assessments", ModuleOf(LmsPermissions.GradeManage));
        Assert.Equal("community", ModuleOf(LmsPermissions.ForumModerate));
        Assert.Equal("organization", ModuleOf(LmsPermissions.RoleManage));
    }

    [Fact]
    public async Task The_catalog_endpoint_returns_the_modules_in_order_with_every_permission()
    {
        var t = await world.NewTenantAsync();
        var response = await t.Admin.GetAsync("/api/v1/tenant/security/permission-catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var modules = (await ReadAsync(response)).EnumerateArray().ToList();
        Assert.Equal(13, modules.Count);
        Assert.Equal("organization", modules[0].GetProperty("module").GetString());
        Assert.Equal("Organization and access", modules[0].GetProperty("title").GetString());
        var codes = modules.SelectMany(module => module.GetProperty("permissions").EnumerateArray()).Select(item => item.GetProperty("code").GetString()!).ToArray();
        Assert.Equal(LmsPermissions.All.OrderBy(item => item), codes.OrderBy(item => item));
        var first = modules[2].GetProperty("permissions").EnumerateArray().First();
        Assert.Equal("course.read", first.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("description").GetString()));
    }

    [Fact]
    public async Task Only_people_who_may_read_roles_get_it()
    {
        var t = await world.NewTenantAsync();
        var ada = await world.AddLearnerAsync(t, "Ada");
        Assert.Equal(HttpStatusCode.Forbidden, (await ada.Client.GetAsync("/api/v1/tenant/security/permission-catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateTenantClient(t.Slug).GetAsync("/api/v1/tenant/security/permission-catalog")).StatusCode);
    }

    [Fact]
    public async Task A_custom_role_can_be_changed_and_a_system_role_cannot()
    {
        var t = await world.NewTenantAsync();
        var created = await ReadAsync(await t.Admin.PostAsJsonAsync("/api/v1/tenant/roles", new { code = "AUDITOR", name = "Auditor", permissions = new[] { "course.read" } }));
        var id = created.GetProperty("id").GetGuid();
        var changed = await t.Admin.PutAsJsonAsync($"/api/v1/tenant/roles/{id}", new { code = "AUDITOR", name = "Senior auditor", permissions = new[] { "course.read", "report.read" } });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var body = await ReadAsync(changed);
        Assert.Equal("Senior auditor", body.GetProperty("name").GetString());
        Assert.Equal(["course.read", "report.read"], body.GetProperty("permissions").EnumerateArray().Select(item => item.GetString()).ToArray());

        var roles = (await ReadAsync(await t.Admin.GetAsync("/api/v1/tenant/roles"))).EnumerateArray().ToList();
        var admin = roles.Single(item => item.GetProperty("code").GetString() == "TENANT_ADMIN");
        Assert.Equal(LmsPermissions.All.Length, admin.GetProperty("permissions").GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict, (await t.Admin.PutAsJsonAsync($"/api/v1/tenant/roles/{admin.GetProperty("id").GetGuid()}", new { code = "TENANT_ADMIN", name = "x", permissions = new[] { "course.read" } })).StatusCode);
    }
}
