using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lms.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase8CoreHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tenant_memberships_TenantId_UserId",
                table: "tenant_memberships");

            migrationBuilder.AddColumn<bool>(
                name: "SubmittedAfterTimeLimit",
                table: "assessment_attempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_memberships_TenantId_UserId_RoleId",
                table: "tenant_memberships",
                columns: new[] { "TenantId", "UserId", "RoleId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tenant_memberships_TenantId_UserId_RoleId",
                table: "tenant_memberships");

            migrationBuilder.DropColumn(
                name: "SubmittedAfterTimeLimit",
                table: "assessment_attempts");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_memberships_TenantId_UserId",
                table: "tenant_memberships",
                columns: new[] { "TenantId", "UserId" },
                unique: true);
        }
    }
}
