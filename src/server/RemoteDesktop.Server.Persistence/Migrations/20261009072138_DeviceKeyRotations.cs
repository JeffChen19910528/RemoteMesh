using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RemoteDesktop.Server.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceKeyRotations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceKeyRotations",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    OldKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    NewKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    OldKeySignature = table.Column<byte[]>(type: "bytea", nullable: false),
                    NewKeySignature = table.Column<byte[]>(type: "bytea", nullable: false),
                    RotatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceKeyRotations", x => new { x.DeviceId, x.Sequence });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceKeyRotations");
        }
    }
}
