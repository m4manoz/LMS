using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lms.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssessmentDepth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_assessment_questions_AssessmentId_DisplayOrder",
                table: "assessment_questions");

            migrationBuilder.AddColumn<Guid>(
                name: "RubricId",
                table: "questions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CurrentVersion",
                table: "assessments",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "DraftVersion",
                table: "assessments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShuffleOptions",
                table: "assessments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShuffleQuestions",
                table: "assessments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PoolName",
                table: "assessment_questions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "assessment_questions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "PlanJson",
                table: "assessment_attempts",
                type: "character varying(60000)",
                maxLength: 60000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimeLimitMinutes",
                table: "assessment_attempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "assessment_attempts",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "RubricScoresJson",
                table: "assessment_answers",
                type: "character varying(30000)",
                maxLength: 30000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "assessment_pools",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DrawCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assessment_pools", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assessment_pools_assessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "learner_accommodations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    LearnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExtraTimePercent = table.Column<int>(type: "integer", nullable: false),
                    ExtraAttempts = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learner_accommodations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_learner_accommodations_courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rubrics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CriteriaJson = table.Column<string>(type: "character varying(30000)", maxLength: 30000, nullable: false),
                    TotalPoints = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rubrics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rubrics_courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_questions_RubricId",
                table: "questions",
                column: "RubricId");

            migrationBuilder.CreateIndex(
                name: "IX_assessment_questions_AssessmentId_Version_DisplayOrder",
                table: "assessment_questions",
                columns: new[] { "AssessmentId", "Version", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_assessment_pools_AssessmentId",
                table: "assessment_pools",
                column: "AssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_assessment_pools_TenantId_AssessmentId_Version_Name",
                table: "assessment_pools",
                columns: new[] { "TenantId", "AssessmentId", "Version", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_learner_accommodations_CourseId",
                table: "learner_accommodations",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_learner_accommodations_TenantId_CourseId_LearnerUserId",
                table: "learner_accommodations",
                columns: new[] { "TenantId", "CourseId", "LearnerUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rubrics_CourseId",
                table: "rubrics",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_rubrics_TenantId_CourseId_Name",
                table: "rubrics",
                columns: new[] { "TenantId", "CourseId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_questions_rubrics_RubricId",
                table: "questions",
                column: "RubricId",
                principalTable: "rubrics",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_questions_rubrics_RubricId",
                table: "questions");

            migrationBuilder.DropTable(
                name: "assessment_pools");

            migrationBuilder.DropTable(
                name: "learner_accommodations");

            migrationBuilder.DropTable(
                name: "rubrics");

            migrationBuilder.DropIndex(
                name: "IX_questions_RubricId",
                table: "questions");

            migrationBuilder.DropIndex(
                name: "IX_assessment_questions_AssessmentId_Version_DisplayOrder",
                table: "assessment_questions");

            migrationBuilder.DropColumn(
                name: "RubricId",
                table: "questions");

            migrationBuilder.DropColumn(
                name: "CurrentVersion",
                table: "assessments");

            migrationBuilder.DropColumn(
                name: "DraftVersion",
                table: "assessments");

            migrationBuilder.DropColumn(
                name: "ShuffleOptions",
                table: "assessments");

            migrationBuilder.DropColumn(
                name: "ShuffleQuestions",
                table: "assessments");

            migrationBuilder.DropColumn(
                name: "PoolName",
                table: "assessment_questions");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "assessment_questions");

            migrationBuilder.DropColumn(
                name: "PlanJson",
                table: "assessment_attempts");

            migrationBuilder.DropColumn(
                name: "TimeLimitMinutes",
                table: "assessment_attempts");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "assessment_attempts");

            migrationBuilder.DropColumn(
                name: "RubricScoresJson",
                table: "assessment_answers");

            migrationBuilder.CreateIndex(
                name: "IX_assessment_questions_AssessmentId_DisplayOrder",
                table: "assessment_questions",
                columns: new[] { "AssessmentId", "DisplayOrder" });
        }
    }
}
