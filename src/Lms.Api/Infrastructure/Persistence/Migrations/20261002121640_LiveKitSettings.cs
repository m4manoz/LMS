using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lms.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LiveKitSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LiveKitApiKey",
                table: "live_class_settings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LiveKitSecretProtected",
                table: "live_class_settings",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LiveKitSecretReference",
                table: "live_class_settings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LiveKitUrl",
                table: "live_class_settings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LiveKitApiKey",
                table: "live_class_settings");

            migrationBuilder.DropColumn(
                name: "LiveKitSecretProtected",
                table: "live_class_settings");

            migrationBuilder.DropColumn(
                name: "LiveKitSecretReference",
                table: "live_class_settings");

            migrationBuilder.DropColumn(
                name: "LiveKitUrl",
                table: "live_class_settings");
        }
    }
}
