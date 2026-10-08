using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lms.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LiveKitAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoRecord",
                table: "live_class_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "session_track_recordings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderRecordingId = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    OutputKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ContentAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_track_recordings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_session_track_recordings_live_class_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "live_class_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_session_track_recordings_SessionId",
                table: "session_track_recordings",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_session_track_recordings_Status",
                table: "session_track_recordings",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_session_track_recordings_TenantId_SessionId_UserId",
                table: "session_track_recordings",
                columns: new[] { "TenantId", "SessionId", "UserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "session_track_recordings");

            migrationBuilder.DropColumn(
                name: "AutoRecord",
                table: "live_class_sessions");
        }
    }
}
