using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lms.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationDedupKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DedupKey",
                table: "notification_messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_messages_TenantId_RecipientUserId_DedupKey",
                table: "notification_messages",
                columns: new[] { "TenantId", "RecipientUserId", "DedupKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notification_messages_TenantId_RecipientUserId_DedupKey",
                table: "notification_messages");

            migrationBuilder.DropColumn(
                name: "DedupKey",
                table: "notification_messages");
        }
    }
}
