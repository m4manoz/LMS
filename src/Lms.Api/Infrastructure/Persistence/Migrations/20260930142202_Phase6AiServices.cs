using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lms.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase6AiServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: true),
                    Feature = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Instruction = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    OutputLanguage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Model = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    InputHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_jobs_courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ai_outputs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: true),
                    Feature = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Title = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    Content = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    StructuredJson = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: true),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    InputHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_outputs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_outputs_ai_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "ai_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ai_outputs_courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ai_citations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AiOutputId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceTitle = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    Locator = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: true),
                    Excerpt = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_citations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_citations_ai_outputs_AiOutputId",
                        column: x => x.AiOutputId,
                        principalTable: "ai_outputs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AiOutputId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_reviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_reviews_ai_outputs_AiOutputId",
                        column: x => x.AiOutputId,
                        principalTable: "ai_outputs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_citations_AiOutputId",
                table: "ai_citations",
                column: "AiOutputId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_citations_TenantId_AiOutputId",
                table: "ai_citations",
                columns: new[] { "TenantId", "AiOutputId" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_jobs_CourseId",
                table: "ai_jobs",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_jobs_TenantId_RequestedByUserId_CreatedAtUtc",
                table: "ai_jobs",
                columns: new[] { "TenantId", "RequestedByUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_jobs_TenantId_Status_NextAttemptAtUtc",
                table: "ai_jobs",
                columns: new[] { "TenantId", "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_outputs_CourseId",
                table: "ai_outputs",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_outputs_JobId",
                table: "ai_outputs",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_outputs_TenantId_JobId_CreatedAtUtc",
                table: "ai_outputs",
                columns: new[] { "TenantId", "JobId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_reviews_AiOutputId",
                table: "ai_reviews",
                column: "AiOutputId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_reviews_TenantId_AiOutputId_CreatedAtUtc",
                table: "ai_reviews",
                columns: new[] { "TenantId", "AiOutputId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_citations");

            migrationBuilder.DropTable(
                name: "ai_reviews");

            migrationBuilder.DropTable(
                name: "ai_outputs");

            migrationBuilder.DropTable(
                name: "ai_jobs");
        }
    }
}
