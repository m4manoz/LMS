using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lms.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase4AssessmentsGrading : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "question_banks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_question_banks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_question_banks_courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assessments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionBankId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    Instructions = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TimeLimitMinutes = table.Column<int>(type: "integer", nullable: true),
                    AttemptLimit = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assessments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assessments_course_versions_CourseVersionId",
                        column: x => x.CourseVersionId,
                        principalTable: "course_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assessments_courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_assessments_question_banks_QuestionBankId",
                        column: x => x.QuestionBankId,
                        principalTable: "question_banks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "questions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionBankId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Prompt = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    OptionsJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    CorrectAnswerJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    Points = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_questions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_questions_question_banks_QuestionBankId",
                        column: x => x.QuestionBankId,
                        principalTable: "question_banks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assessment_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    LearnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ScorePoints = table.Column<int>(type: "integer", nullable: false),
                    PossiblePoints = table.Column<int>(type: "integer", nullable: false),
                    Percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    TeacherFeedback = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GradedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assessment_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assessment_attempts_assessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_assessment_attempts_courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assessment_questions",
                columns: table => new
                {
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    Points = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assessment_questions", x => new { x.AssessmentId, x.QuestionId });
                    table.ForeignKey(
                        name: "FK_assessment_questions_assessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_assessment_questions_questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assessment_answers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnswerJson = table.Column<string>(type: "character varying(30000)", maxLength: 30000, nullable: false),
                    ScorePoints = table.Column<int>(type: "integer", nullable: false),
                    IsCorrect = table.Column<bool>(type: "boolean", nullable: true),
                    Feedback = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AnsweredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assessment_answers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_assessment_answers_assessment_attempts_AttemptId",
                        column: x => x.AttemptId,
                        principalTable: "assessment_attempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_assessment_answers_questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assessment_answers_AttemptId",
                table: "assessment_answers",
                column: "AttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_assessment_answers_QuestionId",
                table: "assessment_answers",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_assessment_answers_TenantId_AttemptId_QuestionId",
                table: "assessment_answers",
                columns: new[] { "TenantId", "AttemptId", "QuestionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assessment_attempts_AssessmentId",
                table: "assessment_attempts",
                column: "AssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_assessment_attempts_CourseId",
                table: "assessment_attempts",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_assessment_attempts_TenantId_AssessmentId_LearnerUserId_Att~",
                table: "assessment_attempts",
                columns: new[] { "TenantId", "AssessmentId", "LearnerUserId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assessment_attempts_TenantId_LearnerUserId_Status",
                table: "assessment_attempts",
                columns: new[] { "TenantId", "LearnerUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_assessment_questions_AssessmentId_DisplayOrder",
                table: "assessment_questions",
                columns: new[] { "AssessmentId", "DisplayOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_assessment_questions_QuestionId",
                table: "assessment_questions",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_assessments_CourseId",
                table: "assessments",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_assessments_CourseVersionId",
                table: "assessments",
                column: "CourseVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_assessments_QuestionBankId",
                table: "assessments",
                column: "QuestionBankId");

            migrationBuilder.CreateIndex(
                name: "IX_assessments_TenantId_CourseId_Status",
                table: "assessments",
                columns: new[] { "TenantId", "CourseId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_question_banks_CourseId",
                table: "question_banks",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_question_banks_TenantId_CourseId_Name",
                table: "question_banks",
                columns: new[] { "TenantId", "CourseId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_questions_QuestionBankId",
                table: "questions",
                column: "QuestionBankId");

            migrationBuilder.CreateIndex(
                name: "IX_questions_TenantId_QuestionBankId",
                table: "questions",
                columns: new[] { "TenantId", "QuestionBankId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assessment_answers");

            migrationBuilder.DropTable(
                name: "assessment_questions");

            migrationBuilder.DropTable(
                name: "assessment_attempts");

            migrationBuilder.DropTable(
                name: "questions");

            migrationBuilder.DropTable(
                name: "assessments");

            migrationBuilder.DropTable(
                name: "question_banks");
        }
    }
}
