using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lms.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase7LiveClassesSecurityRbac : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "live_class_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: true),
                    HostUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProviderMeetingId = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    JoinUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    HostUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    StartAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_class_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_live_class_sessions_courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "security_audit_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    DetailsJson = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_audit_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "live_polls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Question = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    OptionsJson = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_polls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_live_polls_live_class_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "live_class_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session_announcements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    IsPinned = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_announcements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_session_announcements_live_class_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "live_class_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session_attendance",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    JoinedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeftAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_attendance", x => x.Id);
                    table.ForeignKey(
                        name: "FK_session_attendance_live_class_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "live_class_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session_chat_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_chat_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_session_chat_messages_live_class_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "live_class_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "session_hand_raises",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RaisedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LoweredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_session_hand_raises", x => x.Id);
                    table.ForeignKey(
                        name: "FK_session_hand_raises_live_class_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "live_class_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "live_poll_responses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PollId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OptionIndex = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_poll_responses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_live_poll_responses_live_class_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "live_class_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_live_poll_responses_live_polls_PollId",
                        column: x => x.PollId,
                        principalTable: "live_polls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_live_class_sessions_CourseId",
                table: "live_class_sessions",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_live_class_sessions_TenantId_CourseId",
                table: "live_class_sessions",
                columns: new[] { "TenantId", "CourseId" });

            migrationBuilder.CreateIndex(
                name: "IX_live_class_sessions_TenantId_StartAtUtc_Status",
                table: "live_class_sessions",
                columns: new[] { "TenantId", "StartAtUtc", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_live_poll_responses_PollId",
                table: "live_poll_responses",
                column: "PollId");

            migrationBuilder.CreateIndex(
                name: "IX_live_poll_responses_SessionId",
                table: "live_poll_responses",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_live_poll_responses_TenantId_PollId_UserId",
                table: "live_poll_responses",
                columns: new[] { "TenantId", "PollId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_live_polls_SessionId",
                table: "live_polls",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_live_polls_TenantId_SessionId_CreatedAtUtc",
                table: "live_polls",
                columns: new[] { "TenantId", "SessionId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_security_audit_events_TenantId_CreatedAtUtc",
                table: "security_audit_events",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_announcements_SessionId",
                table: "session_announcements",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_session_announcements_TenantId_SessionId_CreatedAtUtc",
                table: "session_announcements",
                columns: new[] { "TenantId", "SessionId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_attendance_SessionId",
                table: "session_attendance",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_session_attendance_TenantId_SessionId_UserId",
                table: "session_attendance",
                columns: new[] { "TenantId", "SessionId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_session_chat_messages_SessionId",
                table: "session_chat_messages",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_session_chat_messages_TenantId_SessionId_CreatedAtUtc",
                table: "session_chat_messages",
                columns: new[] { "TenantId", "SessionId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_session_hand_raises_SessionId",
                table: "session_hand_raises",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_session_hand_raises_TenantId_SessionId_UserId",
                table: "session_hand_raises",
                columns: new[] { "TenantId", "SessionId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "live_poll_responses");

            migrationBuilder.DropTable(
                name: "security_audit_events");

            migrationBuilder.DropTable(
                name: "session_announcements");

            migrationBuilder.DropTable(
                name: "session_attendance");

            migrationBuilder.DropTable(
                name: "session_chat_messages");

            migrationBuilder.DropTable(
                name: "session_hand_raises");

            migrationBuilder.DropTable(
                name: "live_polls");

            migrationBuilder.DropTable(
                name: "live_class_sessions");
        }
    }
}
