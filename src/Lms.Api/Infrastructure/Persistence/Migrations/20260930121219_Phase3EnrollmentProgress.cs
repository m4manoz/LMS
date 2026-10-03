using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lms.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase3EnrollmentProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "enrollments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    LearnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StartDateAd = table.Column<DateOnly>(type: "date", nullable: true),
                    EndDateAd = table.Column<DateOnly>(type: "date", nullable: true),
                    ProgressPercent = table.Column<int>(type: "integer", nullable: false),
                    CurrentLessonId = table.Column<Guid>(type: "uuid", nullable: true),
                    EnrolledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAccessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enrollments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_enrollments_courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "course_bookmarks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnrollmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    LearnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LessonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PositionSeconds = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_course_bookmarks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_course_bookmarks_course_lessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "course_lessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_course_bookmarks_enrollments_EnrollmentId",
                        column: x => x.EnrollmentId,
                        principalTable: "enrollments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "learner_notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnrollmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    LearnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LessonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learner_notes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_learner_notes_course_lessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "course_lessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_learner_notes_enrollments_EnrollmentId",
                        column: x => x.EnrollmentId,
                        principalTable: "enrollments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "learning_progress_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnrollmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    LearnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LessonId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PositionSeconds = table.Column<int>(type: "integer", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_learning_progress_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_learning_progress_events_enrollments_EnrollmentId",
                        column: x => x.EnrollmentId,
                        principalTable: "enrollments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lesson_progress",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnrollmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    LearnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LessonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PositionSeconds = table.Column<int>(type: "integer", nullable: false),
                    LastViewedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lesson_progress", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lesson_progress_course_lessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "course_lessons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_lesson_progress_enrollments_EnrollmentId",
                        column: x => x.EnrollmentId,
                        principalTable: "enrollments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_course_bookmarks_EnrollmentId",
                table: "course_bookmarks",
                column: "EnrollmentId");

            migrationBuilder.CreateIndex(
                name: "IX_course_bookmarks_LessonId",
                table: "course_bookmarks",
                column: "LessonId");

            migrationBuilder.CreateIndex(
                name: "IX_course_bookmarks_TenantId_EnrollmentId_LessonId",
                table: "course_bookmarks",
                columns: new[] { "TenantId", "EnrollmentId", "LessonId" });

            migrationBuilder.CreateIndex(
                name: "IX_enrollments_CourseId",
                table: "enrollments",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_enrollments_TenantId_CourseId_LearnerUserId",
                table: "enrollments",
                columns: new[] { "TenantId", "CourseId", "LearnerUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_enrollments_TenantId_LearnerUserId_Status",
                table: "enrollments",
                columns: new[] { "TenantId", "LearnerUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_learner_notes_EnrollmentId",
                table: "learner_notes",
                column: "EnrollmentId");

            migrationBuilder.CreateIndex(
                name: "IX_learner_notes_LessonId",
                table: "learner_notes",
                column: "LessonId");

            migrationBuilder.CreateIndex(
                name: "IX_learner_notes_TenantId_EnrollmentId_LessonId",
                table: "learner_notes",
                columns: new[] { "TenantId", "EnrollmentId", "LessonId" });

            migrationBuilder.CreateIndex(
                name: "IX_learning_progress_events_EnrollmentId",
                table: "learning_progress_events",
                column: "EnrollmentId");

            migrationBuilder.CreateIndex(
                name: "IX_learning_progress_events_TenantId_EnrollmentId_IdempotencyK~",
                table: "learning_progress_events",
                columns: new[] { "TenantId", "EnrollmentId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_learning_progress_events_TenantId_EnrollmentId_OccurredAtUtc",
                table: "learning_progress_events",
                columns: new[] { "TenantId", "EnrollmentId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_lesson_progress_EnrollmentId",
                table: "lesson_progress",
                column: "EnrollmentId");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_progress_LessonId",
                table: "lesson_progress",
                column: "LessonId");

            migrationBuilder.CreateIndex(
                name: "IX_lesson_progress_TenantId_EnrollmentId_LessonId",
                table: "lesson_progress",
                columns: new[] { "TenantId", "EnrollmentId", "LessonId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lesson_progress_TenantId_LearnerUserId_CourseId",
                table: "lesson_progress",
                columns: new[] { "TenantId", "LearnerUserId", "CourseId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "course_bookmarks");

            migrationBuilder.DropTable(
                name: "learner_notes");

            migrationBuilder.DropTable(
                name: "learning_progress_events");

            migrationBuilder.DropTable(
                name: "lesson_progress");

            migrationBuilder.DropTable(
                name: "enrollments");
        }
    }
}
