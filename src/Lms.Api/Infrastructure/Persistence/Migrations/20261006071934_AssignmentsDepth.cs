using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lms.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssignmentsDepth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsGroup",
                table: "assignments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "RubricId",
                table: "assignments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "assignment_submissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RubricScoresJson",
                table: "assignment_submissions",
                type: "character varying(30000)",
                maxLength: 30000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "assignment_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignment_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assignment_groups_assignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assignment_group_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    LearnerUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignment_group_members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assignment_group_members_assignment_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "assignment_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assignments_RubricId",
                table: "assignments",
                column: "RubricId");

            migrationBuilder.CreateIndex(
                name: "IX_assignment_submissions_TenantId_GroupId",
                table: "assignment_submissions",
                columns: new[] { "TenantId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_assignment_group_members_GroupId",
                table: "assignment_group_members",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_assignment_group_members_TenantId_AssignmentId_LearnerUserId",
                table: "assignment_group_members",
                columns: new[] { "TenantId", "AssignmentId", "LearnerUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assignment_groups_AssignmentId",
                table: "assignment_groups",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_assignment_groups_TenantId_AssignmentId_Name",
                table: "assignment_groups",
                columns: new[] { "TenantId", "AssignmentId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_assignments_rubrics_RubricId",
                table: "assignments",
                column: "RubricId",
                principalTable: "rubrics",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_assignments_rubrics_RubricId",
                table: "assignments");

            migrationBuilder.DropTable(
                name: "assignment_group_members");

            migrationBuilder.DropTable(
                name: "assignment_groups");

            migrationBuilder.DropIndex(
                name: "IX_assignments_RubricId",
                table: "assignments");

            migrationBuilder.DropIndex(
                name: "IX_assignment_submissions_TenantId_GroupId",
                table: "assignment_submissions");

            migrationBuilder.DropColumn(
                name: "IsGroup",
                table: "assignments");

            migrationBuilder.DropColumn(
                name: "RubricId",
                table: "assignments");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "assignment_submissions");

            migrationBuilder.DropColumn(
                name: "RubricScoresJson",
                table: "assignment_submissions");
        }
    }
}
