using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lms.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MessagingForumsDepth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EditedAtUtc",
                table: "forum_threads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EditedAtUtc",
                table: "forum_replies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttachmentContentType",
                table: "conversation_messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttachmentKey",
                table: "conversation_messages",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttachmentName",
                table: "conversation_messages",
                type: "character varying(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AttachmentSizeBytes",
                table: "conversation_messages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAtUtc",
                table: "conversation_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EditedAtUtc",
                table: "conversation_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "forum_attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReplyId = table.Column<Guid>(type: "uuid", nullable: true),
                    StorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_forum_attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_forum_attachments_forum_threads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "forum_threads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "forum_edits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ThreadId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReplyId = table.Column<Guid>(type: "uuid", nullable: true),
                    PreviousTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PreviousBody = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    EditedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EditedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_forum_edits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_forum_edits_forum_threads_ThreadId",
                        column: x => x.ThreadId,
                        principalTable: "forum_threads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_forum_attachments_TenantId_ThreadId_ReplyId",
                table: "forum_attachments",
                columns: new[] { "TenantId", "ThreadId", "ReplyId" });

            migrationBuilder.CreateIndex(
                name: "IX_forum_attachments_ThreadId",
                table: "forum_attachments",
                column: "ThreadId");

            migrationBuilder.CreateIndex(
                name: "IX_forum_edits_TenantId_ThreadId_ReplyId_EditedAtUtc",
                table: "forum_edits",
                columns: new[] { "TenantId", "ThreadId", "ReplyId", "EditedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_forum_edits_ThreadId",
                table: "forum_edits",
                column: "ThreadId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "forum_attachments");

            migrationBuilder.DropTable(
                name: "forum_edits");

            migrationBuilder.DropColumn(
                name: "EditedAtUtc",
                table: "forum_threads");

            migrationBuilder.DropColumn(
                name: "EditedAtUtc",
                table: "forum_replies");

            migrationBuilder.DropColumn(
                name: "AttachmentContentType",
                table: "conversation_messages");

            migrationBuilder.DropColumn(
                name: "AttachmentKey",
                table: "conversation_messages");

            migrationBuilder.DropColumn(
                name: "AttachmentName",
                table: "conversation_messages");

            migrationBuilder.DropColumn(
                name: "AttachmentSizeBytes",
                table: "conversation_messages");

            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "conversation_messages");

            migrationBuilder.DropColumn(
                name: "EditedAtUtc",
                table: "conversation_messages");
        }
    }
}
