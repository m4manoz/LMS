using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lms.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GradebookV2WeightsAndScales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "course_grading_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    GradeScaleId = table.Column<Guid>(type: "uuid", nullable: true),
                    PassPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_course_grading_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "grade_categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    WeightPercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grade_categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "grade_item_categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grade_item_categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "grade_scales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    BandsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grade_scales", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_course_grading_settings_TenantId_CourseId",
                table: "course_grading_settings",
                columns: new[] { "TenantId", "CourseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_grade_categories_TenantId_CourseId_DisplayOrder",
                table: "grade_categories",
                columns: new[] { "TenantId", "CourseId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_grade_item_categories_TenantId_CourseId",
                table: "grade_item_categories",
                columns: new[] { "TenantId", "CourseId" });

            migrationBuilder.CreateIndex(
                name: "IX_grade_item_categories_TenantId_ItemKind_ItemId",
                table: "grade_item_categories",
                columns: new[] { "TenantId", "ItemKind", "ItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_grade_scales_TenantId_Name",
                table: "grade_scales",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "course_grading_settings");

            migrationBuilder.DropTable(
                name: "grade_categories");

            migrationBuilder.DropTable(
                name: "grade_item_categories");

            migrationBuilder.DropTable(
                name: "grade_scales");
        }
    }
}
