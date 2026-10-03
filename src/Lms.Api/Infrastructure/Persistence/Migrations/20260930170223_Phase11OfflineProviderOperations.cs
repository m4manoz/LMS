using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lms.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase11OfflineProviderOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WebhookSecretProtected",
                table: "virtual_labs",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxAwardsPerHour",
                table: "gamification_settings",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.CreateTable(
                name: "gamification_abuse_reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Signal = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    WindowStartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gamification_abuse_reviews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "offline_devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    FingerprintHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SecretHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MaxActivePackages = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offline_devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "product_telemetry_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OfflineDeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PropertiesJson = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_telemetry_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "virtual_lab_health_checks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    VirtualLabId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LatencyMilliseconds = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CheckedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_virtual_lab_health_checks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_virtual_lab_health_checks_virtual_labs_VirtualLabId",
                        column: x => x.VirtualLabId,
                        principalTable: "virtual_labs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "virtual_lab_webhook_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    VirtualLabId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalEventId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    PayloadJson = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: false),
                    Signature = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_virtual_lab_webhook_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_virtual_lab_webhook_events_virtual_labs_VirtualLabId",
                        column: x => x.VirtualLabId,
                        principalTable: "virtual_labs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "offline_package_licenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OfflineDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DownloadCount = table.Column<int>(type: "integer", nullable: false),
                    LastDownloadedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offline_package_licenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_offline_package_licenses_courses_CourseId",
                        column: x => x.CourseId,
                        principalTable: "courses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_offline_package_licenses_offline_devices_OfflineDeviceId",
                        column: x => x.OfflineDeviceId,
                        principalTable: "offline_devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "offline_sync_conflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OfflineDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CourseId = table.Column<Guid>(type: "uuid", nullable: false),
                    LessonId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientOccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ServerUpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClientPositionSeconds = table.Column<int>(type: "integer", nullable: false),
                    ClientStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ServerPositionSeconds = table.Column<int>(type: "integer", nullable: false),
                    ServerStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offline_sync_conflicts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_offline_sync_conflicts_offline_devices_OfflineDeviceId",
                        column: x => x.OfflineDeviceId,
                        principalTable: "offline_devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_gamification_abuse_reviews_TenantId_UserId_Status_WindowSta~",
                table: "gamification_abuse_reviews",
                columns: new[] { "TenantId", "UserId", "Status", "WindowStartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_offline_devices_TenantId_UserId_FingerprintHash",
                table: "offline_devices",
                columns: new[] { "TenantId", "UserId", "FingerprintHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offline_devices_TenantId_UserId_Status",
                table: "offline_devices",
                columns: new[] { "TenantId", "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_offline_package_licenses_CourseId",
                table: "offline_package_licenses",
                column: "CourseId");

            migrationBuilder.CreateIndex(
                name: "IX_offline_package_licenses_OfflineDeviceId",
                table: "offline_package_licenses",
                column: "OfflineDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_offline_package_licenses_TenantId_OfflineDeviceId_CourseId",
                table: "offline_package_licenses",
                columns: new[] { "TenantId", "OfflineDeviceId", "CourseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_offline_package_licenses_TenantId_UserId_ExpiresAtUtc",
                table: "offline_package_licenses",
                columns: new[] { "TenantId", "UserId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_offline_sync_conflicts_OfflineDeviceId",
                table: "offline_sync_conflicts",
                column: "OfflineDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_offline_sync_conflicts_TenantId_UserId_Status_CreatedAtUtc",
                table: "offline_sync_conflicts",
                columns: new[] { "TenantId", "UserId", "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_product_telemetry_events_TenantId_Name_OccurredAtUtc",
                table: "product_telemetry_events",
                columns: new[] { "TenantId", "Name", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_virtual_lab_health_checks_TenantId_VirtualLabId_CheckedAtUtc",
                table: "virtual_lab_health_checks",
                columns: new[] { "TenantId", "VirtualLabId", "CheckedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_virtual_lab_health_checks_VirtualLabId",
                table: "virtual_lab_health_checks",
                column: "VirtualLabId");

            migrationBuilder.CreateIndex(
                name: "IX_virtual_lab_webhook_events_TenantId_VirtualLabId_ExternalEv~",
                table: "virtual_lab_webhook_events",
                columns: new[] { "TenantId", "VirtualLabId", "ExternalEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_virtual_lab_webhook_events_VirtualLabId",
                table: "virtual_lab_webhook_events",
                column: "VirtualLabId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gamification_abuse_reviews");

            migrationBuilder.DropTable(
                name: "offline_package_licenses");

            migrationBuilder.DropTable(
                name: "offline_sync_conflicts");

            migrationBuilder.DropTable(
                name: "product_telemetry_events");

            migrationBuilder.DropTable(
                name: "virtual_lab_health_checks");

            migrationBuilder.DropTable(
                name: "virtual_lab_webhook_events");

            migrationBuilder.DropTable(
                name: "offline_devices");

            migrationBuilder.DropColumn(
                name: "WebhookSecretProtected",
                table: "virtual_labs");

            migrationBuilder.DropColumn(
                name: "MaxAwardsPerHour",
                table: "gamification_settings");
        }
    }
}
