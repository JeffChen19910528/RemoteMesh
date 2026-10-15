using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteDesktop.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetDeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventType = table.Column<int>(type: "integer", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Platform = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.DeviceId);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterShortId = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ConnectionMode = table.Column<int>(type: "integer", nullable: false),
                    PausedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Permissions = table.Column<int>(type: "integer", nullable: false),
                    Epoch = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "SessionTokens",
                columns: table => new
                {
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Expires = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionTokens", x => x.TokenHash);
                });

            migrationBuilder.CreateTable(
                name: "ShortIds",
                columns: table => new
                {
                    ShortId = table.Column<string>(type: "character(9)", fixedLength: true, maxLength: 9, nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShortIds", x => x.ShortId);
                });

            migrationBuilder.CreateTable(
                name: "TrustedDevices",
                columns: table => new
                {
                    OwnerDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrustedDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrustedDeviceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TrustedSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DefaultPermissions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrustedDevices", x => new { x.OwnerDeviceId, x.TrustedDeviceId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_ActorDeviceId",
                table: "AuditLogs",
                column: "ActorDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_TargetDeviceId",
                table: "AuditLogs",
                column: "TargetDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Timestamp",
                table: "AuditLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_RequesterDeviceId",
                table: "Sessions",
                column: "RequesterDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_State",
                table: "Sessions",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TargetDeviceId",
                table: "Sessions",
                column: "TargetDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_SessionTokens_Expires",
                table: "SessionTokens",
                column: "Expires");

            migrationBuilder.CreateIndex(
                name: "IX_SessionTokens_SessionId",
                table: "SessionTokens",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ShortIds_DeviceId",
                table: "ShortIds",
                column: "DeviceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "SessionTokens");

            migrationBuilder.DropTable(
                name: "ShortIds");

            migrationBuilder.DropTable(
                name: "TrustedDevices");
        }
    }
}
