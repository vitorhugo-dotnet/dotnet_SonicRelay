using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SonicRelay.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRelayDeviceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "relay_device_settings",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelayMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TurnUris = table.Column<string[]>(type: "text[]", nullable: false),
                    TurnUsername = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TurnCredential = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_relay_device_settings", x => x.DeviceId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "relay_device_settings");
        }
    }
}
