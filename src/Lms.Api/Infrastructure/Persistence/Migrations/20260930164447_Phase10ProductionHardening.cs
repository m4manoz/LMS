using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lms.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase10ProductionHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HealthStatus",
                table: "virtual_labs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastHealthCheckUtc",
                table: "virtual_labs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastHealthError",
                table: "virtual_labs",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "gamification_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CourseCompletionPoints = table.Column<int>(type: "integer", nullable: false),
                    DailyPointCap = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gamification_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "recommendation_dismissals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    LearnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Variant = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DismissedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendation_dismissals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "virtual_lab_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    VirtualLabId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalAttemptId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ScorePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    Completed = table.Column<bool>(type: "boolean", nullable: false),
                    PayloadJson = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_virtual_lab_results", x => x.Id);
                    table.ForeignKey(
                        name: "FK_virtual_lab_results_virtual_labs_VirtualLabId",
                        column: x => x.VirtualLabId,
                        principalTable: "virtual_labs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_gamification_settings_TenantId",
                table: "gamification_settings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_dismissals_TenantId_LearnerUserId_CourseId",
                table: "recommendation_dismissals",
                columns: new[] { "TenantId", "LearnerUserId", "CourseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_virtual_lab_results_TenantId_VirtualLabId_UserId_ExternalAt~",
                table: "virtual_lab_results",
                columns: new[] { "TenantId", "VirtualLabId", "UserId", "ExternalAttemptId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_virtual_lab_results_VirtualLabId",
                table: "virtual_lab_results",
                column: "VirtualLabId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gamification_settings");

            migrationBuilder.DropTable(
                name: "recommendation_dismissals");

            migrationBuilder.DropTable(
                name: "virtual_lab_results");

            migrationBuilder.DropColumn(
                name: "HealthStatus",
                table: "virtual_labs");

            migrationBuilder.DropColumn(
                name: "LastHealthCheckUtc",
                table: "virtual_labs");

            migrationBuilder.DropColumn(
                name: "LastHealthError",
                table: "virtual_labs");
        }
    }
}
